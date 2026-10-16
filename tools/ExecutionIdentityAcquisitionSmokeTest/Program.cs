using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

var count = 0;
void Check(bool condition, string title) { if (!condition) throw new Exception(title); count++; Console.WriteLine("PASS " + title); }
var ct = CancellationToken.None;
var clock = new TestClock();
var plc = new TestPlc(); var vehicle = new TestVehicle(); var cache = new ExecutionIdentityCache();
var options = new ExecutionIdentityAcquisitionOptions { Enabled = true, ReadTimeoutMs = 100 };
ExecutionIdentityAcquisition Create(ExecutionIdentityAcquisitionOptions? settings = null, params IPlcExecutionIdentityReader[] readers) =>
    new(settings ?? options, cache, readers.Length == 0 ? [plc] : readers, [vehicle], clock);
var acquisition = Create();
var disabled = new ExecutionIdentityAcquisition(new(), cache, [plc], [vehicle], clock);
Check((await disabled.PollOnceAsync(ct)).Count == 0 && plc.Reads == 0 && vehicle.Reads == 0 && cache.GetPlc().Count == 0, "默认关闭身份采集且不调用任何读取");
Check(Throws(() => new ExecutionIdentityAcquisition(options, cache, [], [], clock)), "启用但没有真实源明确拒绝启动");
Check(Throws(() => Create(options, plc, plc)), "拒绝重复工位采集源");
Check(Throws(() => Create(new() { ReadTimeoutMs = 0 })), "拒绝无效读取超时配置");
Check(Throws(() => Create(new() { CoherentReadAttempts = 6 })), "限制一致读取重试次数");
Check(Throws(() => Create(options, new TestPlc { StationId = "" })), "拒绝空源编号");
var results = await acquisition.PollOnceAsync(ct);
Check(results.All(r => r.State == "Good") && plc.Reads == 3 && vehicle.Reads == 1, "PLC前后版本与字段读取以及车辆只读快照完成");
Check(cache.TryGetPlc("PLC-01", out var original) && original.Quality == SignalQuality.Good && original.IsCoherent, "一致PLC身份发布为Good");
Check(original.ObservedAt == clock.Now && cache.GetVehicles().Single().ObservedAt == clock.Now, "读取完成使用主机时间不依赖设备时钟");
var baseline = new ExecutionIdentityBaseline(original, cache.GetVehicles().Single(), clock.Now);
RecoveryIdentityVerification Verify() => ExecutionIdentityVerifier.Verify("TASK", "PLC-01", "STEP", "VENDOR", "AGV-01", cache.GetPlc().Single(),
    cache.GetVehicles().Single(), baseline, clock.Now.AddMinutes(-1), clock.Now, TimeSpan.FromSeconds(10), true);
Check(Verify().MachineVerified, "只读采集结果接入原执行身份核验");
plc.Stamp = plc.Stamp with { CycleId = "0", DeviceSessionId = "BOOT-2" }; clock.Now = clock.Now.AddSeconds(1);
await acquisition.PollOnceAsync(ct);
Check(Verify().BlockReason?.Contains("启动编号") == true, "重启后周期计数归零仍区别原设备世代");
plc.Stamp = plc.Stamp with { CycleId = "0", DeviceSessionId = "BOOT-1" };
await acquisition.PollOnceAsync(ct);
Check(Verify().BlockReason?.Contains("周期号") == true, "同世代周期回绕或复位拒绝复用原意图");
plc.Stamp = TestPlc.ValidStamp;
vehicle.Frame = vehicle.Frame with { OriginalBusinessStepId = "OTHER" };
await acquisition.PollOnceAsync(ct);
Check(Verify().BlockReason?.Contains("原运输") == true, "原运输关联不同不能靠读取成功恢复");
vehicle.Frame = TestVehicle.ValidFrame;
plc.Stamps = new([TestPlc.ValidStamp, TestPlc.ValidStamp with { SnapshotVersion = "2" }, TestPlc.ValidStamp, TestPlc.ValidStamp]);
var reads = plc.Reads; await acquisition.PollOnceAsync(ct);
Check(plc.Reads - reads == 6 && cache.GetPlc().Single().IsCoherent, "读途中字段版本变化丢弃并有界重读");
plc.Changing = true; reads = plc.Reads; results = await acquisition.PollOnceAsync(ct);
Check(plc.Reads - reads == 9 && results.Single(r => r.SourceKind == "PLC").State == "Invalid" && cache.GetPlc().Single().Quality == SignalQuality.Bad,
    "持续变化达到三次限制并使旧PLC身份失效");
Check(!Verify().MachineVerified && Verify().BlockReason != null, "不一致采集不能作为恢复依据");
plc.Changing = false; plc.Stamp = TestPlc.ValidStamp with { IsStable = false };
await acquisition.PollOnceAsync(ct);
Check(!cache.GetPlc().Single().IsCoherent, "读前后版本相同但仍在更新中不能标记一致");
plc.Stamp = TestPlc.ValidStamp; plc.Fields = plc.Fields with { StationId = "OTHER" };
await acquisition.PollOnceAsync(ct);
Check(cache.GetPlc().Single().StationId == "PLC-01" && cache.GetPlc().Single().Quality == SignalQuality.Bad && cache.GetPlc().Count == 1,
    "错误工位响应使配置源失效不污染其他工位");
plc.Fields = TestPlc.ValidFields; await acquisition.PollOnceAsync(ct);
var lastGood = cache.GetPlc().Single(); plc.Fail = true; clock.Now = clock.Now.AddSeconds(1);
results = await acquisition.PollOnceAsync(ct);
var failed = cache.GetPlc().Single();
Check(results.Single(r => r.SourceKind == "PLC").State == "Failed" && failed.Quality == SignalQuality.Bad, "PLC断连立刻降低身份质量");
Check(failed.CycleId == lastGood.CycleId && failed.ObservedAt == lastGood.ObservedAt && !failed.IsCoherent, "断连保留原读数和成功时间但不能刷新旧值");
Check(cache.GetVehicles().Single().Quality == SignalQuality.Good, "单个PLC失败不会阻止其他车辆采集");
plc.Fail = false; plc.ThrowCancelled = true;
results = await acquisition.PollOnceAsync(ct);
Check(results.Single(r => r.SourceKind == "PLC").State == "Failed", "驱动自行取消按读取失败处理不误报超时");
plc.ThrowCancelled = false; await acquisition.PollOnceAsync(ct);
Check(cache.GetPlc().Single().Quality == SignalQuality.Good && Verify().MachineVerified, "重新读取一致原身份后恢复有效质量");
vehicle.Fail = true; var vehicleTime = cache.GetVehicles().Single().ObservedAt; clock.Now = clock.Now.AddSeconds(1);
await acquisition.PollOnceAsync(ct);
Check(cache.GetVehicles().Single().Quality == SignalQuality.Bad && cache.GetVehicles().Single().ObservedAt == vehicleTime, "车辆断连不新鲜化旧容器");
vehicle.Fail = false;
foreach (var frame in new[] {
    TestVehicle.ValidFrame with { IsAtomic = false }, TestVehicle.ValidFrame with { VehicleId = "OTHER" },
    TestVehicle.ValidFrame with { LoadState = ContainerLoadState.Empty }, TestVehicle.ValidFrame with { ContainerId = null },
    TestVehicle.ValidFrame with { SchemaVersion = 2 }, TestVehicle.ValidFrame with { Quality = SignalQuality.Stale },
    TestVehicle.ValidFrame with { LoadState = (ContainerLoadState)42 } })
{
    vehicle.Frame = frame; await acquisition.PollOnceAsync(ct);
    Check(cache.GetVehicles().Single().Quality == SignalQuality.Bad, "不合法车辆载荷或非原子帧失效：" + frame);
}
vehicle.Frame = TestVehicle.ValidFrame with { LoadState = ContainerLoadState.Unknown, ContainerId = null };
await acquisition.PollOnceAsync(ct);
Check(cache.GetVehicles().Single().LoadState == ContainerLoadState.Unknown && !Verify().MachineVerified, "读取成功的未知载荷也不能解释为空载");
vehicle.Frame = TestVehicle.ValidFrame with { LoadState = ContainerLoadState.Empty, ContainerId = null };
await acquisition.PollOnceAsync(ct);
Check(Verify().MachineVerified, "明确空载仍只证明关联身份不证明动作完成");
vehicle.Frame = TestVehicle.ValidFrame; await acquisition.PollOnceAsync(ct);
var hung = new TaskCompletionSource<PlcIdentityStamp>(TaskCreationOptions.RunContinuationsAsynchronously);
plc.Hung = hung.Task; reads = plc.Reads;
results = await acquisition.PollOnceAsync(ct);
Check(results.Single(r => r.SourceKind == "PLC").State == "Timeout" && cache.GetPlc().Single().Quality == SignalQuality.Bad, "超时不等待失控驱动并使旧值失效");
await acquisition.PollOnceAsync(ct);
Check(plc.Reads == reads + 1, "忽略取消的驱动未返回前不重入设备读取");
plc.Hung = null; hung.SetResult(TestPlc.ValidStamp); await Task.Delay(30);
Check(cache.GetPlc().Single().Quality == SignalQuality.Bad, "迟到响应不能覆盖超时失效状态");
await acquisition.PollOnceAsync(ct);
Check(Verify().MachineVerified, "挂起读取结束后可重新采集新快照");
using (var cancel = new CancellationTokenSource())
{
    plc.Hung = Task.Delay(Timeout.Infinite, cancel.Token).ContinueWith<PlcIdentityStamp>(_ => throw new OperationCanceledException(cancel.Token), TaskScheduler.Default);
    cancel.CancelAfter(20); var cancelled = false;
    try { await acquisition.PollOnceAsync(cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
    Check(cancelled && cache.GetPlc().Single().Quality == SignalQuality.Disabled, "服务取消传播且不保留有效身份");
    plc.Hung = null;
}
await acquisition.PollOnceAsync(ct); acquisition.MarkStopped();
Check(cache.GetPlc().Single().Quality == SignalQuality.Disabled && cache.GetVehicles().Single().Quality == SignalQuality.Disabled, "采集停止使全部源失效");
await acquisition.PollOnceAsync(ct); options.Enabled = false; reads = plc.Reads;
Check((await acquisition.PollOnceAsync(ct)).Count == 0 && plc.Reads == reads && cache.GetPlc().Single().Quality == SignalQuality.Disabled,
    "关闭已运行的采集不再读取并使已有身份失效");
options.Enabled = true; await acquisition.PollOnceAsync(ct);
var pausedRead = new TaskCompletionSource<PlcIdentityStamp>(TaskCreationOptions.RunContinuationsAsynchronously);
plc.Hung = pausedRead.Task;
var pendingPoll = acquisition.PollOnceAsync(ct); acquisition.MarkStopped();
plc.Hung = null; pausedRead.SetResult(TestPlc.ValidStamp); await pendingPoll;
Check(cache.GetPlc().Single().Quality == SignalQuality.Disabled && cache.GetVehicles().Single().Quality == SignalQuality.Disabled,
    "采集中途停止后迟到结果不能重新发布Good");
await acquisition.PollOnceAsync(ct);
Check(cache.GetPlc().Single().Quality == SignalQuality.Good, "新采集周期可以在停止后重新建立新样本");
var services = new ServiceCollection(); services.AddSingleton(new ExecutionIdentityAcquisitionOptions()); services.AddSingleton<ExecutionIdentityCache>();
services.AddSingleton<ExecutionIdentityAcquisition>(); using (var provider = services.BuildServiceProvider())
    Check(provider.GetRequiredService<ExecutionIdentityAcquisition>().SourceCount == 0, "生产默认DI无现场驱动但关闭模式可正常构造");
var workerCache = new ExecutionIdentityCache(); var workerPlc = new TestPlc(); var workerVehicle = new TestVehicle();
var workerOptions = new ExecutionIdentityAcquisitionOptions { Enabled = true, PollIntervalMs = 100, ReadTimeoutMs = 100 };
var workerAcquisition = new ExecutionIdentityAcquisition(workerOptions, workerCache, [workerPlc], [workerVehicle]);
using (var worker = new ExecutionIdentityPollingService(workerAcquisition, workerOptions, NullLogger<ExecutionIdentityPollingService>.Instance))
{
    await worker.StartAsync(ct);
    for (var i = 0; i < 30 && workerCache.GetPlc().Count == 0; i++) await Task.Delay(10);
    Check(workerCache.GetPlc().Count == 1, "后台只读轮询采集真实注册的模拟源");
    await worker.StopAsync(ct);
    Check(workerCache.GetPlc().Single().Quality == SignalQuality.Disabled, "后台关闭不留下可恢复的Good样本");
}
Console.WriteLine($"Execution identity acquisition: {count} checks passed. Fake readers only; no device I/O.");
static bool Throws(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } catch (InvalidOperationException) { return true; } }
sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now = new(2026, 10, 13, 6, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class TestPlc : IPlcExecutionIdentityReader
{
    public static PlcIdentityStamp ValidStamp => new("BOOT-1", "100", "1", true, SignalQuality.Good);
    public static PlcIdentityFields ValidFields => new("PLC-01", "TASK", "BOX-001", "STEP", "VENDOR", SignalQuality.Good);
    public string StationId { get; set; } = "PLC-01";
    public PlcIdentityStamp Stamp = ValidStamp; public PlcIdentityFields Fields = ValidFields;
    public Queue<PlcIdentityStamp> Stamps = new(); public bool Changing, Fail, ThrowCancelled; public int Reads;
    public Task<PlcIdentityStamp>? Hung;
    public Task<PlcIdentityStamp> ReadStampAsync(CancellationToken ct)
    {
        Reads++; if (ThrowCancelled) throw new OperationCanceledException("driver aborted"); if (Fail) throw new IOException("fake PLC disconnected"); if (Hung != null) return Hung;
        return Task.FromResult(Stamps.Count > 0 ? Stamps.Dequeue() : Changing ? Stamp with { SnapshotVersion = Reads.ToString() } : Stamp);
    }
    public Task<PlcIdentityFields> ReadFieldsAsync(CancellationToken ct) { Reads++; return Task.FromResult(Fields); }
}
sealed class TestVehicle : IVehicleContainerIdentityReader
{
    public static VehicleIdentityFrame ValidFrame => new("AGV-01", ContainerLoadState.Loaded, "BOX-001", "STEP", "VENDOR", true, SignalQuality.Good);
    public string VehicleId => "AGV-01"; public VehicleIdentityFrame Frame = ValidFrame; public int Reads; public bool Fail;
    public Task<VehicleIdentityFrame> ReadAtomicAsync(CancellationToken ct) { Reads++; if (Fail) throw new IOException("fake vehicle disconnected"); return Task.FromResult(Frame); }
}
