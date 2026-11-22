using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;
namespace AgvDispatch.Infrastructure.Persistence;

public sealed class ExecutionEvidenceJournal(ISqlSugarClient database, IExecutionIdentityCache? identities = null) : IExecutionEvidenceJournal
{
    private readonly AsyncLocal<(string Task, string Step)?> _scope = new();
    public IDisposable BeginScope(string taskId, string stepId)
    {
        var previous = _scope.Value;
        _scope.Value = (taskId, stepId);
        return new Scope(() => _scope.Value = previous);
    }
    public Task RecordCurrentAsync(string kind, object evidence, CancellationToken ct) =>
        RecordAsync(_scope.Value?.Task ?? "", _scope.Value?.Step ?? "", kind, evidence, ct);
    public async Task RecordAsync(string taskId, string stepId, string kind, object evidence, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(evidence);
        if (identities != null && kind is ("PlcWriteIntent" or "AgvSendIntent") && taskId != "" && stepId != "")
        {
            var step = await database.Queryable<DispatchTaskStepEntity>().Where(s => s.TaskId == taskId && s.StepId == stepId).FirstAsync(ct);
            var task = await database.Queryable<DispatchTaskEntity>().Where(t => t.TaskId == taskId).FirstAsync(ct);
            if (step != null && task != null)
            {
                var station = step.StepType is 40 or 60 ? step.SourceSite ?? task.StationId : task.StationId;
                identities.TryGetPlc(station, out var plc);
                identities.TryGetVehicle(step.VehicleId ?? task.VehicleId ?? "", out var vehicle);
                // Store even missing samples. Recovery must not choose a later intent as a new original baseline.
                var node = System.Text.Json.Nodes.JsonNode.Parse(json);
                if (node is System.Text.Json.Nodes.JsonObject obj)
                { obj["ExecutionIdentityBaseline"] = JsonSerializer.SerializeToNode(new ExecutionIdentityBaseline(plc, vehicle, AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow())); json = obj.ToJsonString(); }
            }
        }
        await database.Insertable(new ExecutionEvidenceEntity
        {
            Id = Guid.NewGuid().ToString("N"), TaskId = taskId, StepId = stepId,
            Operator = "service", Kind = kind, Evidence = json, OccurredAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow().LocalDateTime
        }).ExecuteCommandAsync(ct);
    }
    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
