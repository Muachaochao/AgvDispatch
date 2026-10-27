using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Application.Maps;

namespace AgvDispatch.Service;

public sealed class TaskParameterSettingsService(TaskParameterStorageOptions storage, TaskParameterRuntime runtime,
    IManualTaskCreationService validator, IActiveTaskLookup tasks, IActiveMapRuntime maps, IMapExecutionGate gate) : ITaskParameterService
{
    private TaskParameterDocument? _saved = ReadDocument(storage.Path);
    public static TaskParameterDocument? ReadDocument(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("任务参数文件超过 1 MB。");
        var document = JsonSerializer.Deserialize<TaskParameterDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("任务参数文件为空。");
        if (document.Version != 1 || !Guid.TryParseExact(document.Revision, "N", out _) || document.Audits is null)
            throw new InvalidDataException("任务参数版本、修订号或审计记录无效。");
        TaskParameterRuntime.CheckStructure(document.Configuration);
        return document;
    }
    private async Task<TaskParameterSnapshot> SnapshotAsync(CancellationToken ct)
    {
        var active = await tasks.GetActiveTasksAsync(ct);
        var map = maps.Current;
        // Preparing configuration shares the control mutex with switching, but never opens the send/recovery barrier.
        var reason = gate.Mode != MapExecutionMode.Running ? $"地图执行模式为 {gate.Mode}，请先完成或取消地图切换，再保存任务参数。" : active.Count > 0
            ? $"存在 {active.Count} 个未结束任务（含故障任务），处理完成后才能保存。" : "保存前逐步骤校验；无未结束任务时可保存配置，重启后保留。配置保存不会放行启动恢复、PLC 写入或车辆动作。";
        return new(runtime.Revision, runtime.Capture(), map?.MapId, map?.VersionId,
            gate.Mode == MapExecutionMode.Running && active.Count == 0 && map is not null, map is null ? "请先激活运行地图。" : reason, _saved?.Audits ?? []) { SavedRequestId = _saved?.RequestId };
    }
    public async Task<TaskParameterSnapshot> GetAsync(CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        return await SnapshotAsync(ct);
    }
    private TaskParameterResult ValidateCore(TaskParameterRequest request)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _)) return new(false, "请求编号无效。", []);
        if (request.ExpectedRevision != runtime.Revision) return new(false, "任务参数已变化，请重新加载后编辑。", []);
        if (maps.Current is not { } map || map.MapId != request.MapId || map.VersionId != request.MapVersionId)
            return new(false, "运行地图已变化或未激活，请重新加载参数。", []);
        try { TaskParameterRuntime.CheckStructure(request.Configuration); }
        catch (ArgumentException error) { return new(false, error.Message, []); }
        if (runtime.Capture().EngineeringSource is not null && request.Configuration.EngineeringSource is null)
            return new(false, "当前任务参数已绑定工程版本，不能移除工程来源和车辆池；请通过工程执行接入应用新版本。", []);
        var current = runtime.Capture();
        if (current.EngineeringSource is { } bound && request.Configuration.EngineeringSource?.VersionId == bound.VersionId &&
            (request.Configuration.EngineeringSource != bound ||
             JsonSerializer.Serialize(new { current.DumperStationId, current.WaitingSite, current.DumpingSite, current.Pickups }) !=
             JsonSerializer.Serialize(new { request.Configuration.DumperStationId, request.Configuration.WaitingSite, request.Configuration.DumpingSite, request.Configuration.Pickups })))
            return new(false, "同一工程版本的工位、倒料及车辆池绑定不可改写；请发布并应用新工程版本。", []);
        if (request.Configuration.EngineeringSource is { } source && source.MapDefinitionHash != EngineeringRuntimeIdentity.MapHash(map.Definition))
            return new(false, "工程绑定的地图内容摘要已变化，未应用参数。", []);
        var preview = validator.ValidateParameters(request.Configuration);
        var errors = preview.SelectMany(row => row.Step.ValidationErrors.Select(error => $"{row.StationId} 第 {row.Step.Sequence} 步（{row.Step.Name}）：{error}")).ToArray();
        return new(errors.Length == 0, errors.Length == 0 ? "所有工位九步配置校验通过；配置通过不代表执行许可。PLC 写入许可、恢复放行、首段车辆路线和动态路权在创建/执行时检查。" : string.Join("；", errors), preview);
    }
    public async Task<TaskParameterResult> ValidateAsync(TaskParameterRequest request, CancellationToken ct)
    {
        using var lease = await gate.EnterControlAsync(ct);
        return ValidateCore(request);
    }
    public async Task<TaskParameterResult> SaveAsync(TrafficUserContext user, TaskParameterRequest request, CancellationToken ct)
    {
        if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("当前会话不能保存任务参数。");
        using var lease = await gate.EnterControlAsync(ct);
        if (_saved?.RequestId == request.RequestId)
            return JsonSerializer.Serialize(_saved.Configuration) == JsonSerializer.Serialize(request.Configuration) &&
                _saved.MapId == request.MapId && _saved.MapVersionId == request.MapVersionId
                ? new(true, "该请求已保存并生效，未重复保存。", [], await SnapshotAsync(ct))
                : new(false, "请求编号已用于不同任务参数。", []);
        var snapshot = await SnapshotAsync(ct);
        if (!snapshot.CanSave) return new(false, snapshot.Explanation, [], snapshot);
        // Clone client data before validation/persistence to keep the saved and applied profile identical.
        try { TaskParameterRuntime.CheckStructure(request.Configuration); }
        catch (ArgumentException error) { return new(false, error.Message, []); }
        request = request with { Configuration = TaskParameterRuntime.Clone(request.Configuration) };
        var validation = ValidateCore(request);
        if (!validation.Accepted) return validation;
        var revision = Guid.NewGuid().ToString("N");
        var saved = new TaskParameterDocument(1, revision, request.RequestId, request.MapId, request.MapVersionId, request.Configuration,
            new[] { new TaskParameterAudit(DateTimeOffset.Now, user.WindowsIdentity, revision) }.Concat(_saved?.Audits ?? []).Take(100).ToArray());
        var path = Path.GetFullPath(storage.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }), ct);
            ct.ThrowIfCancellationRequested(); File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        runtime.Apply(saved); _saved = saved;
        return new(true, "任务参数配置已保存并立即生效；后台重启后继续使用。配置保存不会放行启动恢复、PLC 写入或车辆动作。", validation.Steps, await SnapshotAsync(ct));
    }
}
