using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

var databasePath = Path.Combine(
    Path.GetTempPath(),
    $"agv-dispatch-outbox-smoke-{Guid.NewGuid():N}.db");

try
{
    var database = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = databasePath });
    var initializer = new DatabaseInitializer(database);
    var tasks = new DispatchTaskRepository(database);
    var orchestration = new DispatchOrchestrationRepository(database);
    var activeTasks = new DispatchTaskReadRepository(database);
    var outbox = new AgvCommandOutboxRepository(database);
    await initializer.InitializeAsync(CancellationToken.None);

    var created = await tasks.TryCreateAsync(
        "CNC001",
        "DUMPER-001",
        10,
        CancellationToken.None);
    Ensure(created.Created, "应成功创建业务任务。");
    var steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    Ensure(steps.Count == 9, "包含倒料等待位和倒料位的满箱任务必须拆成9个步骤。");
    Ensure(steps.All(step => step.Status == DispatchTaskStepStatus.Pending),
        "PLC应答前所有步骤应为Pending。");

    await tasks.MarkAcknowledgedAsync(created.Task.TaskId, CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    Ensure(steps[0].Status == DispatchTaskStepStatus.Ready,
        "PLC应答后首步骤应为Ready。");
    var activatedAt = steps[0].StartedAt;
    Ensure(activatedAt is not null, "步骤激活时应持久化计时起点。");
    Ensure(steps.Skip(1).All(step => step.Status == DispatchTaskStepStatus.Pending),
        "PLC应答后其余步骤仍应为Pending。");

    var prepared = await outbox.TryPrepareTransportStepAsync(
        created.Task.TaskId,
        steps[0].StepId,
        "AGV-01",
        new[] { "PARK-01", "CNC001-PICKUP" },
        null,
        CancellationToken.None);
    Ensure(prepared, "应成功原子创建首条AGV命令。");
    Ensure(!await outbox.TryPrepareTransportStepAsync(
            created.Task.TaskId,
            steps[0].StepId,
            "AGV-01",
            new[] { "PARK-01", "CNC001-PICKUP" },
            null,
            CancellationToken.None),
        "同一步骤不得重复创建Outbox命令。");

    var due = await outbox.GetDueCommandsAsync(
        DateTimeOffset.Now.AddSeconds(1),
        10,
        CancellationToken.None);
    Ensure(due.Count == 1, "应有且只有一条待发送命令。");
    var claimed = await outbox.TryClaimAsync(
        due[0].CommandId,
        DateTimeOffset.Now,
        CancellationToken.None);
    Ensure(claimed is not null && claimed.AttemptCount == 1,
        "首次领取命令后尝试次数应为1。");

    var recovered = await outbox.RecoverStaleClaimsAsync(
        DateTimeOffset.Now.AddSeconds(1),
        CancellationToken.None);
    Ensure(recovered == 1, "应能回收服务中断时遗留的Sending命令。");
    due = await outbox.GetDueCommandsAsync(
        DateTimeOffset.Now.AddSeconds(1),
        10,
        CancellationToken.None);
    claimed = await outbox.TryClaimAsync(
        due.Single().CommandId,
        DateTimeOffset.Now,
        CancellationToken.None);
    Ensure(claimed is not null && claimed.AttemptCount == 2,
        "恢复后再次领取必须沿用原命令并累计尝试次数。");

    await outbox.ScheduleRetryAsync(
        claimed!.CommandId,
        "模拟网络中断",
        DateTimeOffset.Now.AddMilliseconds(-1),
        10,
        CancellationToken.None);
    due = await outbox.GetDueCommandsAsync(DateTimeOffset.Now, 10, CancellationToken.None);
    claimed = await outbox.TryClaimAsync(
        due.Single().CommandId,
        DateTimeOffset.Now,
        CancellationToken.None);
    Ensure(claimed is not null && claimed.AttemptCount == 3,
        "失败重试必须累计尝试次数。");
    await outbox.MarkSentAsync(
        claimed!.CommandId,
        "VENDOR-TASK-001",
        CancellationToken.None);

    var executing = await orchestration.GetExecutingTasksAsync(CancellationToken.None);
    Ensure(executing.Single().Stage == DispatchTaskStage.GoingToPickup,
        "命令发送成功后父任务应进入GoingToPickup。");
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    Ensure(steps[0].Status == DispatchTaskStepStatus.Executing,
        "命令发送成功后首步骤应进入Executing。");
    Ensure(steps[0].VendorTaskId == "VENDOR-TASK-001",
        "厂家任务号必须同时写入步骤。");
    Ensure(steps[0].StartedAt == activatedAt, "发送和重试不得重置已激活步骤的计时起点。");

    await orchestration.CompleteExecutingStepAsync(
        created.Task.TaskId,
        "VENDOR-TASK-001",
        CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    Ensure(steps[0].Status == DispatchTaskStepStatus.Completed,
        "AGV到达CNC后首步骤应完成。");
    Ensure(steps[1].Status == DispatchTaskStepStatus.Ready,
        "AGV到达CNC后只能激活取满箱步骤，不能跳到倒料机。");
    Ensure(steps[1].StartedAt is not null, "PLC等待步骤激活时应开始计时。");
    executing = await orchestration.GetExecutingTasksAsync(CancellationToken.None);
    Ensure(executing.Count == 0,
        "取箱PLC握手步骤不应被当作执行中的AGV命令查询。");
    var readySteps = await orchestration.GetReadyStepsAsync(10, CancellationToken.None);
    Ensure(readySteps.Single().TaskStage == DispatchTaskStage.PickingUp,
        "到达CNC后父任务应进入PickingUp。");
    Ensure((await orchestration.GetAssignedTasksAsync(CancellationToken.None)).Count == 1,
        "等待PLC握手期间任务和车辆必须继续保持占用。");

    await orchestration.CompleteReadyStepAsync(
        created.Task.TaskId,
        steps[1].StepId,
        CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await PrepareAndSendAsync(
        outbox, created.Task.TaskId, steps[2].StepId,
        "AGV-01", "CNC001-PICKUP", "DUMPER-WAIT", "VENDOR-TASK-002");
    Ensure((await orchestration.GetExecutingTasksAsync(CancellationToken.None))
            .Single().Stage == DispatchTaskStage.GoingToDumper,
        "送满箱命令发送后应进入GoingToDumper。");
    await orchestration.CompleteExecutingStepAsync(
        created.Task.TaskId, "VENDOR-TASK-002", CancellationToken.None);

    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await orchestration.CompleteReadyStepAsync(
        created.Task.TaskId, steps[3].StepId, CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await PrepareAndSendAsync(
        outbox, created.Task.TaskId, steps[4].StepId,
        "AGV-01", "DUMPER-WAIT", "DUMPER-DUMP", "VENDOR-TASK-003");
    await orchestration.CompleteExecutingStepAsync(
        created.Task.TaskId, "VENDOR-TASK-003", CancellationToken.None);

    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await orchestration.CompleteReadyStepAsync(
        created.Task.TaskId, steps[5].StepId, CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await PrepareAndSendAsync(
        outbox, created.Task.TaskId, steps[6].StepId,
        "AGV-01", "DUMPER-DUMP", "CNC001-PICKUP", "VENDOR-TASK-004");
    await orchestration.CompleteExecutingStepAsync(
        created.Task.TaskId, "VENDOR-TASK-004", CancellationToken.None);

    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await orchestration.CompleteReadyStepAsync(
        created.Task.TaskId, steps[7].StepId, CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    await orchestration.CompleteReadyStepAsync(
        created.Task.TaskId, steps[8].StepId, CancellationToken.None);
    steps = await tasks.GetStepsAsync(created.Task.TaskId, CancellationToken.None);
    Ensure(steps.All(step => step.Status == DispatchTaskStepStatus.Completed),
        "完整闭环后9个步骤都应完成。");
    Ensure((await activeTasks.GetActiveTasksAsync(CancellationToken.None)).Count == 0,
        "PLC收尾完成后父任务应进入Completed并退出活动任务集合。");

    database.Ado.Close();
    database.Ado.Connection.Dispose();
    database.Dispose();
    SqliteConnection.ClearAllPools();
    Console.WriteLine("PASS: DispatchTaskStep + AgvCommandOutbox workflow");
}
finally
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    foreach (var path in new[] { databasePath, databasePath + "-shm", databasePath + "-wal" })
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            Console.WriteLine($"WARN: 临时数据库仍被SQLite连接池占用，将由系统临时目录清理：{path}");
        }
    }
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task PrepareAndSendAsync(
    AgvCommandOutboxRepository outbox,
    string taskId,
    string stepId,
    string vehicleId,
    string source,
    string destination,
    string vendorTaskId)
{
    Ensure(await outbox.TryPrepareTransportStepAsync(
            taskId, stepId, vehicleId, new[] { source, destination }, null,
            CancellationToken.None),
        $"步骤{stepId}应成功写入Outbox。");
    var due = await outbox.GetDueCommandsAsync(
        DateTimeOffset.Now.AddSeconds(1), 10, CancellationToken.None);
    var command = await outbox.TryClaimAsync(
        due.Single().CommandId, DateTimeOffset.Now, CancellationToken.None);
    Ensure(command is not null, $"步骤{stepId}的命令应可领取。");
    await outbox.MarkSentAsync(command!.CommandId, vendorTaskId, CancellationToken.None);
}
