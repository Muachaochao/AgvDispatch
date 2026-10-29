using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using Microsoft.Extensions.Hosting;

namespace AgvDispatch.Service;

// A durable session, not a second task executor. Nine-step tasks retain their own
// outbox, handshake and cancellation rules. Uncertain sends never auto retry.
public sealed class BusinessRunService(IBusinessRunPort port, BusinessRunOwnership ownership, string path)
    : IsolatedBackgroundService, IBusinessRunService
{
    private readonly SemaphoreSlim mutex = new(1, 1);
    private BusinessRunRecord? run = Load(path, ownership);
    private static BusinessRunRecord? Load(string path, BusinessRunOwnership ownership)
    {
        ownership.LoadRetirements(path + ".retired-requests.json");
        if (!File.Exists(path)) return null;
        var row = JsonSerializer.Deserialize<BusinessRunRecord>(File.ReadAllText(path))
            ?? throw new InvalidDataException("业务运行记录损坏。");
        if (row.Stage != "Completed")
        {
            row = row with { Stage = "Hold", Message = "重启后保留原任务和占用，须核对原运行；禁止自动续跑。" };
            ownership.Set(row.Plan.VehicleId);
            ownership.Returning = true;
        }
        return row;
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, run, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true); }
        File.Move(tmp, path, true);
    }
    private void Move(string stage, string message)
    { if (stage == "Hold") ownership.Returning = true; run = run! with { Stage = stage, Message = message, UpdatedAt = DateTimeOffset.UtcNow }; Save(); }
    private BusinessRunStatus Status() => new(run, "按需运行不制造呼叫；循环测试由本机PLC工具产生呼叫。收尾后返回待机点。");
    private static void Authorize(TrafficUserContext user)
    { if (!user.Has(TrafficPermission.Administer)) throw new UnauthorizedAccessException("需要管理员权限。"); }
    public async Task<BusinessRunStatus> ReadAsync(CancellationToken ct)
    { await mutex.WaitAsync(ct); try { if (run?.Stage == "Hold") { run = await port.ReconcileAsync(run, ct); Save(); } return Status(); } finally { mutex.Release(); } }
    public async Task<BusinessRunStatus> StartAsync(TrafficUserContext user, BusinessRunStart plan, CancellationToken ct)
    {
        Authorize(user); await mutex.WaitAsync(ct);
        try
        {
            if (run?.Plan.RequestId == plan.RequestId)
            {
                if (JsonSerializer.Serialize(run.Plan) != JsonSerializer.Serialize(plan)) throw new ArgumentException("同一请求编号不能修改参数。");
                return Status();
            }
            if (run is { Stage: not "Completed" }) throw new InvalidOperationException("原运行未结束；请查询原任务，不能覆盖。");
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            if (Directory.Exists(directory))
                foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(path) + ".*.history"))
                    if (JsonSerializer.Deserialize<BusinessRunRecord>(File.ReadAllText(file))?.Plan.RequestId == plan.RequestId)
                        throw new InvalidOperationException("原申请已经归档，不能重放为新运行。");
            if (!Guid.TryParseExact(plan.RequestId, "N", out _) || !plan.SiteConfirmed ||
                plan.Mode is not ("Demand" or "Cycle") || plan.Stations is null || plan.Stations.Length is < 1 or > 1000 ||
                plan.Stations.Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Stations.Length ||
                plan.Stations.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 32 || s != s.Trim() || s.Any(char.IsControl)) || plan.Rounds is < 1 or > 100 ||
                string.IsNullOrWhiteSpace(plan.VehicleId) || string.IsNullOrWhiteSpace(plan.StandbySite))
                throw new ArgumentException("请核对模式、车辆、工位顺序、轮数及现场确认。");
            await port.ValidateAsync(plan, ct);
            var initial = new BusinessRunRecord(Guid.NewGuid().ToString("N"), plan, "", "", "", user.WindowsIdentity,
                "Preparing", "准备运行", 0, null, null, null, false, DateTimeOffset.UtcNow);
            var observation = await port.ObserveAsync(initial, ct);
            if (observation.Block is { } block) throw new InvalidOperationException(block);
            if (observation.AnyActiveTasks || !observation.AllClosed || !observation.Stopped)
                throw new InvalidOperationException("启动前须无未完成业务任务、原握手已闭合且车辆停稳。");
            if (run is not null) File.Copy(path, path + "." + run.RunId + ".history", false);
            run = initial with { MapId = observation.MapId, MapVersionId = observation.MapVersionId, ParameterRevision = observation.ParameterRevision };
            ownership.Set(plan.VehicleId); Save();
            try
            {
                if (plan.Mode == "Cycle") await port.SimulatorAsync(run, false, ct);
                Move("Waiting", "已启动，等待PLC工位呼叫；无呼叫时返回待机点。");
            }
            catch (Exception e) { Move("Hold", "启动结果待核对：" + e.Message); throw; }
            return Status();
        }
        finally { mutex.Release(); }
    }
    public async Task<BusinessRunStatus> StopAsync(TrafficUserContext user, BusinessRunStop request, CancellationToken ct)
    {
        Authorize(user); await mutex.WaitAsync(ct);
        try
        {
            if (run is null || run.RunId != request.RunId) throw new ArgumentException("原运行编号不一致。");
            if (run.Stage == "Completed") return Status();
            run = run with { StopRequested = true, UpdatedAt = DateTimeOffset.UtcNow }; Save();
            if (run.Plan.Mode == "Cycle")
                try { await port.SimulatorAsync(run, true, ct); }
                catch (Exception e) { Move("Hold", "停止新增呼叫结果待核对，请重试同一停止申请：" + e.Message); throw; }
            // Do not cancel motion or clear calls/H bits. Already raised calls still drain.
            return Status();
        }
        finally { mutex.Release(); }
    }
    public async Task<BusinessRunStatus> CloseReviewedAsync(TrafficUserContext user, BusinessRunStop request, CancellationToken ct)
    {
        Authorize(user); await mutex.WaitAsync(ct);
        try
        {
            if (run is null || run.RunId != request.RunId || run.Stage != "Hold")
                throw new InvalidOperationException("仅能核对并结束指定暂停运行。");
            run = await port.ReconcileAsync(run, ct); Save();
            var o = await port.ObserveAsync(run, ct);
            if (o.Block is not null || !o.AllClosed || o.AnyActiveTasks || o.Calls.Length != 0 || !o.Stopped ||
                string.IsNullOrWhiteSpace(o.CurrentSite) || run.TaskId is not null && o.CurrentTaskState is not ("Completed" or "Cancelled") ||
                run.VendorTaskId is not null && !o.StandbyCompleted)
                throw new InvalidOperationException(o.Block ?? "原任务、呼叫、握手或待机停靠尚未核对闭合，不解除占用。");
            if (run.PendingRequestId is not null && run.TaskId is null && run.VendorTaskId is null)
            {
                run = await port.RetireUnacceptedAsync(run, ct); Save();
                if (run.PendingRequestId is not null)
                    throw new InvalidOperationException("原发送意图未取得确定回执，须先核对原请求，不能直接解除占用。");
            }
            await port.ValidateAsync(run.Plan, ct);
            if (run.Plan.Mode == "Cycle") await port.SimulatorAsync(run, true, ct);
            if (run.VendorTaskId is not null) await port.ReleaseReturnAsync(run, ct);
            Move("Completed", "人工请求核对结束；原任务、停止状态及安全站点已核验，没有自动续跑或重发。");
            ownership.Set(null); ownership.Returning = false;
            return Status();
        }
        finally { mutex.Release(); }
    }
    public async Task<BusinessRunStatus> ResumeAsync(TrafficUserContext user, BusinessRunResume request, CancellationToken ct)
    {
        Authorize(user); await mutex.WaitAsync(ct);
        try
        {
            if (run is null || run.RunId != request.RunId) throw new InvalidOperationException("原运行编号不一致。");
            if (!Guid.TryParseExact(request.RequestId,"N",out _) || !request.SiteConfirmed || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Trim().Length is < 10 or > 1000)
                throw new ArgumentException("续接须保留申请编号，填写现场依据并确认现场条件。");
            if (run.LastResumeRequestId == request.RequestId)
            {
                if (run.LastResumeReason != request.Reason.Trim()) throw new ArgumentException("同一续接申请不能修改依据。");
                return Status();
            }
            if (run.Stage != "Hold" || run.UpdatedAt != request.ExpectedUpdatedAt)
                throw new InvalidOperationException("原运行状态已变化，请刷新后重新核对。");
            run = await port.ReconcileAsync(run,ct); Save();
            var o = await port.ObserveAsync(run,ct);
            if (o.Block is not null) throw new InvalidOperationException(o.Block);
            if (o.MapId != run.MapId || o.MapVersionId != run.MapVersionId || o.ParameterRevision != run.ParameterRevision)
                throw new InvalidOperationException("原地图或参数版本已变化，不允许续接。");
            string stage;
            if (run.VendorTaskId is not null) stage = "Returning"; // Observe verifies the original vendor identity.
            else if (run.TaskId is not null)
            {
                if (o.CurrentTaskState is null or "Cancelled" or "Faulted" or "CancellationReview" or "CancellationPending")
                    throw new InvalidOperationException("原任务仍需取消或恢复核对；不能用续接替代原任务处置。");
                stage = "Executing";
            }
            else
            {
                if (run.PendingRequestId is not null) throw new InvalidOperationException("原发送结果未确定；仅查询原回执，禁止重发或新建替代任务。");
                if (!o.AllClosed || o.AnyActiveTasks || !o.Stopped) throw new InvalidOperationException("无原任务可续接，须先核对握手、占用及车辆停稳。");
                await port.ValidateAsync(run.Plan,ct);
                stage = "Waiting";
            }
            run = run with { LastResumeRequestId=request.RequestId, LastResumeReason=request.Reason.Trim(), Operator=user.WindowsIdentity };
            Move(stage,"人工核对后续接原运行；保留原任务、发送意图及停止新增标记。依据："+request.Reason.Trim());
            ownership.Returning=stage=="Returning";
            return Status();
        }
        finally { mutex.Release(); }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) await TickAsync(stoppingToken);
    }
    public async Task TickAsync(CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if (run is null || run.Stage is "Completed" or "Hold") return;
            var o = await port.ObserveAsync(run, ct);
            if (o.Block is { } block) { ownership.Returning = true; Move("Hold", block); return; }
            if (o.MapId != run.MapId || o.MapVersionId != run.MapVersionId || o.ParameterRevision != run.ParameterRevision)
            { Move("Hold", "地图或参数版本变化，请核对原运行。"); return; }
            if (run.Stage == "Executing")
            {
                if (o.CurrentTaskState is "Faulted" or "Cancelled" or "CancellationReview" || o.CurrentTaskState is null)
                { Move("Hold", "原业务任务异常或未找到，不计完成也不启动下一工位。"); return; }
                if (run.Plan.Mode == "Cycle") await port.SimulatorFeedbackAsync(run, ct);
                if (o.CurrentTaskState != "Completed" || !o.AllClosed || o.AnyActiveTasks) return;
                run = run with { CompletedJobs = run.CompletedJobs + 1, TaskId = null, PendingRequestId = null };
                Move("Waiting", "原工位任务及握手已闭合，检查下一呼叫。");
                return;
            }
            if (run.Stage == "Returning")
            {
                if (!o.StandbyCompleted || !o.Stopped || o.CurrentSite != run.Plan.StandbySite) return;
                await port.ReleaseReturnAsync(run, ct);
                run = run with { VendorTaskId = null, PendingRequestId = null }; ownership.Returning = false;
                Move("Waiting", "已核对到达待机点，检查途中新增呼叫。");
                return;
            }
            if (run.Stage != "Waiting") { Move("Hold", "存在未确定的发送意图，禁止自动重发。"); return; }
            var finished = run.Plan.Mode == "Cycle" && run.CompletedJobs >= run.Plan.Rounds * run.Plan.Stations.Length;
            var next = run.Plan.Stations.FirstOrDefault(s => o.Calls.Contains(s));
            if (next is null && o.Calls.Length > 0)
            { run = run with { Message = "有未参与工位呼叫，保留呼叫等待处理，不宣告无任务返回待机。" }; Save(); return; }
            if (next is null && !run.StopRequested && run.Plan.Mode == "Cycle" &&
                run.CompletedJobs < run.Plan.Rounds * run.Plan.Stations.Length &&
                DateTimeOffset.UtcNow - run.UpdatedAt < TimeSpan.FromSeconds(10))
                return; // The PLC tool raises the first simulated call just after the run is armed.
            if (next is not null)
            {
                if (o.AnyActiveTasks || !o.Stopped) return;
                if (finished) { Move("Hold", "轮数已达到但仍有呼叫，保留现场信号待核对。"); return; }
                var id = Guid.NewGuid().ToString("N");
                run = run with { PendingRequestId = id }; Move("Creating", "正在受理 " + next);
                ownership.RequestId = id;
                string taskId;
                try { taskId = await port.CreateAsync(run, next, id, ct); }
                finally { ownership.RequestId = null; }
                run = run with { TaskId = taskId }; Move("Executing", "正在执行 " + next + "；等待九步及握手全部闭合。");
                return;
            }
            if (!o.AllClosed || o.AnyActiveTasks || !o.Stopped) return;
            if (o.CurrentSite != run.Plan.StandbySite)
            {
                run = run with { PendingRequestId = Guid.NewGuid().ToString("N") };
                ownership.Returning = true; Move("ReturnSending", "原任务闭合且无呼叫，准备返回待机点。");
                var vendorId = await port.ReturnAsync(run, ct);
                run = run with { VendorTaskId = vendorId }; Move("Returning", "返回待机中；新增呼叫到达待机后接续，途中不改派。");
                return;
            }
            if (run.StopRequested || finished)
            {
                if (run.Plan.Mode == "Cycle") await port.SimulatorAsync(run, true, ct);
                Move("Completed", "全部任务、握手闭合并已返回待机点。"); ownership.Set(null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { if (run is not null && run.Stage is not ("Completed" or "Hold")) Move("Hold", "运行中断，保留原任务及发送意图，禁止重发。"); }
        catch (Exception e) { if (run is not null) { ownership.Returning = true; Move("Hold", "运行暂停待核对：" + e.Message); } }
        finally { mutex.Release(); }
    }
}

public sealed partial class BusinessRunOwnership : IBusinessRunInterlock
{
    private string? vehicle;
    public bool Active => Volatile.Read(ref vehicle) is not null;
    public string? Vehicle => Volatile.Read(ref vehicle);
    public bool Returning { get; set; }
    public string? RequestId { get; set; }
    public void Set(string? id) => Volatile.Write(ref vehicle, id);
}
