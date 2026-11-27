using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace AgvDispatch.Infrastructure.Persistence;

// This coordinator never clears a PLC bit or transport lease. The original cancellation/recovery executor
// supplies closure evidence, and the ordinary creation/outbox executor remains the only replacement sender.
public sealed class TaskSwitchService(ISqlSugarClient database, ITaskSwitchPort port, TimeProvider? clock = null)
    : ITaskSwitchService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim mutex = new(1, 1);
    private bool initialized;
    private DateTime Now => (clock ?? AgvDispatch.Application.Runtime.BusinessClock.Provider).GetUtcNow().UtcDateTime;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { await InitializeCoreAsync(ct); }
        finally { mutex.Release(); }
    }
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        if (initialized) return;
        foreach (var row in await ActiveRowsAsync(ct))
        {
            var session = Convert(row);
            await HoldAsync(session, "后台重启；保留原车辆及请求编号，须显式核对续接。", ["启动后尚未核对本次受控换单"], ct);
        }
        initialized = true;
    }

    public async Task<TaskSwitchRecord?> GetAsync(string switchId, CancellationToken ct)
    {
        var row = await RowAsync(switchId, ct);
        return row is null ? null : Convert(row);
    }
    public async Task<IReadOnlyList<TaskSwitchRecord>> ListAsync(CancellationToken ct) =>
        (await database.Queryable<TaskSwitchEntity>().OrderByDescending(r => r.UpdatedAtUtc).Take(200).ToListAsync(ct))
        .Select(Convert).ToArray();

    public async Task<TaskSwitchResult> RequestAsync(TrafficUserContext user, TaskSwitchRequest request, CancellationToken ct)
    {
        Demand(user);
        if (!ValidId(request.RequestId) || !Text(request.TaskId, 64) || !Text(request.TargetStationId, 32) ||
            !Enum.IsDefined(request.ExpectedStage) || request.Priority is < 0 or > 100 || !request.SiteConfirmed ||
            !Text(request.Reason, 1000) || request.Reason.Trim().Length < 10 || !TaskSwitchModes.IsValid(request.Mode))
            return Reject("须填写有效的原任务、目标工位、优先级、至少10字依据，并确认现场条件。", null);
        request = request with { Reason = request.Reason.Trim() };
        await mutex.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            var replay = await RowAsync(request.RequestId, ct);
            if (replay is not null)
            {
                var original = Convert(replay);
                return JsonSerializer.Serialize(original.Request, Json) == JsonSerializer.Serialize(request, Json)
                    ? new(true, "原申请已记录；本次只返回原进度，没有再次取消或创建任务。", original)
                    : Reject("申请编号已用于不同的换单参数，请查询原申请。", original);
            }
            var inspection = await port.InspectSourceAsync(request, ct);
            if (!Text(inspection.VehicleId, 32) || !Text(inspection.MapId, 64) || !Text(inspection.MapVersionId, 64) ||
                !Text(inspection.ParameterRevision, 128) || inspection.Blockers.Length != 0)
                return Reject(inspection.Blockers.Length != 0 ? string.Join("；", inspection.Blockers) : "缺少原车辆或运行配置版本。", null);
            var conflict = (await ActiveRowsAsync(ct)).FirstOrDefault(r => r.VehicleId == inspection.VehicleId || r.SourceTaskId == request.TaskId);
            if (conflict is not null) return Reject("原任务或车辆已有未闭合换单申请，请查询原记录。", Convert(conflict));
            if (!await new TaskSwitchGuard(database).AllowsVehicleAsync(inspection.VehicleId, null, null, ct))
                return Reject("原车辆正在交接已创建的新任务，须等正常调度完成接管后再申请换单。", null);
            var initialMessage = TaskSwitchModes.IsCleanup(inspection.ResolvedMode)
                ? "已保存载箱返箱收尾申请；原九步业务继续执行，完成PLC收尾后才创建新任务。"
                : "已保存空车导航换单申请，等待按原编号申请取消。";
            var session = new TaskSwitchRecord(request.RequestId, request.TaskId, inspection.VehicleId, request.TargetStationId,
                null, TaskSwitchStages.Requested, initialMessage, [], 1,
                new DateTimeOffset(Now, TimeSpan.Zero), inspection.MapId, inspection.MapVersionId, inspection.ParameterRevision,
                Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), request, user.WindowsIdentity)
                { ResolvedMode = inspection.ResolvedMode };
            try { await database.Insertable(ToRow(session)).ExecuteCommandAsync(ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                var winner = (await ActiveRowsAsync(ct)).FirstOrDefault(r => r.VehicleId == inspection.VehicleId || r.SourceTaskId == request.TaskId);
                if (winner is not null) return Reject("并发申请已锁定原任务或车辆，请查询原记录。", Convert(winner));
                throw;
            }
            return new(true, session.Message, session);
        }
        finally { mutex.Release(); }
    }

    public async Task<TaskSwitchResult> ResumeAsync(TrafficUserContext user, TaskSwitchResumeRequest request, CancellationToken ct)
    {
        Demand(user);
        await mutex.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            var row = await RowAsync(request.SwitchId, ct);
            if (row is null) return Reject("换单申请不存在。", null);
            var session = Convert(row);
            if (!ValidReview(request) || request.ExpectedRevision != session.Revision ||
                session.Stage is not (TaskSwitchStages.ReviewHold or TaskSwitchStages.Suspended))
                return Reject("现场确认、依据、版本或原挂起状态不匹配，请刷新原记录后核对。", session);
            var observation = await port.ObserveCloseAsync(session, ct);
            var blockers = ContextBlockers(session, observation);
            if (blockers.Length > 0)
            {
                session = await HoldAsync(session, "原申请仍有未闭合条件。", blockers, ct);
                return Reject(session.Message, session);
            }
            var stage = session.ResumeStage ?? TaskSwitchStages.Requested;
            session = await SaveAsync(session with { Stage = stage, ResumeStage = null, Blockers = [],
                Message = "现场核对后续接原换单申请；保留取消编号和新任务申请编号。依据：" + request.Reason.Trim() }, ct);
            return new(true, session.Message, session);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var row = await RowAsync(request.SwitchId, ct);
            if (row is null) throw;
            var held = await HoldAsync(Convert(row), "续接核对失败；原车辆及申请仍保留。", [error.Message], ct);
            return Reject(held.Message, held);
        }
        finally { mutex.Release(); }
    }

    public async Task<TaskSwitchResult> SuspendAsync(TrafficUserContext user, TaskSwitchResumeRequest request, CancellationToken ct)
    {
        Demand(user);
        await mutex.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            var row = await RowAsync(request.SwitchId, ct);
            if (row is null) return Reject("换单申请不存在。", null);
            var session = Convert(row);
            if (!ValidReview(request) || request.ExpectedRevision != session.Revision || TaskSwitchStages.IsTerminal(session.Stage))
                return Reject("现场确认、依据或原版本不匹配，请刷新后重新核对。", session);
            if (session.Stage == TaskSwitchStages.Suspended)
                return new(true, "原换单申请已经挂起；车辆锁和原任务责任继续保留。", session);
            if (session.ReplacementCreateAttempted || session.Stage is TaskSwitchStages.SubmittingReplacement or TaskSwitchStages.ReadyToSwitch)
                return Reject("新任务创建意图已经记录，不能挂起后释放车辆。", session);
            if (session.Stage is TaskSwitchStages.AwaitingStop or TaskSwitchStages.ClosingOriginal)
                return Reject("原导航取消或原任务关闭已经发起，只能查询原记录并完成收尾，不能临时挂起。", session);
            if (session.Stage is not (TaskSwitchStages.Requested or TaskSwitchStages.AwaitingBusinessCleanup or TaskSwitchStages.ReviewHold))
                return Reject("当前换单阶段不能挂起。", session);
            var resumeStage = session.Stage == TaskSwitchStages.ReviewHold
                ? session.ResumeStage ?? TaskSwitchStages.Requested
                : session.Stage;
            session = await SaveAsync(session with
            {
                Stage = TaskSwitchStages.Suspended,
                ResumeStage = resumeStage,
                Blockers = [],
                Message = "换单已挂起；原任务和车辆责任保持不变，其他车辆可以接单。依据：" + request.Reason.Trim(),
            }, ct);
            return new(true, session.Message, session);
        }
        finally { mutex.Release(); }
    }

    public async Task<TaskSwitchResult> AbandonAsync(TrafficUserContext user, TaskSwitchResumeRequest request, CancellationToken ct)
    {
        Demand(user);
        await mutex.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            var row = await RowAsync(request.SwitchId, ct);
            if (row is null) return Reject("换单申请不存在。", null);
            var session = Convert(row);
            if (session.Stage == TaskSwitchStages.Abandoned) return new(true, "原换单申请已放弃。", session);
            if (!ValidReview(request) || request.ExpectedRevision != session.Revision || TaskSwitchStages.IsTerminal(session.Stage))
                return Reject("现场确认、依据或原版本不匹配，请刷新后重新核对。", session);
            var receipt = await port.FindReplacementAsync(session, ct);
            if (!string.IsNullOrWhiteSpace(receipt))
            {
                session = await CompleteAsync(session, receipt, ct);
                return Reject("新任务已经创建，原换单交接已记录；请通过新任务处置入口操作，不能放弃后再建任务。", session);
            }
            if (session.ReplacementCreateAttempted || session.Stage == TaskSwitchStages.SubmittingReplacement || session.ResumeStage == TaskSwitchStages.SubmittingReplacement)
                return Reject("已记录新任务创建意图但原回执尚未核实，不能释放车辆或放弃申请。", session);
            var observation = await port.ObserveCloseAsync(session, ct);
            var blockers = ContextBlockers(session, observation).Concat(!observation.OriginalClosed
                ? ["原任务、箱体、PLC握手或路权尚未核对闭合"] : Array.Empty<string>()).Distinct().ToArray();
            if (blockers.Length > 0)
            {
                session = await HoldAsync(session, "原责任仍未闭合，不能放弃并释放车辆。", blockers, ct);
                return Reject(session.Message, session);
            }
            session = await SaveAsync(session with { Stage = TaskSwitchStages.Abandoned, ResumeStage = null, Blockers = [],
                Message = "已核对原任务闭合且未创建新任务，放弃本次换单。依据：" + request.Reason.Trim() }, ct);
            return new(true, session.Message, session);
        }
        finally { mutex.Release(); }
    }

    public async Task ProcessAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            await InitializeCoreAsync(ct);
            foreach (var row in await ActiveRowsAsync(ct))
            {
                var session = Convert(row);
                if (session.Stage is TaskSwitchStages.ReviewHold or TaskSwitchStages.Suspended) continue;
                try { await AdvanceAsync(session, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    // A side effect may already have happened. Reload the last durable intent, never infer absence.
                    var durable = Convert((await RowAsync(session.SwitchId, ct))!);
                    await HoldAsync(durable, "换单执行结果需核对；保留原车辆与请求编号。", [error.Message], ct);
                }
            }
        }
        finally { mutex.Release(); }
    }

    private async Task AdvanceAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        // Always reconcile a fixed replacement receipt before interpreting source closure again.
        if (session.Stage == TaskSwitchStages.SubmittingReplacement)
        {
            var receipt = await port.FindReplacementAsync(session, ct);
            if (!string.IsNullOrWhiteSpace(receipt)) { await CompleteAsync(session, receipt, ct); return; }
        }
        var observation = await port.ObserveCloseAsync(session, ct);
        var blockers = ContextBlockers(session, observation);
        if (blockers.Length > 0) { await HoldAsync(session, "受控换单等待核对。", blockers, ct); return; }
        switch (session.Stage)
        {
            case TaskSwitchStages.Requested:
                if (TaskSwitchModes.IsCleanup(session.ResolvedMode))
                {
                    await SaveAsync(session with { Stage = TaskSwitchStages.AwaitingBusinessCleanup,
                        Message = "原任务继续按九步业务返箱、倒料和PLC收尾；此阶段不发送取消或替代导航。" }, ct);
                }
                else
                {
                    // Persist before external cancellation. A lost response is recovered with the SAME cancel request.
                    session = await SaveAsync(session with { Stage = TaskSwitchStages.AwaitingStop,
                        ResumeStage = TaskSwitchStages.Requested, Message = "已记录取消提交意图，等待原运输终结。" }, ct);
                    await port.RequestCancelAsync(session, ct);
                    await SaveAsync(session with { ResumeStage = null }, ct);
                }
                break;
            case TaskSwitchStages.AwaitingBusinessCleanup:
                if (observation.CleanupConfirmed && observation.OriginalClosed)
                    await SaveAsync(session with { Stage = TaskSwitchStages.ReadyToSwitch,
                        Message = "原任务九步和PLC责任已完成，下一阶段才会申请新任务。" }, ct);
                break;
            case TaskSwitchStages.AwaitingStop:
                if (observation.CancelConfirmed)
                    await SaveAsync(session with { Stage = TaskSwitchStages.ClosingOriginal, Message = "原运输已确认终结，等待原箱体、PLC握手及路权核对闭合。" }, ct);
                break;
            case TaskSwitchStages.ClosingOriginal:
                if (observation.OriginalClosed && observation.CancelConfirmed)
                    await SaveAsync(session with { Stage = TaskSwitchStages.ReadyToSwitch, Message = "原任务责任已闭合，下一阶段才会申请新任务。" }, ct);
                else if (observation.CancelConfirmed && observation.CanCloseOriginal)
                {
                    session = await SaveAsync(session with { Message = "已记录原任务关闭意图；端口须再次核验原责任闭合，不写PLC或释放路权。" }, ct);
                    await port.CloseOriginalAsync(session, ct);
                }
                break;
            case TaskSwitchStages.ReadyToSwitch:
                if (!observation.OriginalClosed || !observation.TerminalConfirmed)
                { await HoldAsync(session, "原闭合证据发生变化。", ["原任务终态或责任闭合未确认"], ct); return; }
                await SaveAsync(session with { Stage = TaskSwitchStages.SubmittingReplacement, Message = "已保存新任务申请阶段，下一次只使用固定申请编号。" }, ct);
                break;
            case TaskSwitchStages.SubmittingReplacement:
                if (session.ReplacementCreateAttempted)
                { await HoldAsync(session, "新任务创建结果不确定，继续查询原申请回执；禁止重建或更换编号。", ["未查询到原新任务申请回执"], ct); return; }
                if (!observation.OriginalClosed || !observation.TerminalConfirmed)
                { await HoldAsync(session, "原闭合证据发生变化。", ["原任务终态或责任闭合未确认"], ct); return; }
                session = await SaveAsync(session with { ReplacementCreateAttempted = true, Message = "已持久化新任务创建意图，沿用固定申请编号。" }, ct);
                var targetId = await port.CreateReplacementAsync(session, ct);
                if (string.IsNullOrWhiteSpace(targetId)) throw new InvalidOperationException("创建接口未返回确定的新任务编号，须查询原申请回执。");
                await CompleteAsync(session, targetId, ct);
                break;
            default: await HoldAsync(session, "未知换单阶段，保留原锁等待核对。", ["未知阶段：" + session.Stage], ct); break;
        }
    }

    private Task<TaskSwitchRecord> CompleteAsync(TaskSwitchRecord session, string taskId, CancellationToken ct) =>
        SaveAsync(session with { Stage = TaskSwitchStages.Completed, TargetTaskId = taskId, ResumeStage = null, Blockers = [],
            Message = "新任务编号已保存，交由正常调度执行器执行；本记录不代表新业务任务已经完成。" }, ct);

    private Task<TaskSwitchRecord> HoldAsync(TaskSwitchRecord session, string message, string[] blockers, CancellationToken ct) =>
        SaveAsync(session with { Stage = TaskSwitchStages.ReviewHold,
            ResumeStage = session.ResumeStage ?? (session.Stage == TaskSwitchStages.ReviewHold ? TaskSwitchStages.Requested : session.Stage),
            Message = message, Blockers = blockers.Distinct().ToArray() }, ct);

    private static string[] ContextBlockers(TaskSwitchRecord session, TaskSwitchClosureObservation observation) =>
        observation.Blockers.Concat(session.MapId != observation.MapId || session.MapVersionId != observation.MapVersionId ||
            session.ParameterRevision != observation.ParameterRevision ? ["地图、版本或任务参数已变化，禁止沿用原申请换单"] : Array.Empty<string>()).Distinct().ToArray();

    private async Task<TaskSwitchRecord> SaveAsync(TaskSwitchRecord session, CancellationToken ct)
    {
        var next = session with { Revision = session.Revision + 1, UpdatedAt = new DateTimeOffset(Now, TimeSpan.Zero) };
        var changed = await database.Updateable(ToRow(next)).Where(r => r.SwitchId == session.SwitchId && r.Revision == session.Revision).ExecuteCommandAsync(ct);
        if (changed != 1) throw new InvalidOperationException("换单记录已变化，禁止覆盖原版本。");
        return next;
    }

    private Task<TaskSwitchEntity?> RowAsync(string id, CancellationToken ct) => database.Queryable<TaskSwitchEntity>().FirstAsync(r => r.SwitchId == id, ct)!;
    private Task<List<TaskSwitchEntity>> ActiveRowsAsync(CancellationToken ct) => database.Queryable<TaskSwitchEntity>()
        .Where(r => r.Stage != TaskSwitchStages.Completed && r.Stage != TaskSwitchStages.Abandoned).ToListAsync(ct);
    private static bool ValidId(string value) => Guid.TryParseExact(value, "N", out _);
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool ValidReview(TaskSwitchResumeRequest request) => ValidId(request.SwitchId) && request.SiteConfirmed &&
        Text(request.Reason, 1000) && request.Reason.Trim().Length >= 10;
    private static TaskSwitchResult Reject(string message, TaskSwitchRecord? session) => new(false, message, session);
    private static void Demand(TrafficUserContext user)
    { if (!user.Has(TrafficPermission.View)) throw new UnauthorizedAccessException("需要本地任务操作权限。"); }

    private static TaskSwitchRecord Convert(TaskSwitchEntity row) => new(row.SwitchId, row.SourceTaskId, row.VehicleId, row.TargetStationId,
        row.TargetTaskId, row.Stage, row.Message, JsonSerializer.Deserialize<string[]>(row.BlockersJson, Json) ?? [], row.Revision,
        new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc)), row.MapId, row.MapVersionId, row.ParameterRevision,
        row.ReplacementRequestId, row.CancelRequestId, JsonSerializer.Deserialize<TaskSwitchRequest>(row.RequestJson, Json)
            ?? throw new InvalidDataException("原换单申请内容损坏，禁止继续。"), row.Operator)
        {
            ResumeStage = row.ResumeStage,
            ReplacementCreateAttempted = row.ReplacementCreateAttempted,
            ResolvedMode = TaskSwitchModes.IsValid(row.ResolvedMode) ? row.ResolvedMode : TaskSwitchModes.CancelEmptyNavigation,
        };
    private static TaskSwitchEntity ToRow(TaskSwitchRecord session) => new()
    {
        SwitchId = session.SwitchId, SourceTaskId = session.SourceTaskId, VehicleId = session.VehicleId, TargetStationId = session.TargetStationId,
        TargetTaskId = session.TargetTaskId, Stage = session.Stage, ResumeStage = session.ResumeStage, Message = session.Message,
        BlockersJson = JsonSerializer.Serialize(session.Blockers, Json), Revision = session.Revision, UpdatedAtUtc = session.UpdatedAt.UtcDateTime,
        MapId = session.MapId, MapVersionId = session.MapVersionId, ParameterRevision = session.ParameterRevision,
        ReplacementRequestId = session.ReplacementRequestId, CancelRequestId = session.CancelRequestId,
        ResolvedMode = session.ResolvedMode, RequestJson = JsonSerializer.Serialize(session.Request, Json),
        Operator = session.Operator, ReplacementCreateAttempted = session.ReplacementCreateAttempted
    };
}
