using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

public sealed class MapManagementService(
    ISqlSugarClient database,
    IMapValidationService validator,
    IMapImportService importer,
    ICadUnderlayJobQueue underlayJobs,
    IMapCalibrationService calibration,
    IActiveMapRuntime activeMap,
    TrafficResourceCatalog resourceCatalog,
    MapAssetStorageOptions storage,
    IMapExecutionGate executionGate,
    IMapSwitchService mapSwitches, IBusinessRunInterlock? businessRun=null) : IMapManagementService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<MapOperationResult> ArchiveAsync(TrafficUserContext user, ArchiveMapRequest request, CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        using var control=await executionGate.EnterControlAsync(cancellationToken);
        var project=await database.Queryable<MapProjectEntity>().Where(p=>p.MapId==request.MapId).FirstAsync(cancellationToken);
        if(project is null)return new(false,"地图项目不存在。");
        if(project.IsActive||activeMap.Current?.MapId==request.MapId)return new(false,"当前运行地图不能移除。");
        if(await database.Queryable<MapSwitchEntity>().Where(r=>r.Phase<(int)MapSwitchPhase.Completed &&
            (r.MapId==request.MapId||r.PreviousMapId==request.MapId)).AnyAsync(cancellationToken))
            return new(false,"地图仍关联未结束的切换，请先按原记录处理切换。");
        var draft=await database.Queryable<MapVersionEntity>().Where(v=>v.MapId==request.MapId&&v.Status==(int)MapVersionStatus.Draft).FirstAsync(cancellationToken);
        if(draft is null||draft.EditToken!=request.ExpectedEditToken)return new(false,"草稿已变化，请重新读取后移除。");
        await database.Updateable<MapProjectEntity>().SetColumns(p=>new MapProjectEntity{IsArchived=request.Archived,UpdatedAt=AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime})
            .Where(p=>p.MapId==request.MapId&&!p.IsActive).ExecuteCommandAsync(cancellationToken);
        await AuditAsync(user,request.MapId,draft.VersionId,request.Archived?"ArchiveMap":"RestoreMap",true,"从目录移除可恢复；保留版本与切换历史。",cancellationToken);
        return new(true,request.Archived?"已从地图目录移除，版本与历史保留可恢复。":"地图已恢复到目录。");
    }

    public async Task<IReadOnlyList<MapOperationAuditSnapshot>> GetAuditsAsync(
        TrafficUserContext user,
        string mapId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        var rows = await database.Queryable<MapOperationAuditEntity>()
            .Where(item => item.MapId == mapId)
            .OrderBy(item => item.OccurredAt, OrderByType.Desc)
            .Take(Math.Clamp(maximumCount, 1, 1000))
            .ToListAsync(cancellationToken);
        return rows.Select(item => new MapOperationAuditSnapshot(
            item.AuditId, item.MapId, item.VersionId, item.Operation, item.Success,
            item.OperatorIdentity, item.Summary, ToOffset(item.OccurredAt))).ToArray();
    }

    public async Task<IReadOnlyList<MapVersionHistoryItem>> GetVersionHistoryAsync(
        TrafficUserContext user,
        string mapId,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        var rows = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == mapId)
            .OrderBy(item => item.VersionNumber, OrderByType.Desc)
            .ToListAsync(cancellationToken);
        return rows.Select(row =>
        {
            var definition = Deserialize<MapDefinition>(row.DefinitionJson);
            var report = Deserialize<MapValidationReport>(row.ValidationJson);
            return new MapVersionHistoryItem(
                row.VersionId, row.MapId, row.VersionNumber, (MapVersionStatus)row.Status,
                definition.Nodes.Count, definition.Edges.Count, definition.Resources.Count,
                report.ErrorCount, report.WarningCount, row.CreatedBy, ToOffset(row.UpdatedAt),
                row.PublishedAt.HasValue ? ToOffset(row.PublishedAt.Value) : null);
        }).ToArray();
    }

    public async Task<MapVersionSnapshot?> GetVersionAsync(
        TrafficUserContext user,
        string mapId,
        string versionId,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        var row = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == mapId && item.VersionId == versionId)
            .FirstAsync(cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<MapImportPreviewResult> PreviewImportAsync(
        TrafficUserContext user,
        PreviewMapImportRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var definition = importer.Import(new MapImportRequest(
                request.Path, request.Format, request.CoordinateScale));
            if (request.RuntimeRoles is not null)
            {
                if (request.Format != MapImportFormat.RobokitSmap || !request.RobotCoordinatesConfirmed ||
                    definition.RobokitSource?.Sha256 != request.ExpectedSourceSha256)
                    throw new InvalidDataException("须确认原图摘要及车端坐标，才能准备运行图。");
                var bytes = await File.ReadAllBytesAsync(request.Path, cancellationToken);
                var parsed = new Smap2DImportService().Read(bytes, "");
                definition = RobokitPathEvidenceReader.Attach(bytes, bytes,
                    Smap2DImportService.PrepareRobotWorldRuntime(parsed, request.RuntimeRoles));
                if (!definition.Nodes.Any(n => n.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint))
                    throw new InvalidDataException("至少需要一个停靠站点或等待位；不能全部设置为途经航点。");
            }
            var report = validator.Validate(definition);
            var result = new MapImportPreviewResult(
                true,
                $"导入预览完成：节点{report.NodeCount}、路段{report.EdgeCount}、资源{report.ResourceCount}；错误{report.ErrorCount}、警告{report.WarningCount}。尚未写入草稿。",
                definition,
                report);
            await AuditAsync(user, "IMPORT-PREVIEW", null, "PreviewImport", true,
                $"{Path.GetFileName(request.Path)}；节点{report.NodeCount}，路段{report.EdgeCount}，错误{report.ErrorCount}", cancellationToken);
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or NotSupportedException or ArgumentException)
        {
            await AuditAsync(user, "IMPORT-PREVIEW", null, "PreviewImport", false,
                $"{Path.GetFileName(request.Path)}；{exception.Message}", cancellationToken);
            return new MapImportPreviewResult(false, $"地图导入失败：{exception.Message}");
        }
    }

    public async Task<MapExportResult> ExportAsync(
        TrafficUserContext user,
        ExportMapRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        var project = await database.Queryable<MapProjectEntity>()
            .Where(item => item.MapId == request.MapId)
            .FirstAsync(cancellationToken);
        if (project is null || project.IsArchived) return new(false, "地图项目不存在或已移除。");
        var status = request.Version == MapExportVersion.Published
            ? MapVersionStatus.Published
            : MapVersionStatus.Draft;
        var version = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == request.MapId && item.Status == (int)status)
            .OrderByDescending(item => item.VersionNumber)
            .FirstAsync(cancellationToken);
        if (version is null) return new(false, $"地图不存在可导出的{status}版本。");

        var exportedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
        var envelope = new MapBackupEnvelope(
            "agv-dispatch-map-backup/v1",
            exportedAt,
            user.WindowsIdentity,
            ToSummary(project),
            ToSnapshot(version));
        var json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true
        });
        var bytes = Encoding.UTF8.GetBytes(json);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Directory.CreateDirectory(storage.BackupPath);
        var fileName = $"{SafeFileSegment(project.Name)}-V{version.VersionNumber}-{status}-{exportedAt:yyyyMMdd-HHmmss}-{sha256[..8]}.json";
        var path = Path.Combine(storage.BackupPath, fileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken);
        File.Move(temporaryPath, path, true);
        await File.WriteAllTextAsync(path + ".sha256", $"{sha256}  {fileName}{Environment.NewLine}", Encoding.ASCII, cancellationToken);
        await AuditAsync(user, request.MapId, version.VersionId, "Export", true,
            $"{status} V{version.VersionNumber}；{fileName}；SHA256={sha256}", cancellationToken);
        return new(true, $"地图{status} V{version.VersionNumber}已导出并生成SHA-256校验文件。", path, sha256, bytes.LongLength);
    }

    public async Task<IReadOnlyList<MapProjectSummary>> GetMapsAsync(
        TrafficUserContext user,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        var rows = await database.Queryable<MapProjectEntity>()
            .Where(item => !item.IsArchived)
            .OrderByDescending(item => item.UpdatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(ToSummary).ToArray();
    }

    public async Task<MapWorkspaceSnapshot?> GetWorkspaceAsync(
        TrafficUserContext user,
        string mapId,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.View);
        if (string.IsNullOrWhiteSpace(mapId)) return null;
        var project = await database.Queryable<MapProjectEntity>()
            .Where(item => item.MapId == mapId)
            .FirstAsync(cancellationToken);
        if (project is null) return null;
        var draft = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == mapId && item.Status == (int)MapVersionStatus.Draft)
            .FirstAsync(cancellationToken);
        if (draft is null)
        {
            throw new InvalidOperationException($"地图{mapId}缺少可编辑草稿版本。");
        }
        MapVersionEntity? published = null;
        if (!string.IsNullOrWhiteSpace(project.PublishedVersionId))
        {
            published = await database.Queryable<MapVersionEntity>()
                .Where(item => item.VersionId == project.PublishedVersionId)
                .FirstAsync(cancellationToken);
        }
        return new MapWorkspaceSnapshot(
            ToSummary(project), ToSnapshot(draft), published is null ? null : ToSnapshot(published));
    }

    public async Task<MapOperationResult> CreateAsync(
        TrafficUserContext user,
        CreateMapRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        var error = ValidateMetadata(request.Name, request.CoordinateSystem, request.Unit, request.CoordinateScale);
        if (error is not null) return new(false, error);
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        var mapId = $"MAP-{Guid.NewGuid():N}";
        var versionId = $"MAPV-{Guid.NewGuid():N}";
        var definition = new MapDefinition(
            request.Name.Trim(), request.BackgroundFilePath.Trim(), request.CoordinateSystem.Trim(),
            request.Unit.Trim(), [], [], []);
        var report = validator.Validate(definition);
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await database.Insertable(new MapProjectEntity
            {
                MapId = mapId,
                Name = Truncate(request.Name.Trim(), 128),
                Description = Truncate(request.Description.Trim(), 1000),
                BackgroundFilePath = Truncate(request.BackgroundFilePath.Trim(), 1000),
                BackgroundFormat = DetectBackgroundFormat(request.BackgroundFilePath),
                CoordinateSystem = Truncate(request.CoordinateSystem.Trim(), 64),
                Unit = Truncate(request.Unit.Trim(), 16),
                CoordinateScale = request.CoordinateScale,
                PreviewPngPath = string.Empty,
                PreviewSvgPath = string.Empty,
                CadLayersJson = "[]",
                CadViewBoxJson = null,
                UnderlayStatus = (int)CadUnderlayJobStatus.None,
                UnderlayMessage = string.Empty,
                UnderlayIncludedLayersJson = "[]",
                CreatedAt = now,
                UpdatedAt = now
            }).ExecuteCommandAsync(cancellationToken);
            await database.Insertable(new MapVersionEntity
            {
                VersionId = versionId,
                MapId = mapId,
                VersionNumber = 1,
                Status = (int)MapVersionStatus.Draft,
                DefinitionJson = Serialize(definition),
                ValidationJson = Serialize(report),
                CreatedBy = Truncate(user.WindowsIdentity, 256),
                CreatedAt = now,
                UpdatedAt = now,
                EditToken = Guid.NewGuid().ToString("N")
            }).ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException("创建地图事务失败，地图名称可能已存在。", transaction.ErrorException);
        }
        var workspace = await GetWorkspaceAsync(user, mapId, cancellationToken);
        await AuditAsync(user, mapId, versionId, "Create", true,
            $"创建地图{request.Name.Trim()}草稿V1", cancellationToken);
        return new(true, $"地图{request.Name.Trim()}已创建，当前为草稿V1。", workspace);
    }

    public async Task<MapOperationResult> SaveDraftAsync(
        TrafficUserContext user,
        SaveMapDraftRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        var error = ValidateMetadata(request.Name, request.CoordinateSystem, request.Unit, request.CoordinateScale);
        if (error is not null) return new(false, error);
        ArgumentNullException.ThrowIfNull(request.Definition);
        var normalized = request.Definition with
        {
            Name = request.Name.Trim(),
            Source = request.BackgroundFilePath.Trim(),
            CoordinateSystem = request.CoordinateSystem.Trim(),
            Unit = request.Unit.Trim()
        };
        var report = validator.Validate(normalized);
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        var currentDraft = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == request.MapId && item.VersionId == request.VersionId &&
                           item.Status == (int)MapVersionStatus.Draft)
            .FirstAsync(cancellationToken);
        if (currentDraft is null) return new(false, "地图草稿不存在或已经发布。");
        if (!string.Equals(currentDraft.EditToken, request.ExpectedEditToken, StringComparison.Ordinal))
        {
            await AuditAsync(user, request.MapId, request.VersionId, "SaveDraft", false,
                "编辑令牌过期，拒绝覆盖其他会话修改", cancellationToken);
            return new(false, "草稿已被其他会话修改。为防止覆盖他人修改，本次保存已拒绝；请刷新地图后重新编辑。");
        }
        var nextEditToken = Guid.NewGuid().ToString("N");
        var currentProject = await database.Queryable<MapProjectEntity>()
            .Where(item => item.MapId == request.MapId)
            .FirstAsync(cancellationToken);
        if (currentProject is null || currentProject.IsArchived) return new(false, "地图项目不存在或已移除。");
        var sourceChanged = !string.Equals(
            currentProject.BackgroundFilePath,
            request.BackgroundFilePath.Trim(),
            StringComparison.OrdinalIgnoreCase);
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            var projectUpdated = await database.Updateable<MapProjectEntity>()
                .SetColumns(item => new MapProjectEntity
                {
                    Name = Truncate(request.Name.Trim(), 128),
                    Description = Truncate(request.Description.Trim(), 1000),
                    BackgroundFilePath = Truncate(request.BackgroundFilePath.Trim(), 1000),
                    BackgroundFormat = DetectBackgroundFormat(request.BackgroundFilePath),
                    CoordinateSystem = Truncate(request.CoordinateSystem.Trim(), 64),
                    Unit = Truncate(request.Unit.Trim(), 16),
                    CoordinateScale = request.CoordinateScale,
                    PreviewPngPath = sourceChanged ? string.Empty : currentProject.PreviewPngPath,
                    PreviewSvgPath = sourceChanged ? string.Empty : currentProject.PreviewSvgPath,
                    CadLayersJson = sourceChanged ? "[]" : currentProject.CadLayersJson,
                    CadViewBoxJson = sourceChanged ? null : currentProject.CadViewBoxJson,
                    CalibrationJson = sourceChanged ? null : currentProject.CalibrationJson,
                    UnderlayStatus = sourceChanged ? (int)CadUnderlayJobStatus.None : currentProject.UnderlayStatus,
                    UnderlayMessage = sourceChanged ? string.Empty : currentProject.UnderlayMessage,
                    UnderlayIncludedLayersJson = sourceChanged ? "[]" : currentProject.UnderlayIncludedLayersJson,
                    UnderlayGeneratedAt = sourceChanged ? null : currentProject.UnderlayGeneratedAt,
                    UpdatedAt = now
                })
                .Where(item => item.MapId == request.MapId)
                .ExecuteCommandAsync(cancellationToken);
            var draftUpdated = await database.Updateable<MapVersionEntity>()
                .SetColumns(item => new MapVersionEntity
                {
                    DefinitionJson = Serialize(normalized),
                    ValidationJson = Serialize(report),
                    UpdatedAt = now,
                    EditToken = nextEditToken
                })
                .Where(item => item.VersionId == request.VersionId && item.MapId == request.MapId &&
                               item.Status == (int)MapVersionStatus.Draft &&
                               item.EditToken == request.ExpectedEditToken)
                .ExecuteCommandAsync(cancellationToken);
            if (projectUpdated != 1 || draftUpdated != 1)
            {
                throw new MapDraftConcurrencyException("地图草稿发生并发修改，保存已回滚。");
            }
        });
        if (!transaction.IsSuccess)
        {
            if (transaction.ErrorException is MapDraftConcurrencyException)
                return new(false, "草稿已被其他会话修改。为防止覆盖他人修改，本次保存已拒绝；请刷新地图后重新编辑。");
            throw new InvalidOperationException("保存地图草稿事务失败。", transaction.ErrorException);
        }
        var workspace = await GetWorkspaceAsync(user, request.MapId, cancellationToken);
        await AuditAsync(user, request.MapId, request.VersionId, "SaveDraft", true,
            $"节点{report.NodeCount}，路段{report.EdgeCount}，资源{report.ResourceCount}；错误{report.ErrorCount}，警告{report.WarningCount}",
            cancellationToken);
        return new(true,
            $"草稿已保存：节点{report.NodeCount}、路段{report.EdgeCount}、资源{report.ResourceCount}；错误{report.ErrorCount}、警告{report.WarningCount}。",
            workspace);
    }

    public async Task<MapOperationResult> PublishAsync(
        TrafficUserContext user,
        PublishMapRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        if(await database.Queryable<MapProjectEntity>().Where(p=>p.MapId==request.MapId&&p.IsArchived).AnyAsync(cancellationToken))return new(false,"地图已移除，请先恢复。");
        var draft = await database.Queryable<MapVersionEntity>()
            .Where(item => item.MapId == request.MapId && item.VersionId == request.VersionId &&
                           item.Status == (int)MapVersionStatus.Draft)
            .FirstAsync(cancellationToken);
        if (draft is null) return new(false, "待发布草稿不存在或已经发布。");
        if (request.ExpectedEditToken is not null && request.ExpectedEditToken != draft.EditToken)
            return new(false, "草稿已被修改，请重新读取并核对后发布。");
        var definition = Deserialize<MapDefinition>(draft.DefinitionJson);
        var report = validator.Validate(definition);
        if (!report.IsValid)
        {
            return new(false, $"地图存在{report.ErrorCount}项阻断错误，不能发布。首项：{report.Issues.First(item => item.Severity == MapValidationSeverity.Error).Message}");
        }

        var unmappedStations = definition.Nodes
            .Where(node => node.Enabled &&
                           node.Type is MapNodeType.Station or MapNodeType.WaitingPoint or MapNodeType.ChargingPoint &&
                           string.IsNullOrWhiteSpace(node.AgvSiteId))
            .Select(node => node.Id)
            .ToArray();
        if (unmappedStations.Length > 0)
        {
            return new(false,
                $"地图存在未绑定Robokit站点ID的运行站点，不能发布：{string.Join(',', unmappedStations.Take(10))}。");
        }

        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        var nextVersionId = $"MAPV-{Guid.NewGuid():N}";
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            await database.Updateable<MapVersionEntity>()
                .SetColumns(item => new MapVersionEntity
                {
                    Status = (int)MapVersionStatus.Archived,
                    UpdatedAt = now
                })
                .Where(item => item.MapId == request.MapId &&
                               item.Status == (int)MapVersionStatus.Published)
                .ExecuteCommandAsync(cancellationToken);
            var published = await database.Updateable<MapVersionEntity>()
                .SetColumns(item => new MapVersionEntity
                {
                    Status = (int)MapVersionStatus.Published,
                    ValidationJson = Serialize(report),
                    PublishedAt = now,
                    UpdatedAt = now
                })
                .Where(item => item.VersionId == request.VersionId &&
                               item.EditToken == draft.EditToken &&
                               item.Status == (int)MapVersionStatus.Draft)
                .ExecuteCommandAsync(cancellationToken);
            if (published != 1)
            {
                throw new InvalidOperationException("草稿已被并发修改，发布已回滚。");
            }
            await database.Insertable(new MapVersionEntity
            {
                VersionId = nextVersionId,
                MapId = request.MapId,
                VersionNumber = draft.VersionNumber + 1,
                Status = (int)MapVersionStatus.Draft,
                DefinitionJson = Serialize(definition),
                ValidationJson = Serialize(report),
                CreatedBy = Truncate(user.WindowsIdentity, 256),
                CreatedAt = now,
                UpdatedAt = now,
                EditToken = Guid.NewGuid().ToString("N")
            }).ExecuteCommandAsync(cancellationToken);
            await database.Updateable<MapProjectEntity>()
                .SetColumns(item => new MapProjectEntity
                {
                    PublishedVersionId = draft.VersionId,
                    UpdatedAt = now
                })
                .Where(item => item.MapId == request.MapId)
                .ExecuteCommandAsync(cancellationToken);
        });
        if (!transaction.IsSuccess)
        {
            throw new InvalidOperationException("发布地图事务失败。", transaction.ErrorException);
        }
        var workspace = await GetWorkspaceAsync(user, request.MapId, cancellationToken);
        await AuditAsync(user, request.MapId, draft.VersionId, "Publish", true,
            $"发布V{draft.VersionNumber}并创建草稿V{draft.VersionNumber + 1}", cancellationToken);
        return new(true, $"地图V{draft.VersionNumber}已发布，并创建下一草稿V{draft.VersionNumber + 1}。", workspace);
    }

    public async Task<MapOperationResult> GenerateUnderlayAsync(
        TrafficUserContext user,
        GenerateMapUnderlayRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        if (request.Width < 320 || request.Width > 8192 || request.Height < 240 || request.Height > 8192 ||
            (long)request.Width * request.Height > 32000000)
            return new(false, "底图分辨率超出限制（最大3200万像素）。");
        var project = await database.Queryable<MapProjectEntity>()
            .Where(item => item.MapId == request.MapId)
            .FirstAsync(cancellationToken);
        if (project is null || project.IsArchived) return new(false, "地图项目不存在或已移除。");
        var sourcePath = string.IsNullOrWhiteSpace(request.SourcePath)
            ? project.BackgroundFilePath
            : request.SourcePath.Trim();
        if (!File.Exists(sourcePath)) return new(false, "CAD底图文件不存在。");
        if ((CadUnderlayJobStatus)project.UnderlayStatus is CadUnderlayJobStatus.Pending or CadUnderlayJobStatus.Processing)
            return new(false, "该地图已有CAD底图任务正在执行，请等待完成。");
        var sourceChanged = !string.Equals(project.BackgroundFilePath, sourcePath, StringComparison.OrdinalIgnoreCase);
        var includedLayers = sourceChanged ? Array.Empty<string>() : request.IncludedLayers ?? [];
        await database.Updateable<MapProjectEntity>()
            .SetColumns(item => new MapProjectEntity
            {
                BackgroundFilePath = Truncate(sourcePath, 1000),
                BackgroundFormat = DetectBackgroundFormat(sourcePath),
                PreviewPngPath = sourceChanged ? string.Empty : project.PreviewPngPath,
                PreviewSvgPath = sourceChanged ? string.Empty : project.PreviewSvgPath,
                CadLayersJson = sourceChanged ? "[]" : project.CadLayersJson,
                CadViewBoxJson = sourceChanged ? null : project.CadViewBoxJson,
                CalibrationJson = sourceChanged ? null : project.CalibrationJson,
                UnderlayGeneratedAt = sourceChanged ? null : project.UnderlayGeneratedAt,
                UnderlayStatus = (int)CadUnderlayJobStatus.Pending,
                UnderlayMessage = "CAD底图已进入后台队列。",
                UnderlayIncludedLayersJson = Serialize(includedLayers),
                UnderlayWidth = request.Width,
                UnderlayHeight = request.Height,
                UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
            })
            .Where(item => item.MapId == request.MapId)
            .ExecuteCommandAsync(cancellationToken);
        await underlayJobs.QueueAsync(
            new CadUnderlayJob(request.MapId, sourcePath, includedLayers, request.Width, request.Height),
            cancellationToken);
        var workspace = await GetWorkspaceAsync(user, request.MapId, cancellationToken);
        await AuditAsync(user, request.MapId, workspace?.Draft.VersionId, "GenerateUnderlay", true,
            $"源文件={Path.GetFileName(sourcePath)}；图层{includedLayers.Count}", cancellationToken);
        return new(true, "CAD底图已进入后台转换队列，页面和其他诊断功能不会被阻塞。", workspace);
    }

    public async Task<MapOperationResult> SaveCalibrationAsync(
        TrafficUserContext user,
        SaveMapCalibrationRequest request,
        CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        var result = calibration.Calculate(request.ControlPoints);
        var updated = await database.Updateable<MapProjectEntity>()
            .SetColumns(item => new MapProjectEntity
            {
                CalibrationJson = Serialize(result),
                CoordinateScale = result.Scale,
                UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
            })
            .Where(item => item.MapId == request.MapId)
            .ExecuteCommandAsync(cancellationToken);
        if (updated != 1) return new(false, "地图项目不存在。");
        var workspace = await GetWorkspaceAsync(user, request.MapId, cancellationToken);
        await AuditAsync(user, request.MapId, workspace?.Draft.VersionId, "SaveCalibration", true,
            $"控制点{request.ControlPoints.Count}；RMS={result.RootMeanSquareError:G6}", cancellationToken);
        return new(true,
            $"坐标标定已保存：比例{result.Scale:G8}、旋转{result.RotationDegrees:F4}°、RMS误差{result.RootMeanSquareError:G6}。",
            workspace);
    }

    public async Task<MapOperationResult> ActivateAsync(
        TrafficUserContext user, ActivateMapRequest request, CancellationToken cancellationToken)
    {
        Demand(user, TrafficPermission.ManageMap);
        using var control = await executionGate.EnterControlAsync(cancellationToken);
        if (businessRun?.Active == true) return new(false, "请先停止业务运行并完成待机收尾；原运行待核对时不能激活地图。");
        var project = await database.Queryable<MapProjectEntity>().Where(item => item.MapId == request.MapId)
            .FirstAsync(cancellationToken);
        if (project is null || project.IsArchived) return new(false, "地图项目不存在或已移除。");
        if (request.ConfirmationText?.Trim() != $"ACTIVATE {project.Name}")
            return new(false, $"激活确认文本不正确，请输入：ACTIVATE {project.Name}");
        var release = await database.Queryable<MapSwitchEntity>()
            .Where(item => item.Phase < (int)MapSwitchPhase.Completed).FirstAsync(cancellationToken);
        if (release is null || release.MapId != request.MapId || release.VersionId != request.VersionId ||
            release.Phase != (int)MapSwitchPhase.Draining)
            return new(false, "请先开始该版本的安全切换并等待旧任务收尾。");
        if (request.ExpectedReleaseId is not null && (string.IsNullOrWhiteSpace(request.ExpectedReleaseId) ||
            request.ExpectedReleaseId.Length > 64 || release.ReleaseId != request.ExpectedReleaseId))
            return new(false, "原切换编号不一致，禁止将旧激活请求应用到其他切换。");
        if (!request.SiteSafetyConfirmed || !request.VehicleMapCompatibilityConfirmed ||
            string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 1500)
            return new(false, "请确认现场安全和车辆地图兼容，并填写1至1500字的核对依据。");
        var version = await database.Queryable<MapVersionEntity>().Where(item =>
            item.MapId == request.MapId && item.VersionId == request.VersionId &&
            item.Status == (int)MapVersionStatus.Published).FirstAsync(cancellationToken);
        if (version is null || project.PublishedVersionId != request.VersionId)
            return new(false, "发布指针已变化，请取消此次切换后重新开始。");
        var definition = Deserialize<MapDefinition>(version.DefinitionJson);
        if (!validator.Validate(definition).IsValid) return new(false, "已发布版本校验失败。");
        var previous = await database.Queryable<MapProjectEntity>().Where(item => item.IsActive).FirstAsync(cancellationToken);
        MapDefinition? previousDefinition = null;
        if (previous?.ActiveVersionId is { } previousVersionId)
        {
            var previousVersion = await database.Queryable<MapVersionEntity>()
                .Where(item => item.MapId == previous.MapId && item.VersionId == previousVersionId)
                .FirstAsync(cancellationToken);
            if (previousVersion is not null) previousDefinition = Deserialize<MapDefinition>(previousVersion.DefinitionJson);
        }
        var compatibleGeometryUpdate = request.StoppedOffStationGeometryUpdateConfirmed &&
            previous?.MapId == request.MapId && previousDefinition is not null &&
            SameRuntimeTopology(previousDefinition, definition);
        if (request.StoppedOffStationGeometryUpdateConfirmed && !compatibleGeometryUpdate)
            return new(false, "同图几何更新确认只适用于节点、道路端点和站点角色均未改变的当前地图版本。");
        // Construct the complete snapshot before committing, so graph construction cannot fail after the DB write.
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime;
        var snapshot = new ActiveMapRuntimeSnapshot(request.MapId, request.VersionId, definition, new DateTimeOffset(now));
        var blockers = await mapSwitches.GetBlockersAsync(definition, cancellationToken,
            requireStoppedStation: !compatibleGeometryUpdate);
        if (blockers.Count > 0) return new(false, "禁止切换：" + string.Join("；", blockers));
        if (previous?.MapId != release.PreviousMapId || previous?.ActiveVersionId != release.PreviousVersionId ||
            activeMap.Current?.MapId != release.PreviousMapId || activeMap.Current?.VersionId != release.PreviousVersionId)
            return new(false, "原地图指针与切换记录不一致，禁止切换。");
        // Materialize the response before committing. Missing/corrupt workspace metadata must
        // not turn a committed activation into an apparent failure while building its reply.
        MapWorkspaceSnapshot responseWorkspace;
        try
        {
            var workspace = await GetWorkspaceAsync(user, request.MapId, cancellationToken)
                ?? throw new InvalidOperationException("目标地图工作区不存在。");
            responseWorkspace = workspace with
            {
                Project = ToSummary(project) with { IsActive = true, ActiveVersionId = request.VersionId, UpdatedAt = ToOffset(now) },
                Published = ToSnapshot(version)
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException or InvalidDataException)
        {
            return new(false, "目标地图工作区不完整，未激活：" + exception.Message);
        }
        executionGate.SetMode(MapExecutionMode.Held);
        // Stay held on any exception/ambiguous outcome; the persistent release is reconciled on restart.
        var transaction = await database.Ado.UseTranAsync(async () =>
        {
            // The common gate excludes all task/command/PLC mutations; recheck DB evidence inside the transaction.
            var finalBlockers = await mapSwitches.GetBlockersAsync(definition, cancellationToken,
                requireStoppedStation: !compatibleGeometryUpdate);
            if (finalBlockers.Count > 0) throw new InvalidOperationException(string.Join("；", finalBlockers));
            await database.Updateable<MapProjectEntity>()
                .SetColumns(item => new MapProjectEntity { IsActive = false, ActiveVersionId = null, UpdatedAt = now })
                .Where(item => item.IsActive).ExecuteCommandAsync(cancellationToken);
            var updated = await database.Updateable<MapProjectEntity>()
                .SetColumns(item => new MapProjectEntity { IsActive = true, ActiveVersionId = request.VersionId, UpdatedAt = now })
                .Where(item => item.MapId == request.MapId && item.PublishedVersionId == request.VersionId)
                .ExecuteCommandAsync(cancellationToken);
            if (updated != 1) throw new InvalidOperationException("地图发布指针已变化。");
            await database.Deleteable<TrafficResourceDefinitionEntity>().ExecuteCommandAsync(cancellationToken);
            var runtimeResources = MapCorridorResources.AllResources(definition);
            if (runtimeResources.Count > 0)
                await database.Insertable(runtimeResources.Select(resource => new TrafficResourceDefinitionEntity
                { ResourceId = resource.Id, ResourceType = (int)resource.Type, Capacity = resource.Capacity, UpdatedAt = now })
                .ToArray()).ExecuteCommandAsync(cancellationToken);
            updated = await database.Updateable<MapSwitchEntity>()
                .SetColumns(item => new MapSwitchEntity { Phase = (int)MapSwitchPhase.AwaitingResume,
                    Evidence = user.WindowsIdentity + ": " + request.Evidence.Trim(), UpdatedAt = now })
                .Where(item => item.ReleaseId == release.ReleaseId && item.Phase == (int)MapSwitchPhase.Draining)
                .ExecuteCommandAsync(cancellationToken);
            if (updated != 1) throw new InvalidOperationException("切换记录已变化。");
            await AuditAsync(user, request.MapId, request.VersionId, "Activate", true,
                $"切换{release.ReleaseId}提交V{version.VersionNumber}，等待现场确认放行", cancellationToken);
        });
        if (!transaction.IsSuccess) throw new InvalidOperationException("激活事务失败；保持停止派单，请核对切换记录。", transaction.ErrorException);
        activeMap.Replace(snapshot);
        resourceCatalog.Replace(MapCorridorResources.AllResources(snapshot.Definition));
        return new(true, $"地图V{version.VersionNumber}已激活，派单仍暂停；重新核对现场后点击确认放行。",
            responseWorkspace);
    }

    private static bool SameRuntimeTopology(MapDefinition current, MapDefinition next)
    {
        static string NodeKey(MapNode node) =>
            $"{node.Id}\u001f{node.Enabled}\u001f{node.Type}\u001f{node.AgvSiteId}\u001f{string.Join(',', node.ResourceIds.Order(StringComparer.OrdinalIgnoreCase))}";
        static string EdgeKey(MapEdge edge) =>
            $"{edge.Id}\u001f{edge.Enabled}\u001f{edge.From}\u001f{edge.To}\u001f{edge.Bidirectional}\u001f{string.Join(',', edge.ResourceIds.Order(StringComparer.OrdinalIgnoreCase))}";
        static string ResourceKey(TrafficResourceDefinition resource) =>
            $"{resource.Id}\u001f{resource.Type}\u001f{resource.Capacity}";
        return current.CoordinateSystem == next.CoordinateSystem &&
            current.Unit == next.Unit &&
            current.Nodes.Select(NodeKey).Order(StringComparer.Ordinal)
                .SequenceEqual(next.Nodes.Select(NodeKey).Order(StringComparer.Ordinal)) &&
            current.Edges.Select(EdgeKey).Order(StringComparer.Ordinal)
                .SequenceEqual(next.Edges.Select(EdgeKey).Order(StringComparer.Ordinal)) &&
            current.Resources.Select(ResourceKey).Order(StringComparer.Ordinal)
                .SequenceEqual(next.Resources.Select(ResourceKey).Order(StringComparer.Ordinal));
    }

    private static string? ValidateMetadata(
        string name,
        string coordinateSystem,
        string unit,
        double scale)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2) return "地图名称至少需要2个字符。";
        if (string.IsNullOrWhiteSpace(coordinateSystem)) return "坐标系不能为空。";
        if (string.IsNullOrWhiteSpace(unit)) return "坐标单位不能为空。";
        return !double.IsFinite(scale) || scale <= 0 ? "坐标缩放比例必须是大于0的有限数值。" : null;
    }

    private static MapProjectSummary ToSummary(MapProjectEntity row) => new(
        row.MapId, row.Name, row.Description, row.BackgroundFilePath, row.BackgroundFormat,
        row.CoordinateSystem, row.Unit, row.CoordinateScale,
        row.PreviewPngPath ?? string.Empty,
        row.PreviewSvgPath ?? string.Empty,
        DeserializeOrDefault(row.CadLayersJson, Array.Empty<CadLayerSnapshot>()),
        DeserializeOrDefault<CadViewBoxSnapshot?>(row.CadViewBoxJson, null),
        DeserializeOrDefault<MapCalibrationSnapshot?>(row.CalibrationJson, null),
        (CadUnderlayJobStatus)row.UnderlayStatus,
        row.UnderlayMessage ?? string.Empty,
        row.UnderlayGeneratedAt.HasValue ? ToOffset(row.UnderlayGeneratedAt.Value) : null,
        row.IsActive,
        row.ActiveVersionId,
        row.PublishedVersionId,
        ToOffset(row.UpdatedAt));

    private static MapVersionSnapshot ToSnapshot(MapVersionEntity row) => new(
        row.VersionId, row.MapId, row.VersionNumber, (MapVersionStatus)row.Status,
        Deserialize<MapDefinition>(row.DefinitionJson),
        Deserialize<MapValidationReport>(row.ValidationJson), row.CreatedBy,
        ToOffset(row.CreatedAt), ToOffset(row.UpdatedAt),
        row.PublishedAt.HasValue ? ToOffset(row.PublishedAt.Value) : null,
        row.EditToken ?? string.Empty);

    private static string DetectBackgroundFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "None";
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (extension == "DWG" && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            var header = new byte[Math.Min(6, (int)stream.Length)];
            _ = stream.Read(header);
            var version = Encoding.ASCII.GetString(header);
            return $"DWG-{version}";
        }
        return extension.Length == 0 ? "Unknown" : extension;
    }

    private static string SafeFileSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "map" : result;
    }

    private static void Demand(TrafficUserContext user, TrafficPermission permission)
    {
        if (!user.Has(permission)) throw new UnauthorizedAccessException($"当前软件账号没有{permission}权限。");
    }
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, JsonOptions) ??
        throw new InvalidDataException($"无法反序列化{typeof(T).Name}。");
    private static T DeserializeOrDefault<T>(string? value, T fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        try { return JsonSerializer.Deserialize<T>(value, JsonOptions) ?? fallback; }
        catch (JsonException) { return fallback; }
    }
    private static DateTimeOffset ToOffset(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Local));
    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private Task AuditAsync(
        TrafficUserContext user,
        string mapId,
        string? versionId,
        string operation,
        bool success,
        string summary,
        CancellationToken cancellationToken) => database.Insertable(new MapOperationAuditEntity
    {
        AuditId = $"MAPA-{Guid.NewGuid():N}",
        MapId = Truncate(mapId, 64),
        VersionId = string.IsNullOrWhiteSpace(versionId) ? null : Truncate(versionId, 64),
        Operation = Truncate(operation, 64),
        Success = success,
        OperatorIdentity = Truncate(user.WindowsIdentity, 256),
        Summary = Truncate(summary, 2000),
        OccurredAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
    }).ExecuteCommandAsync(cancellationToken);

    private sealed class MapDraftConcurrencyException(string message) : InvalidOperationException(message);
    private sealed record MapBackupEnvelope(
        string Schema,
        DateTimeOffset ExportedAt,
        string ExportedBy,
        MapProjectSummary Project,
        MapVersionSnapshot Version);
}
