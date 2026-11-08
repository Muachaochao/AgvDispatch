using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;
using System.Text;
using System.Text.Json;

namespace AgvDispatch.WebHost.Engineering;

public static class EngineeringRuntimeApi
{
    public sealed record PreviewRequest(string VersionId);
    public sealed record ApplyRequest(string VersionId, string RequestId, string ExpectedRevision, string ExpectedCommunicationRevision,
        string ExpectedContentHash, string ExpectedMapHash, string ConfirmationText, bool ActionsReviewed,
        bool SiteSafetyConfirmed = false, bool VehicleMapCompatibilityConfirmed = false, string Evidence = "",
        bool StoppedOffStationGeometryUpdateConfirmed = false);
    public sealed record EngineeringApplyResult(bool Accepted, string Message, TaskParameterResult Parameters,
        bool RestartRequired, string CommunicationRevision, string? MapSwitchReleaseId, bool RequiresFinalRelease);

    private static async Task<EngineeringRuntimePreview> Preview(string id, EngineeringStore store, DispatchPipe pipe, CancellationToken ct)
    {
        var version = await store.Version(id, ct);
        var workspace = await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = version.Plan.MapId }, ct)
            ?? throw new InvalidDataException("后台没有目标地图，请先导入并发布地图。");
        var parameters = await pipe.ReadAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters, new { }, ct)
            ?? throw new InvalidDataException("后台没有任务参数。");
        var communication = await pipe.ReadAsync<CommunicationSettingsSnapshot>(TrafficControlOperations.GetCommunicationSettings, new { }, ct)
            ?? throw new InvalidDataException("后台没有通信配置。");
        var compiled = EngineeringRuntimeCompiler.Compile(version, workspace, parameters, communication);
        if (compiled.Ready && compiled.MapActive)
        {
            var validation = await pipe.ReadAsync<TaskParameterResult>(TrafficControlOperations.ValidateTaskParameters,
                new TaskParameterRequest(Guid.NewGuid().ToString("N"), parameters.Revision, compiled.MapId, compiled.MapVersionId, compiled.Configuration!), ct);
            var errors = compiled.Blockers.ToList();
            if (!parameters.CanSave) errors.Add(parameters.Explanation);
            if (validation is not { Accepted: true }) errors.Add(validation?.Message ?? "真实九步配置校验未返回结果。");
            compiled = compiled with { Blockers = errors };
        }
        return compiled;
    }

    private static async Task<MapWorkspaceSnapshot> Workspace(string id, EngineeringStore store, DispatchPipe pipe, CancellationToken ct)
    {
        var version = await store.Version(id, ct);
        return await pipe.ReadAsync<MapWorkspaceSnapshot>(TrafficControlOperations.GetMapWorkspace, new { MapId = version.Plan.MapId }, ct)
            ?? throw new InvalidDataException("后台没有工程绑定的目标地图。");
    }

    private static string Csv(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    private static string SignalCsv(EngineeringPlan plan)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Csv("address") + "," + Csv("variableName") + "," + Csv("chineseName") + "," +
            Csv("stationId") + "," + Csv("deviceType") + "," + Csv("instanceIndex") + "," + Csv("dataType") + "," +
            Csv("direction") + "," + Csv("access") + "," + Csv("addressStatus") + "," + Csv("enabled") + "," +
            Csv("setConditionZh") + "," + Csv("resetConditionZh") + "," + Csv("sourceNote"));
        foreach (var signal in plan.Signals.OrderBy(s => s.PlcId).ThenBy(s => s.Address).ThenBy(s => s.Id))
        {
            var owner = plan.Stations.FirstOrDefault(s => s.Id.Equals(signal.OwnerId, StringComparison.OrdinalIgnoreCase));
            var isDumper = plan.Dumpers.Any(d => d.Id.Equals(signal.OwnerId, StringComparison.OrdinalIgnoreCase));
            var direction = signal.Direction == "Read" ? "PLC->Host" : "Host->PLC";
            builder.AppendLine(string.Join(',',
                Csv(signal.Address), Csv(signal.Name), Csv(signal.Name), Csv(signal.OwnerId),
                Csv(isDumper ? "Dumper" : "ChipRemovalStation"), Csv("1"), Csv(signal.DataType),
                Csv(direction), Csv(signal.Direction), Csv("EngineeringBundle"), Csv("true"),
                Csv("按工程包条件"), Csv("按工程包条件"),
                Csv($"工程版本冻结点表；工位={(owner?.Id ?? signal.OwnerId)}；PLC={signal.PlcId}")));
        }
        return builder.ToString();
    }

    private static string FindServiceConfig(IWebHostEnvironment environment)
    {
        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "Service", "config")),
            Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "AgvDispatch.Service", "config")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "Service", "config"))
        };
        return candidates.FirstOrDefault(Directory.Exists)
            ?? throw new InvalidDataException("未找到后台 Service/config，不能冻结PLC点表。");
    }

    private static async Task<CommunicationSettingsSnapshot> StageCommunication(
        EngineeringVersion version,
        EngineeringRuntimePreview preview,
        CommunicationSettingsSnapshot current,
        MapWorkspaceSnapshot workspace,
        ApplyRequest request,
        IWebHostEnvironment environment,
        DispatchPipe pipe,
        CancellationToken ct)
    {
        var plan = version.Plan;
        var next = JsonSerializer.Deserialize<CommunicationConfiguration>(
            JsonSerializer.Serialize(current.Saved), WebJson.Options)
            ?? throw new InvalidDataException("通信配置无法复制。");

        foreach (var target in plan.Vehicles)
        {
            var live = next.Agvs.FirstOrDefault(v => v.VehicleId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                live = new AgvConnectionSetting { VehicleId = target.Id, DisplayName = target.Name };
                next.Agvs.Add(live);
            }
            if (string.IsNullOrWhiteSpace(live.DisplayName)) live.DisplayName = target.Name;
            live.IpAddress = target.Host;
            live.Enabled = target.Enabled;
        }
        foreach (var existing in next.Agvs.Where(v => plan.Vehicles.All(p => !p.Id.Equals(v.VehicleId, StringComparison.OrdinalIgnoreCase))))
            existing.Enabled = false;

        string? csvRelative = null;
        if (plan.Signals.Count > 0)
        {
            var configDirectory = FindServiceConfig(environment);
            var fileName = "engineering-" + version.Id + ".csv";
            var fullPath = Path.Combine(configDirectory, fileName);
            var content = SignalCsv(plan);
            var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct);
                File.Move(temporary, fullPath, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            csvRelative = "config/" + fileName;
        }

        foreach (var target in plan.Plcs)
        {
            var live = next.Plcs.FirstOrDefault(p => p.DeviceId.Equals(target.Id, StringComparison.OrdinalIgnoreCase));
            if (live is null)
            {
                live = new PlcConnectionSetting { DeviceId = target.Id, DisplayName = target.Id };
                next.Plcs.Add(live);
            }
            live.IpAddress = target.Host;
            live.Port = target.Port;
            live.Enabled = target.Enabled;
            if (csvRelative is not null) live.SignalCsvPath = csvRelative;
        }

        next.MapBindings ??= [];
        foreach (var target in plan.Vehicles.Where(v => v.Enabled))
        {
            var targetMapName = workspace.Published?.Definition.RobokitSource?.MapName ?? "";
            var binding = next.MapBindings.FirstOrDefault(b =>
                b.VehicleId.Equals(target.Id, StringComparison.OrdinalIgnoreCase) &&
                b.LocalMapId.Equals(plan.MapId, StringComparison.OrdinalIgnoreCase))
                ?? next.MapBindings.FirstOrDefault(b =>
                    b.VehicleId.Equals(target.Id, StringComparison.OrdinalIgnoreCase) &&
                    b.RobotMap.Equals(targetMapName, StringComparison.OrdinalIgnoreCase));
            if (binding is null)
                throw new InvalidDataException(
                    $"车辆 {target.Id} 没有目标地图 {targetMapName}/{plan.MapVersion} 的车端地图MD5绑定；请先完成只读地图拉取和绑定，工程包未提交。");
            binding.LocalMapId = plan.MapId;
            binding.LocalMapVersion = plan.MapVersion;
            binding.CoordinatesVerified = preview.MapActive
                ? binding.CoordinatesVerified
                : request.VehicleMapCompatibilityConfirmed;
            var note = $"{request.Evidence.Trim()} 工程包={version.Id}；地图={plan.MapId}/{plan.MapVersion}；通信修订={preview.CommunicationRevision}。";
            binding.ConfirmationNote = note.Length <= 1000 ? note : note[..1000];
        }

        return await pipe.ReadAsync<CommunicationSettingsSnapshot>(
            TrafficControlOperations.SaveCommunicationSettings,
            new SaveCommunicationSettingsRequest(request.ExpectedCommunicationRevision, next), ct)
            ?? throw new InvalidOperationException("通信配置保存未返回结果。");
    }

    public static void MapEngineeringRuntimeApi(this WebApplication app, bool enabled)
    {
        app.MapGet("/api/engineering-runtime/state", async (DispatchPipe pipe, CancellationToken ct) =>
            await pipe.ReadAsync<TaskParameterSnapshot>(TrafficControlOperations.GetTaskParameters, new { }, ct));
        app.MapPost("/api/engineering-runtime/preview", async (PreviewRequest q, EngineeringStore store, DispatchPipe pipe, CancellationToken ct) =>
            await Preview(q.VersionId, store, pipe, ct));
        app.MapPost("/api/engineering-runtime/apply", async (ApplyRequest q, EngineeringStore store, DispatchPipe pipe,
            IWebHostEnvironment environment, CancellationToken ct) =>
        {
            if (!enabled) return Results.Json(new { error = "工程执行参数应用未启用；模拟实例不允许应用现场参数。" }, statusCode: 403);
            if (!Guid.TryParseExact(q.RequestId, "N", out _) || !q.ActionsReviewed || q.ConfirmationText != "APPLY " + q.VersionId)
                return Results.Ok(new TaskParameterResult(false, "请核对四段车辆动作，并输入准确工程版本确认文字。", []));
            var version = await store.Version(q.VersionId, ct);
            var workspace = await Workspace(q.VersionId, store, pipe, ct);
            var preview = await Preview(q.VersionId, store, pipe, ct);
            if (!preview.Ready) return Results.Ok(new TaskParameterResult(false, "未应用：" + string.Join("；", preview.Blockers), []));
            if (preview.CommunicationRevision != q.ExpectedCommunicationRevision || preview.ContentHash != q.ExpectedContentHash || preview.MapDefinitionHash != q.ExpectedMapHash)
                return Results.Ok(new TaskParameterResult(false, "地图、工程或通信配置已经变化，重新预检后再应用。", []));
            if (preview.Configuration is null)
                return Results.Ok(new TaskParameterResult(false, "工程没有编译出可执行参数。", []));
            if (!preview.MapActive &&
                (!q.SiteSafetyConfirmed || !q.VehicleMapCompatibilityConfirmed || string.IsNullOrWhiteSpace(q.Evidence)))
                return Results.Ok(new TaskParameterResult(false,
                    "工程包包含地图切换：请勾选现场安全和车辆地图兼容，并填写本次核对依据。", []));

            var communication = await pipe.ReadAsync<CommunicationSettingsSnapshot>(
                TrafficControlOperations.GetCommunicationSettings, new { }, ct)
                ?? throw new InvalidDataException("后台没有通信配置。");
            var staged = await StageCommunication(version, preview, communication, workspace, q, environment, pipe, ct);
            string? releaseId = null;
            var mapActivatedNow = false;

            if (!preview.MapActive)
            {
                var begun = await pipe.ReadAsync<MapSwitchResult>(TrafficControlOperations.BeginMapSwitch,
                    new BeginMapSwitchRequest(version.Plan.MapId, version.Plan.MapVersion), ct);
                if (begun is not { Accepted: true })
                    return Results.Ok(new TaskParameterResult(false,
                        "工程通信/点表已暂存，但地图切换未开始：" + (begun?.Message ?? "后台未返回结果"), []));
                var switchStatus = await pipe.ReadAsync<MapSwitchStatus>(TrafficControlOperations.GetMapSwitch, new { }, ct)
                    ?? throw new InvalidDataException("无法读取地图切换状态。");
                releaseId = switchStatus.Current?.ReleaseId;
                if (releaseId is null || switchStatus.Blockers.Count > 0)
                    return Results.Ok(new TaskParameterResult(false,
                        "工程通信/点表已暂存；地图切换仍被阻止：" + string.Join("；", switchStatus.Blockers), []));
                var activated = await pipe.ReadAsync<MapOperationResult>(TrafficControlOperations.ActivateMap,
                    new ActivateMapRequest(version.Plan.MapId, version.Plan.MapVersion,
                        "ACTIVATE " + workspace.Project.Name, true, true, q.Evidence.Trim())
                    {
                        ExpectedReleaseId = releaseId,
                        StoppedOffStationGeometryUpdateConfirmed = q.StoppedOffStationGeometryUpdateConfirmed
                    }, ct);
                if (activated is not { Accepted: true })
                    return Results.Ok(new TaskParameterResult(false,
                        $"工程通信/点表已暂存；地图激活未完成，执行保持暂停：{activated?.Message ?? "后台未返回结果"}", []));
                mapActivatedNow = true;
            }

            var parameterOperation = mapActivatedNow
                ? TrafficControlOperations.SaveEngineeringTaskParameters
                : TrafficControlOperations.SaveTaskParameters;
            var parameters = await pipe.ReadAsync<TaskParameterResult>(parameterOperation,
                new TaskParameterRequest(q.RequestId, q.ExpectedRevision, preview.MapId, preview.MapVersionId,
                    preview.Configuration), ct)
                ?? new TaskParameterResult(false, "任务参数保存未返回结果。", []);
            if (!parameters.Accepted)
                return Results.Ok(new EngineeringApplyResult(false,
                    parameters.Message + (mapActivatedNow
                        ? "；地图已激活但执行门禁保持暂停，请从原切换记录处理。"
                        : "；工程未完整应用。"),
                    parameters, staged.RestartRequired, staged.Revision, releaseId, mapActivatedNow));
            var message = "工程包参数已应用。" +
                (mapActivatedNow ? "目标地图已安全激活，派单保持暂停。" : "") +
                (staged.RestartRequired ? "通信、车辆IP或PLC点表已暂存，必须受控重启后生效。" : "");
            return Results.Ok(new EngineeringApplyResult(true, message, parameters,
                staged.RestartRequired, staged.Revision, releaseId, mapActivatedNow || staged.RestartRequired));
        });
    }
}
