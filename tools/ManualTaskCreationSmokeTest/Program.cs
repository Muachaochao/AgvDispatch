using System.IO.Compression;
using System.IO;
using System.IO.Pipes;
using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Agvs;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Infrastructure.Maps;
using AgvDispatch.Infrastructure.Persistence;
using AgvDispatch.Infrastructure.Persistence.Entities;
using AgvDispatch.Service;
using AgvDispatch.Wpf.ViewModels;
using AgvDispatch.Wpf.Views;
using Prism.Mvvm;

internal static partial class Program
{
    private static int _checks;
    [STAThread]
    private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try { Run(); Console.WriteLine($"PASS: {_checks} manual task creation checks"); return 0; }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static T Complete<T>(Task<T> task) { Pump(() => task.IsCompleted); return task.GetAwaiter().GetResult(); }
    private static void Complete(Task task) { Pump(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Pump(Func<bool> done)
    {
        if (done()) return;
        var frame = new DispatcherFrame(); var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (done() || DateTime.UtcNow > deadline) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop();
        if (!done()) throw new Exception("Test timed out");
    }
    private static void Require(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static void Run()
    {
        CheckLegacyMigration();
        var path = Path.Combine(Path.GetTempPath(), "agv-manual-task-" + Guid.NewGuid().ToString("N") + ".db");
        using var db = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = path });
        Complete(new DatabaseInitializer(db).InitializeAsync(default));
        var repository = new DispatchTaskRepository(db); var orchestration = new DispatchOrchestrationRepository(db);
        var states = new FixtureStates(); var gateway = new FixtureGateway(); var station = new FixtureStations(); var gate = new FixtureGate();
        var map = new ActiveMapRuntime();
        var nodes = new[] { "HOME", "PICK", "WAIT", "DUMP" }.Select((id, i) => new MapNode(id, i * 5, 0, MapNodeType.Station, true, [])).ToArray();
        var definition = new MapDefinition("fixture", "", "LocalCartesian", "m", nodes,
            Enumerable.Range(0, 3).Select(i => new MapEdge("E" + i, nodes[i].Id, nodes[i + 1].Id, 5, 1, true, true, [])).ToArray(), []);
        map.Replace(new("MAP", "VERSION", definition, DateTimeOffset.Now));
        var dispatch = new DispatchOrchestratorOptions { Enabled = true, DumperWaitingSite = "WAIT", DumperDumpingSite = "DUMP",
            StationPickupSites = new(StringComparer.OrdinalIgnoreCase) { ["CNC001"] = "PICK" } };
        var creation = new DispatchTaskCreationOptions();
        void ResetActions()
        {
            dispatch.StepActions.Clear();
            dispatch.StepActions["GoToPickup"] = new() { Operation = "JackLoad" };
            dispatch.StepActions["DeliverFullBoxToDumper"] = new() { Operation = "Wait" };
            dispatch.StepActions["EnterDumpingPosition"] = new() { Operation = "Wait" };
            dispatch.StepActions["ReturnEmptyBox"] = new() { Operation = "JackUnload" };
        }
        ResetActions();
        var plcConfiguration = new FixturePlcConfiguration();
        var parameterRuntime = new TaskParameterRuntime(dispatch, creation);
        var service = new ManualTaskCreationService(repository, station, gateway, states, map, gate, orchestration, creation, dispatch, new(), new(), plcConfiguration, parameterRuntime);
        var parameterPath = Path.Combine(Path.GetTempPath(), "agv-task-parameters-" + Guid.NewGuid().ToString("N"), "task-parameters.json");
        var parameterService = new TaskParameterSettingsService(new(parameterPath), parameterRuntime, service, new DispatchTaskReadRepository(db), map, gate);
        var initialParameters = Complete(parameterService.GetAsync(default));
        var user = TrafficUserContext.LocalOperation("isolated-test");
        var options = Complete(service.GetOptionsAsync(default));
        Require(options.Available && options.Stations.Single().Available && options.Vehicles.Single().Available, "healthy options available");
        dispatch.MaxConcurrentTasks = 2;
        var unsafeMulti = Complete(service.GetOptionsAsync(default));
        Require(!unsafeMulti.Available && unsafeMulti.Stations.Single().PreviewSteps[2].ValidationErrors.Any(e => e.Contains("候车位")),
            "unsafe multi-car waiting bay is reported at transport step 3 before creation");
        var deniedMulti = Complete(service.CreateAsync(user, new(Guid.NewGuid().ToString("N"), "CNC001", "AGV-01", 20, "MAP", "VERSION")
            { ParameterRevision = parameterRuntime.Revision }, default));
        Require(!deniedMulti.Accepted && db.Queryable<DispatchTaskEntity>().Count() == 0,
            "unsafe multi-car map cannot persist a manual task or bypass the capacity guard");
        dispatch.MaxConcurrentTasks = 1;
        var preview = options.Stations.Single().PreviewSteps;
        Require(preview.Count == 9 && preview[2].DestinationSite == "WAIT" && preview[4].DestinationSite == "DUMP" && preview[6].DestinationSite == "PICK",
            "preview resolves configured pickup waiting dumping and return sites");
        Require(preview[1].WaitingCondition.Contains("BoxPresent=false") && preview[3].WaitingCondition.Contains("DumperReady=true") &&
            preview[5].WaitingCondition.Contains("DumpingCompletedMayLeave=true") && preview[7].WaitingCondition.Contains("BoxPresent=true"), "preview describes actual PLC completion signals");
        Require(preview[8].WaitingCondition.Contains("不等待") && preview[8].PlcHandshake.Contains("EmptyBoxTaskAcknowledged"), "final handshake describes writes without claiming readback wait");
        dispatch.StepActions["GoToPickup"] = new() { Operation = "JackLoad", JackHeight = 42, UsePgv = true };
        Require(Complete(service.GetOptionsAsync(default)).Stations.Single().PreviewSteps[0].VehicleAction.Contains("JackLoad，JackHeight=42，UsePgv=True"), "preview reads actual configured vehicle action parameters");
        ResetActions();
        ManualTaskCreationRequest Request() => new(Guid.NewGuid().ToString("N"), "CNC001", "AGV-01", 20, "MAP", "VERSION");
        var request = Request();
        var requiredPoint = plcConfiguration.Signals.Single(point => point.StationId == "CNC001" && point.VariableName == "EmptyBoxPlaced");
        foreach (var invalid in new[] { requiredPoint with { Enabled = false }, requiredPoint with { Access = "Read" },
            requiredPoint with { DataType = "Int" }, requiredPoint with { Address = "DB1.DBX0.8" } })
        {
            plcConfiguration.Replace(requiredPoint, invalid);
            var invalidOptions = Complete(service.GetOptionsAsync(default));
            Require(!invalidOptions.Stations.Single().ConfigurationValid && invalidOptions.Stations.Single().PreviewSteps[7].ValidationErrors.Any(error => error.Contains("EmptyBoxPlaced")),
                "invalid PLC mapping locates step 8: " + invalid);
            var rejected = Complete(service.CreateAsync(user, request, default));
            Require(!rejected.Accepted && rejected.Message.Contains("第 8 步") && Complete(db.Queryable<DispatchTaskEntity>().CountAsync()) == 0, "backend blocks invalid mapping before persistence");
            plcConfiguration.Options.AllowWrites = false;
            Require(service.ValidateParameters(initialParameters.Configuration).Any(row => row.Step.Sequence == 8 &&
                row.Step.ValidationErrors.Any(error => error.Contains("EmptyBoxPlaced"))),
                "preparation still rejects disabled output, wrong direction, non-Bool or malformed address with writes off");
            plcConfiguration.Options.AllowWrites = true;
            plcConfiguration.Replace(invalid, requiredPoint);
        }
        plcConfiguration.Signals.Remove(requiredPoint);
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted && Complete(service.GetOptionsAsync(default)).Stations.Single().PreviewSteps[8].ValidationErrors.Any(), "missing output prevents both placement and final handshake");
        plcConfiguration.Signals.Add(requiredPoint);
        plcConfiguration.Options.AllowWrites = false;
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "runtime PLC write switch blocks task creation"); plcConfiguration.Options.AllowWrites = true;
        plcConfiguration.Options.Enabled = false;
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "owning PLC disabled blocks task creation"); plcConfiguration.Options.Enabled = true;
        dispatch.StepActions.Remove("ReturnEmptyBox");
        Require(Complete(service.GetOptionsAsync(default)).Stations.Single().PreviewSteps[6].ValidationErrors.Any() && !Complete(service.CreateAsync(user, request, default)).Accepted,
            "missing step 7 action rejected after previously valid options"); ResetActions();
        dispatch.StepActions["EnterDumpingPosition"] = new() { Operation = "Invalid" };
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "unsupported action rejected"); ResetActions();
        dispatch.StepActions["EnterDumpingPosition"] = new() { Operation = "JackHeight", JackHeight = double.NaN };
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "nonfinite height rejected"); ResetActions();
        dispatch.StepActions["EnterDumpingPosition"] = new() { Operation = "JackHeight" };
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "height action without height rejected"); ResetActions();
        Require(!Complete(service.CreateAsync(user, request with { Priority = 101 }, default)).Accepted, "priority range validated");
        Require(!Complete(service.CreateAsync(user, request with { StationId = "UNKNOWN" }, default)).Accepted, "unknown station rejected");
        Require(!Complete(service.CreateAsync(user, request with { RequestedVehicleId = "UNKNOWN" }, default)).Accepted, "unknown vehicle rejected");
        Require(!Complete(service.CreateAsync(user, request with { MapVersionId = "OLD" }, default)).Accepted, "stale active map rejected");
        station.Allowed = false; Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "PLC readiness rechecked"); station.Allowed = true;
        states.Blocked = true; Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "blocked vehicle rejected"); states.Blocked = false;
        gateway.Connection = ConnectionState.Disconnected; Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "offline vehicle rejected"); gateway.Connection = ConnectionState.Online;
        gate.Allowed = false; Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "map gate rejects new work"); gate.Allowed = true;
        map.Replace(new("MAP", "VERSION", definition with { Edges = [] }, DateTimeOffset.Now));
        Require(!Complete(service.CreateAsync(user, request, default)).Accepted, "disconnected map rejected");
        Require(Complete(service.GetOptionsAsync(default)).Stations.Single().PreviewSteps[2].ConfigurationNote.Contains("路径缺失"), "preview identifies unavailable transport leg");
        Require(Complete(service.GetOptionsAsync(default)).Stations.Single().PreviewSteps.Where(step => step.Sequence is 3 or 5 or 7).All(step => step.ValidationErrors.Any()), "each disconnected transport leg has its own validation error");
        map.Replace(new("MAP", "VERSION", definition, DateTimeOffset.Now));
        var preflight = new FixturePlcConfiguration.Preflight();
        var guarded = new ManualTaskCreationService(repository, station, gateway, states, map, gate, orchestration, creation, dispatch, new(), new(), plcConfiguration, parameterRuntime, preflight);
        Require(!Complete(guarded.CreateAsync(user,request,default)).Accepted && Complete(db.Queryable<DispatchTaskEntity>().CountAsync())==0,"failed vehicle map preflight does not persist a task");
        preflight.Accept=true;
        Require(!Complete(guarded.CreateAsync(user,request with {RequestedVehicleId=null},default)).Accepted,"automatic vehicle selection cannot skip vehicle map preflight");
        preflight.After=()=>states.Blocked=true;
        Require(!Complete(guarded.CreateAsync(user,request,default)).Accepted && Complete(db.Queryable<DispatchTaskEntity>().CountAsync())==0,"vehicle changed during map preflight rejects commit");
        states.Blocked=false;preflight.After=null;
        var created = Complete(guarded.CreateAsync(user, request, default));
        Require(created.Accepted && created.Created && created.TaskId is not null, "task created after validation");
        var taskSteps = Complete(repository.GetStepsAsync(created.TaskId!, default));
        Require(taskSteps.Count == 9 && taskSteps.All(step => step.Status == DispatchTaskStepStatus.Pending), "nine steps wait for PLC acknowledgement");
        Require(taskSteps.Select(step => (step.Sequence, step.StepType)).SequenceEqual(preview.Select(step => (step.Sequence, step.StepType))), "preview order and types match persisted execution steps");
        var entity = Complete(db.Queryable<DispatchTaskEntity>().Where(task => task.TaskId == created.TaskId).FirstAsync());
        Require(entity.RequestedVehicleId == "AGV-01" && entity.CreatedBy == "isolated-test" && entity.CreateMapVersionId == "VERSION", "request vehicle operator and map version persisted");
        var replay = Complete(service.CreateAsync(user, request, default));
        Require(replay.Accepted && !replay.Created && replay.TaskId == created.TaskId, "same request id is idempotent");
        Require(!Complete(service.CreateAsync(user, request with { Priority = 21 }, default)).Accepted, "request id cannot be reused for different parameters");
        Require(!Complete(service.CreateAsync(user, Request(), default)).Accepted && Complete(db.Queryable<DispatchTaskEntity>().CountAsync()) == 1, "duplicate station cannot create another active task");
        gate.Allowed = false; Require(Complete(service.CreateAsync(user, request, default)).Accepted, "uncertain-result replay returns receipt despite changed gate"); gate.Allowed = true;
        Complete(repository.MarkAcknowledgedAsync(created.TaskId!, default));
        var candidate = Complete(orchestration.GetDispatchCandidatesAsync(10, default)).Single();
        Require(candidate.RequestedVehicleId == "AGV-01" && TaskVehicleSelection.Accepts(candidate, "AGV-01") && !TaskVehicleSelection.Accepts(candidate, "AGV-02"), "dispatcher respects requested vehicle");
        Require(Complete(repository.GetStepsAsync(created.TaskId!, default))[0].Status == DispatchTaskStepStatus.Ready, "PLC acknowledgement activates first step");

        var query = new TaskCenterQueryService(new TaskCenterReadRepository(db), new FixtureSignals(), creation, parameterRuntime);
        var cancellation = new TaskCancellationService(db, gate, new FixtureGateway());
        var historyExports = new TaskHistoryExportService(new DatabaseOptions { DatabasePath = path });
        var recovery = new ExecutionRecoveryService(db, new RecoveryBarrier(), gate, new FixtureGateway(), states, new FixtureSignals(), new(), map);
        var args = typeof(TrafficControlNamedPipeServer).GetConstructors().Single().GetParameters().Select(parameter =>
            parameter.ParameterType == typeof(IManualTaskCreationService) ? (object)service :
            parameter.ParameterType == typeof(ITaskParameterService) ? parameterService :
            parameter.ParameterType == typeof(ITaskCancellationService) ? cancellation :
            parameter.ParameterType == typeof(ITaskHistoryExportService) ? historyExports :
            parameter.ParameterType == typeof(IExecutionRecoveryService) ? recovery :
            parameter.ParameterType == typeof(ITaskCenterQueryService) ? query : null).ToArray();
        var server = (TrafficControlNamedPipeServer)Activator.CreateInstance(typeof(TrafficControlNamedPipeServer), args)!;
        var dispatchMethod = typeof(TrafficControlNamedPipeServer).GetMethod("DispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pipeReply = Complete((Task<TrafficControlPipeResponse>)dispatchMethod.Invoke(server,
            [user, new TrafficControlPipeRequest(TrafficControlOperations.CreateTask, JsonSerializer.SerializeToElement(request)), CancellationToken.None])!);
        Require(pipeReply.Success && pipeReply.Payload.GetProperty("accepted").GetBoolean(), "IPC dispatcher routes creation and serializes receipt");
        var pipeName = "AgvManualTaskTest-" + Guid.NewGuid().ToString("N");
        Complete(db.Insertable(new ExecutionEvidenceEntity { Id = "pipe-history", TaskId = created.TaskId!, Kind = "TaskCancellationProcessingFailed", Operator = "隔离测试", Reason = "仅验证历史传输", Evidence = "{\"fixture\":true}", OccurredAt = DateTime.Now }).ExecuteCommandAsync());
        var pipeFixture = Task.Run(async () =>
        {
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            for (var i = 0; i < 15; i++)
            {
                await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync();
                var prefix = new byte[4]; await pipe.ReadExactlyAsync(prefix);
                var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)]; await pipe.ReadExactlyAsync(payload);
                var message = JsonSerializer.Deserialize<TrafficControlPipeRequest>(payload, json)!;
                var reply = await (Task<TrafficControlPipeResponse>)dispatchMethod.Invoke(server, [user, message, CancellationToken.None])!;
                var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, json);
                BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
                await pipe.WriteAsync(prefix); await pipe.WriteAsync(bytes); await pipe.FlushAsync();
            }
        });
        using (var wire = new AgvDispatch.Wpf.Services.NamedPipeTrafficControlClient(pipeName))
        {
            Require(!Complete(wire.CancelDispatchTaskAsync(new(Guid.NewGuid().ToString("N"), "missing", DispatchTaskStage.Created, "原任务不存在因此不能提交取消操作请求"), default)).Accepted, "actual pipe cancellation routes to backend validation");
            Require(!Complete(wire.BeginTaskRecoveryReviewAsync(new("missing", "原任务不存在因此不能建立现场恢复核对"), default)).Accepted, "actual pipe runtime recovery routes to backend validation");
            Require(Complete(wire.GetTaskDetailAsync(created.TaskId!, default))!.OperationHistory.Count == 0, "detail omits legacy history when frontend queries separate pages");
            Require(Complete(wire.GetTaskHistoryAsync(new(created.TaskId!, TaskHistoryCategory.Cancellation), default)).Items.Any(row => row.Id == "E:pipe-history"), "history pagination survives isolated pipe serialization");
            var exportedWire = Complete(wire.ExportTaskHistoryAsync(new(new(created.TaskId!, TaskHistoryCategory.Cancellation)), default));
            Require(exportedWire.Accepted && exportedWire.RecordCount==1 && File.Exists(exportedWire.FilePath), "export endpoint generates filtered evidence package through actual pipe");
            var wireCancelId=Guid.NewGuid().ToString("N");
            Require(Complete(wire.CancelTaskHistoryExportAsync(new(wireCancelId),default)).Accepted,"actual pipe handles pre-registration cancellation");
            Require(Complete(wire.GetTaskHistoryExportProgressAsync(new(wireCancelId),default)).Stage=="Cancelled","actual pipe returns export cancellation progress");
            Require(Complete(wire.GetTaskHistoryExportCacheAsync(new(7),default)).TotalCount>0,"actual pipe returns cache list and retention summary");
            Require(Complete(wire.CleanupTaskHistoryExportCacheAsync(new(3650),default)).DeletedCount==0,"actual pipe cleanup respects protected fresh cache");
            var wireOptions = Complete(wire.GetTaskCreationOptionsAsync(default));
            var wirePreview=Complete(wire.GetTaskRoutePreviewAsync(new("CNC001","AGV-01","MAP","VERSION",wireOptions.ParameterRevision),default));
            Require(!wirePreview.Verified&&wirePreview.Legs.Count==4,"current route preview survives real pipe dispatcher and client serialization");
            Require(wirePreview.Map is {Nodes.Count:4,Edges.Count:3}&&wirePreview.Map.ObservationId==wirePreview.ObservationId,"same-batch navigation geometry survives real pipe dispatcher and client");
            Require(wirePreview.Steps.Count==9&&wirePreview.Steps[3].PlcStationId==creation.DumperStationId,"same-observation actions and PLC metadata survive real pipe serialization");
            Require(wireOptions.MapVersionId == "VERSION", "actual pipe client loads creation options");
            Require(wireOptions.Stations.Single().PreviewSteps.Count == 9 && wireOptions.Stations.Single().PreviewSteps[3].PlcStationId == creation.DumperStationId,
                "nine step preview survives actual pipe serialization");
            Require(Complete(wire.CreateTaskAsync(request, default)).TaskId == created.TaskId, "actual pipe client submits and reads replay receipt");
            Require(Complete(wire.GetTaskParametersAsync(default)).Configuration.Actions.Count == 4, "actual pipe client loads editable task parameters");
            Require(Complete(wire.ValidateTaskParametersAsync(new(Guid.NewGuid().ToString("N"), "initial", "MAP", "VERSION", initialParameters.Configuration), default)).Accepted,
                "actual pipe client validates candidate through shared nine step validator");
            Require(!Complete(wire.SaveTaskParametersAsync(new(Guid.NewGuid().ToString("N"), "initial", "MAP", "VERSION", initialParameters.Configuration), default)).Accepted,
                "actual pipe save operation routes to active-task guard");
        }
        Complete(pipeFixture);

        var app = new AgvDispatch.Wpf.App(); app.InitializeComponent();
        CheckRoutePreview(app);
        var client = DispatchProxy.Create<ITrafficControlClient, CreationClient>();
        var proxy = (CreationClient)(object)client; proxy.HistoryExports = historyExports; proxy.Creation = service; proxy.Parameters = parameterService; proxy.Query = query; proxy.User = user;
        var vm = new TaskCenterViewModel(client); vm.OnNavigatedTo(null!); Pump(() => !vm.IsBusy);
        vm.IsCreationOpen = true; Pump(() => !vm.IsCreating);
        Require(vm.CreationStations.Count == 1 && vm.CreationVehicles.Count == 2, "UI loads real station and auto/specific choices");
        Require(vm.CreationStepPreview.Count == 9 && vm.CreationStepPreview[0].Location.Contains("派车时确定起点"), "automatic preview does not invent an assigned vehicle start");
        vm.CreationVehicle = vm.CreationVehicles.Last();
        Require(vm.CreationStepPreview[0].Location.Contains("HOME → PICK") && vm.CreationStepPreview.All(step => step.VehicleAction.Contains("AGV-01")), "selected vehicle refreshes preview origin and vehicle label");
        vm.CreationStation = null;
        Require(vm.CreationStepPreview.Count == 0, "clearing station clears stale preview");
        vm.CreationStation = vm.CreationStations.Single();
        plcConfiguration.Signals.Remove(requiredPoint);
        Complete(vm.LoadCreationOptionsAsync());
        Require(vm.CreationStepPreview.Count == 9 && vm.CreationStepPreview[7].HasErrors && !vm.SubmitTaskCommand.CanExecute(), "UI keeps invalid station preview and disables submission");
        plcConfiguration.Signals.Add(requiredPoint); Complete(vm.LoadCreationOptionsAsync());
        vm.CreationPriorityInput = "bad"; Require(!vm.SubmitTaskCommand.CanExecute(), "bad numeric input cannot submit old value");
        vm.CreationPriorityInput = "20";
        ViewModelLocationProvider.SetDefaultViewModelFactory(_ => vm);
        var view = new TaskCenterView { DataContext = vm, Width = 1280, Height = 920, Background = (Brush)app.FindResource("AppBackgroundBrush") };
        view.Measure(new Size(1280, 920)); view.Arrange(new Rect(0, 0, 1280, 920)); view.UpdateLayout();
        var image = new RenderTargetBitmap(1280, 920, 96, 96, PixelFormats.Pbgra32); image.Render(view);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var output = Path.Combine(AppContext.BaseDirectory, "task-creation.png"); using (var file = File.Create(output)) encoder.Save(file);
        Require(vm.Detail?.Steps.Count == 9 && vm.Detail.Task.RequestedVehicleId == "AGV-01", "created task detail displays nine steps and requested vehicle");
        Complete(db.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.Completed).Where(task => task.TaskId == created.TaskId).ExecuteCommandAsync());
        Complete(vm.LoadCreationOptionsAsync()); vm.CreationStation = vm.CreationStations.Single(); vm.CreationVehicle = vm.CreationVehicles.Last();
        proxy.FailAfterCommit = true;
        Complete(vm.SubmitTaskAsync()); var uncertain = proxy.LastRequest;
        Require(vm.CreationMessage.Contains("尚未确认"), "lost response retains uncertain status");
        Complete(vm.SubmitTaskAsync());
        Require(proxy.LastRequest!.RequestId == uncertain!.RequestId && Complete(db.Queryable<DispatchTaskEntity>().CountAsync()) == 2,
            "UI retries same request after committed response loss without duplicate");
        Require(vm.Detail?.Task.TaskId == proxy.LastCreatedId && vm.Steps.Count == 9 && vm.Search == proxy.LastCreatedId,
            $"successful submission selects exact task and refreshes steps: selected={vm.Detail?.Task.TaskId}, submitted={proxy.LastCreatedId}, search={vm.Search}, steps={vm.Steps.Count}, message={vm.CreationMessage}");
        view.Height = 760; view.Measure(new Size(1280, 760)); view.Arrange(new Rect(0, 0, 1280, 760)); view.UpdateLayout();
        var submit = (System.Windows.Controls.Button)view.FindName("SubmitTaskButton");
        Require(submit.Visibility == Visibility.Visible && submit.ActualHeight > 0 && submit.TranslatePoint(new Point(0, submit.ActualHeight), view).Y < 760,
            $"compact window keeps creation button accessible: visible={submit.IsVisible}, height={submit.ActualHeight}, y={submit.TranslatePoint(new Point(0, submit.ActualHeight), view).Y}");
        var compact = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); compact.Render(view);
        var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compact));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-creation-compact.png"))) compactEncoder.Save(file);
        var formScroll = (System.Windows.Controls.ScrollViewer)view.FindName("CreationFormScroll");
        var previewScroll = (System.Windows.Controls.ScrollViewer)view.FindName("CreationPreviewScroll");
        formScroll.ScrollToEnd(); previewScroll.ScrollToEnd(); view.UpdateLayout();
        var previewList = (System.Windows.Controls.ItemsControl)view.FindName("CreationStepPreviewList");
        var lastStep = (FrameworkElement)previewList.ItemContainerGenerator.ContainerFromIndex(8);
        var lastBottom = lastStep.TranslatePoint(new Point(0, lastStep.ActualHeight), view).Y;
        Require(lastBottom > 0 && lastBottom < 760 && previewScroll.VerticalOffset > 0, "compact preview can scroll to final PLC handshake step");
        var previewImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); previewImage.Render(view);
        var previewEncoder = new PngBitmapEncoder(); previewEncoder.Frames.Add(BitmapFrame.Create(previewImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-creation-preview-last.png"))) previewEncoder.Save(file);
        var runningTaskId = vm.Detail!.Task.TaskId;
        var runningSteps = Complete(repository.GetStepsAsync(runningTaskId, default));
        var now = DateTime.Now;
        Complete(db.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.PickingUp).Where(task => task.TaskId == runningTaskId).ExecuteCommandAsync());
        Complete(db.Updateable<DispatchTaskStepEntity>().SetColumns(step => new DispatchTaskStepEntity
            { Status = (int)DispatchTaskStepStatus.Completed, StartedAt = now.AddMinutes(-2), CompletedAt = now.AddSeconds(-90) })
            .Where(step => step.StepId == runningSteps[0].StepId).ExecuteCommandAsync());
        Complete(db.Updateable<DispatchTaskStepEntity>().SetColumns(step => new DispatchTaskStepEntity
            { Status = (int)DispatchTaskStepStatus.Ready, StartedAt = now.AddSeconds(-90) })
            .Where(step => step.StepId == runningSteps[1].StepId).ExecuteCommandAsync());
        var timedProfile = parameterRuntime.Capture() with { StepTimeouts = parameterRuntime.Capture().StepTimeouts.Select(row => row.StepType == DispatchTaskStepType.PickupFullBox ? row with { Seconds = 60 } : row).ToArray() };
        parameterRuntime.Apply(new(1, "initial", "", "MAP", "VERSION", timedProfile, []));
        vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy);
        Require(vm.Steps[1].IsTimedOut && vm.Steps[1].TimeoutText.Contains("已超时") && vm.Steps[1].WaitingExplanation.Contains("BoxPresent"), "UI IPC detail includes timeout threshold and waiting reason");
        Require(vm.Steps[1].IsCurrent && vm.Steps[0].ElapsedText == "00:00:30" && vm.Steps[1].WaitingExplanation.Contains("BoxPresent"), "UI refresh receives current step elapsed and unmet PLC reason");
        vm.IsCreationOpen = false; view.Height = 920; view.Measure(new Size(1280, 920)); view.Arrange(new Rect(0, 0, 1280, 920)); view.UpdateLayout();
        var timelineScroll = (System.Windows.Controls.ScrollViewer)view.FindName("TaskTimelineScroll"); timelineScroll.ScrollToVerticalOffset(140); view.UpdateLayout();
        var timelineImage = new RenderTargetBitmap(1280, 920, 96, 96, PixelFormats.Pbgra32); timelineImage.Render(view);
        var timelineEncoder = new PngBitmapEncoder(); timelineEncoder.Frames.Add(BitmapFrame.Create(timelineImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-current-step.png"))) timelineEncoder.Save(file);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "task-current-step.png"), Path.Combine(AppContext.BaseDirectory, "task-step-timeout.png"), true);
        parameterRuntime.Apply(new(1, "initial", "", "MAP", "VERSION", initialParameters.Configuration, []));
        vm.IsParametersOpen = true; Pump(() => !vm.IsParametersBusy);
        Require(vm.ParameterActions.Count == 4 && vm.ParameterPickups.Count == 1 && !vm.SaveTaskParametersCommand.CanExecute(), "editor loads profile and disables save during unfinished task");
        TaskParameterRequest ParameterRequest(TaskParameterConfiguration configuration, string? revision = null) => new(Guid.NewGuid().ToString("N"), revision ?? parameterRuntime.Revision, "MAP", "VERSION", configuration);
        Require(!Complete(parameterService.SaveAsync(user, ParameterRequest(initialParameters.Configuration), default)).Accepted && !File.Exists(parameterPath), "backend active-task guard leaves runtime and file unchanged");
        Complete(db.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.Faulted).Where(task => task.TaskId == runningTaskId).ExecuteCommandAsync());
        Require(!Complete(parameterService.SaveAsync(user, ParameterRequest(initialParameters.Configuration), default)).Accepted, "faulted task also blocks parameter replacement");
        Complete(db.Updateable<DispatchTaskEntity>().SetColumns(task => task.Stage == (int)DispatchTaskStage.Completed).Where(task => task.TaskId == runningTaskId).ExecuteCommandAsync());
        Complete(vm.LoadTaskParametersAsync());
        Require(vm.SaveTaskParametersCommand.CanExecute(), "completed tasks allow parameter editing save");
        gate.RecoveryOpen = false; plcConfiguration.Options.AllowWrites = false;
        var preparationSnapshot = Complete(parameterService.GetAsync(default));
        Require(preparationSnapshot.CanSave && !gate.CanStartNewWork && preparationSnapshot.Explanation.Contains("不会放行"),
            "configuration preparation remains available with startup recovery and PLC writes closed");
        var preparationValidation = Complete(parameterService.ValidateAsync(ParameterRequest(initialParameters.Configuration), default));
        Require(preparationValidation.Accepted && preparationValidation.Message.Contains("不代表执行许可"),
            "preparation validates mappings without requiring live write permission");
        Require(!Complete(service.GetOptionsAsync(default)).Stations.Single().ConfigurationValid,
            "actual task readiness still requires live PLC write permission");
        vm.WaitingSiteInput = "MISSING"; Complete(vm.ValidateTaskParametersAsync());
        Require(vm.ParameterValidationSteps.Any(row => row.Step.Sequence == 3 && row.Step.ValidationErrors.Count > 0), "candidate route error shown for specific step without applying it");
        Complete(vm.SaveTaskParametersAsync()); Require(!File.Exists(parameterPath) && dispatch.DumperWaitingSite == "WAIT", "invalid candidate cannot persist or apply");
        vm.WaitingSiteInput = "WAIT"; vm.ParameterActions[0].HeightInput = "bad";
        Complete(vm.SaveTaskParametersAsync()); Require(!File.Exists(parameterPath) && vm.ParameterMessage.Contains("高度"), "invalid height text is rejected without reusing prior value");
        Require(vm.ParameterTimeouts.Count == 9 && vm.ParameterTimeouts.All(row => row.SecondsInput == "0"), "legacy default thresholds disabled for all nine steps");
        vm.ParameterActions[0].HeightInput = "85";
        vm.ParameterTimeouts[1].SecondsInput = "1.5"; Complete(vm.SaveTaskParametersAsync());
        Require(!File.Exists(parameterPath) && vm.ParameterMessage.Contains("整数秒"), "fractional timeout text blocks save");
        vm.ParameterTimeouts[1].SecondsInput = "60";
        vm.ParameterActions[0].HeightInput = "85"; vm.ParameterActions[0].RecognizeInput = "否";
        vm.ParameterActions[0].PgvInput = "是"; vm.ParameterActions[0].DownPgvInput = "不指定";
        Complete(vm.ValidateTaskParametersAsync());
        Require(vm.ParameterValidationSteps.Count == 9 && vm.ParameterValidationSteps.All(row => row.Step.ValidationErrors.Count == 0), "edited profile validates all nine steps");
        var beforeSaveRevision = parameterRuntime.Revision;
        proxy.FailParameterAfterCommit = true; Complete(vm.SaveTaskParametersAsync()); var uncertainParameters = proxy.LastParameterRequest;
        Require(vm.ParameterMessage.Contains("尚未确认") && File.Exists(parameterPath) && dispatch.GetAction(DispatchTaskStepType.GoToPickup)?.JackHeight == 85, "lost save response retains uncertainty after successful atomic apply");
        Complete(vm.SaveTaskParametersAsync());
        var savedParameters = TaskParameterSettingsService.ReadDocument(parameterPath)!;
        Require(proxy.LastParameterRequest!.RequestId == uncertainParameters!.RequestId && savedParameters.Audits.Count == 1 && parameterRuntime.Revision != beforeSaveRevision,
            "same uncertain save request replays without another revision or audit");
        Require(!gate.RecoveryOpen && !gate.CanStartNewWork && !plcConfiguration.Options.AllowWrites &&
            Complete(gate.EnterAsync(MapExecutionOperation.NewWork, default).AsTask()) is null,
            "successful configuration save preserves startup recovery barrier and disabled PLC writes");
        var taskCountBeforePreparation = Complete(db.Queryable<DispatchTaskEntity>().CountAsync());
        Require(!Complete(service.CreateAsync(user, Request() with { ParameterRevision = parameterRuntime.Revision }, default)).Accepted &&
            Complete(db.Queryable<DispatchTaskEntity>().CountAsync()) == taskCountBeforePreparation,
            "preparation save cannot create a task through the closed recovery barrier");
        gate.RecoveryOpen = true;
        Require(!Complete(service.CreateAsync(user, Request() with { ParameterRevision = parameterRuntime.Revision }, default)).Accepted,
            "after recovery opens, saved preparation still cannot create a task with writes off");
        plcConfiguration.Options.AllowWrites = true;
        Require(vm.ParameterActions[0].HeightInput == "85" && vm.ParameterActions[0].Recognize is false && vm.ParameterActions[0].UsePgv is true &&
            vm.ParameterActions[0].UseDownPgv is null && vm.ParameterMessage.Contains("生效"), "UI reads applied profile including distinct nullable boolean choices after save receipt");
        var startupDispatch = new DispatchOrchestratorOptions(); var startupCreation = new DispatchTaskCreationOptions();
        var restartedParameters = new TaskParameterRuntime(startupDispatch, startupCreation); restartedParameters.Apply(savedParameters);
        Require(startupDispatch.GetAction(DispatchTaskStepType.GoToPickup)?.JackHeight == 85 && startupDispatch.GetPickupSite("CNC001") == "PICK" && restartedParameters.Revision == parameterRuntime.Revision,
            "startup reload restores persisted actions mappings and revision");
        Require(restartedParameters.StepTimeouts.Single(row => row.StepType == DispatchTaskStepType.PickupFullBox).Seconds == 60 && vm.ParameterTimeouts[1].SecondsInput == "60", "threshold survives atomic save receipt and restart");
        foreach (var badTimeouts in new IReadOnlyList<TaskStepTimeoutParameter>[] {
            savedParameters.Configuration.StepTimeouts.Take(8).ToArray(),
            savedParameters.Configuration.StepTimeouts.Select(row => row with { Seconds = -1 }).ToArray(),
            savedParameters.Configuration.StepTimeouts.Select(row => row with { Seconds = 604801 }).ToArray(),
            savedParameters.Configuration.StepTimeouts.Select(row => row with { StepType = DispatchTaskStepType.GoToPickup }).ToArray() })
            Require(!Complete(parameterService.ValidateAsync(ParameterRequest(savedParameters.Configuration with { StepTimeouts = badTimeouts }), default)).Accepted, "invalid timeout coverage or range rejected by backend");
        Require(!Complete(service.CreateAsync(user, Request() with { ParameterRevision = "initial" }, default)).Accepted, "old creation preview revision cannot create against newly applied parameters");
        Require(!Complete(parameterService.SaveAsync(user, ParameterRequest(savedParameters.Configuration, "initial"), default)).Accepted, "stale parameter revision cannot overwrite new profile");
        Require(!Complete(parameterService.SaveAsync(user, ParameterRequest(savedParameters.Configuration) with { MapVersionId = "OLD" }, default)).Accepted, "stale map version cannot save profile");
        var duplicates = savedParameters.Configuration with { Pickups = [savedParameters.Configuration.Pickups[0], savedParameters.Configuration.Pickups[0]] };
        Require(!Complete(parameterService.ValidateAsync(ParameterRequest(duplicates), default)).Accepted, "duplicate station mappings rejected");
        foreach (var mode in new[] { MapExecutionMode.Starting, MapExecutionMode.Draining, MapExecutionMode.Held })
        {
            gate.SetMode(mode);
            var blockedSnapshot = Complete(parameterService.GetAsync(default));
            var blockedSave = Complete(parameterService.SaveAsync(user, ParameterRequest(savedParameters.Configuration), default));
            Require(!blockedSnapshot.CanSave && !blockedSave.Accepted && blockedSave.Message.Contains(mode.ToString()) &&
                parameterRuntime.Revision == savedParameters.Revision && TaskParameterSettingsService.ReadDocument(parameterPath)!.Audits.Count == 1,
                "real map state blocks parameter replacement without changing revision or file: " + mode);
        }
        gate.SetMode(MapExecutionMode.Running);
        var noMapService = new TaskParameterSettingsService(new(parameterPath), parameterRuntime, service,
            new DispatchTaskReadRepository(db), new ActiveMapRuntime(), gate);
        Require(!Complete(noMapService.GetAsync(default)).CanSave && !Complete(noMapService.SaveAsync(user, ParameterRequest(savedParameters.Configuration), default)).Accepted,
            "configuration cannot save without an active runtime map");
        var failurePath = Path.Combine(Path.GetDirectoryName(parameterPath)!, "directory-target"); Directory.CreateDirectory(failurePath);
        var failureService = new TaskParameterSettingsService(new(failurePath), parameterRuntime, service, new DispatchTaskReadRepository(db), map, gate);
        try { Complete(failureService.SaveAsync(user, ParameterRequest(savedParameters.Configuration), default)); Require(false, "expected file write failure"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Require(parameterRuntime.Revision == savedParameters.Revision && Directory.GetFiles(Path.GetDirectoryName(parameterPath)!, "*.tmp").Length == 0, "failed file replacement does not apply runtime profile and removes temporary file"); }
        var concurrentA = savedParameters.Configuration with { Actions = savedParameters.Configuration.Actions.Select(action => action.StepType == DispatchTaskStepType.GoToPickup ? action with { JackHeight = 86 } : action).ToArray() };
        var concurrentB = savedParameters.Configuration with { Actions = savedParameters.Configuration.Actions.Select(action => action.StepType == DispatchTaskStepType.GoToPickup ? action with { JackHeight = 87 } : action).ToArray() };
        var concurrentRequestA = ParameterRequest(concurrentA); var concurrentRequestB = ParameterRequest(concurrentB);
        var concurrentResults = Complete(Task.WhenAll(parameterService.SaveAsync(user, concurrentRequestA, default), parameterService.SaveAsync(user, concurrentRequestB, default)));
        Require(concurrentResults.Count(result => result.Accepted) == 1 && TaskParameterSettingsService.ReadDocument(parameterPath)!.Audits.Count == 2,
            "execution mutex and revision check allow only one competing profile save");
        Complete(vm.LoadTaskParametersAsync());
        vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy);
        view.Height = 760; view.Measure(new Size(1280, 760)); view.Arrange(new Rect(0, 0, 1280, 760)); view.UpdateLayout();
        var parameterButton = (System.Windows.Controls.Button)view.FindName("SaveTaskParametersButton");
        Require(parameterButton.ActualHeight > 0 && parameterButton.TranslatePoint(new Point(0, parameterButton.ActualHeight), view).Y < 760, "compact editor keeps save action visible");
        var parameterImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); parameterImage.Render(view);
        var parameterEncoder = new PngBitmapEncoder(); parameterEncoder.Frames.Add(BitmapFrame.Create(parameterImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-parameters.png"))) parameterEncoder.Save(file);
        var parameterScroll = (System.Windows.Controls.ScrollViewer)view.FindName("ParameterFormScroll"); parameterScroll.ScrollToVerticalOffset(350); view.UpdateLayout();
        var actionGrid = (System.Windows.Controls.DataGrid)view.FindName("ParameterActionGrid");
        var finalActionRow = (FrameworkElement)actionGrid.ItemContainerGenerator.ContainerFromIndex(3);
        Require(finalActionRow is not null && finalActionRow.TranslatePoint(new Point(0, finalActionRow.ActualHeight), view).Y < 760, "all four action rows remain accessible by scrolling compact editor");
        var actionImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); actionImage.Render(view);
        var actionEncoder = new PngBitmapEncoder(); actionEncoder.Frames.Add(BitmapFrame.Create(actionImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-parameters-actions.png"))) actionEncoder.Save(file);
        parameterScroll.ScrollToEnd(); view.UpdateLayout();
        var timeoutGrid = (System.Windows.Controls.DataGrid)view.FindName("ParameterTimeoutGrid"); timeoutGrid.ScrollIntoView(vm.ParameterTimeouts[8]); view.UpdateLayout(); parameterScroll.ScrollToEnd(); view.UpdateLayout();
        var lastTimeoutRow = (FrameworkElement)timeoutGrid.ItemContainerGenerator.ContainerFromIndex(8);
        Require(lastTimeoutRow is not null && lastTimeoutRow.TranslatePoint(new Point(0, lastTimeoutRow.ActualHeight), parameterScroll).Y <= parameterScroll.ActualHeight, "ninth timeout editable in compact editor");
        var timeoutImage = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); timeoutImage.Render(view);
        var timeoutEncoder = new PngBitmapEncoder(); timeoutEncoder.Frames.Add(BitmapFrame.Create(timeoutImage));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-parameters-timeouts.png"))) timeoutEncoder.Save(file);
        var freshCreation = Complete(service.CreateAsync(user, Request() with { ParameterRevision = parameterRuntime.Revision }, default));
        Require(freshCreation.Accepted && Complete(db.Queryable<DispatchTaskEntity>().Where(task => task.TaskId == freshCreation.TaskId).FirstAsync()).CreateParameterRevision == parameterRuntime.Revision,
            "new task captures applied parameter revision for audit and replay");
        vm.Search = freshCreation.TaskId!; vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy);
        Require(!vm.CancelDispatchTaskCommand.CanExecute(), "cancel action requires explicit incident reason");
        vm.TaskOperationReason = "现场申请取消未开始任务并核对PLC和原路权";
        Require(vm.CancelDispatchTaskCommand.CanExecute(), "live task and incident reason enable cancel request");
        proxy.Cancellation = cancellation; proxy.FailCancelAfterCommit = true; Complete(vm.CancelDispatchTaskAsync());
        Require(vm.TaskOperationMessage.Contains("已从后台核对") && vm.Detail!.Task.Stage == DispatchTaskStage.CancellationReview && vm.Detail.Cancellation?.RequestId != null,
            "lost cancellation response UI reconciles original durable intent and review state");
        Require(!vm.CancelDispatchTaskCommand.CanExecute() && vm.BeginTaskRecoveryCommand.CanExecute(), "stopped review disables duplicate cancel and enables现场核对");
        ((System.Windows.Controls.Expander)view.FindName("TaskOperationsPanel")).IsExpanded = true;
        vm.IsParametersOpen = false; view.Height = 920; view.Measure(new Size(1280, 920)); view.Arrange(new Rect(0, 0, 1280, 920)); view.UpdateLayout();
        var cancelBitmap = new RenderTargetBitmap(1280, 920, 96, 96, PixelFormats.Pbgra32); cancelBitmap.Render(view);
        var cancelPng = new PngBitmapEncoder(); cancelPng.Frames.Add(BitmapFrame.Create(cancelBitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-cancellation.png"))) cancelPng.Save(file);
        view.Height = 760; view.Measure(new Size(1280, 760)); view.Arrange(new Rect(0, 0, 1280, 760)); view.UpdateLayout();
        var recoveryButton = (System.Windows.Controls.Button)view.FindName("BeginTaskRecoveryButton");
        Require(recoveryButton.TranslatePoint(new Point(0, recoveryButton.ActualHeight), view).Y <= 760, "compact cancellation panel keeps recovery action accessible");
        Require(vm.OperationHistory.Any(row => row.Operation == "提交取消请求"), "history displays durable cancellation after reply loss");
        var historyId = vm.SelectedOperation!.Id;
        vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy);
        Require(vm.SelectedOperation?.Id == historyId, "history selection survives refresh");
        var historyAt = DateTime.Now.AddDays(-1); historyAt = historyAt.AddTicks(-(historyAt.Ticks % TimeSpan.TicksPerSecond));
        Complete(db.Insertable(Enumerable.Range(0,80).Select(n => new ExecutionEvidenceEntity {
            Id=$"ui-history-{n:D3}", TaskId=freshCreation.TaskId!, SessionId="ui-recovery-batch", Kind="CloseCancelled", Operator="现场确认员",
            Reason="隔离测试现场载荷与PLC核对", Evidence="{\"fixture\":true}", OccurredAt=historyAt.AddSeconds(n)
        }).ToArray()).ExecuteCommandAsync());
        Complete(db.Insertable(new ExecutionEvidenceEntity { Id="ui-old-cancel", TaskId=freshCreation.TaskId!, Kind="TaskCancellationProcessingFailed", Operator="service", OccurredAt=historyAt.AddSeconds(90) }).ExecuteCommandAsync());
        Complete(db.Insertable(new TrafficResourceReleaseAuditEntity { AuditId="ui-release", TaskId=freshCreation.TaskId!, ResourceId="TEST-NARROW", Operator="现场解锁员", Reason="隔离测试已确认区域清空", ReleasedAt=historyAt.AddSeconds(100) }).ExecuteCommandAsync());
        vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy && !vm.IsHistoryBusy);
        Require(vm.OperationHistory.Count==50 && vm.HistoryPageCount==2 && vm.NextHistoryPageCommand.CanExecute(), "history starts at latest 50 with next page available");
        var newestIds=vm.OperationHistory.Select(e=>e.Id).ToArray();
        vm.NextHistoryPageCommand.Execute(); Pump(() => !vm.IsHistoryBusy);
        Require(vm.OperationHistory.Count==33 && vm.PreviousHistoryPageCommand.CanExecute() && !vm.NextHistoryPageCommand.CanExecute() && !vm.OperationHistory.Any(e=>newestIds.Contains(e.Id)), "history next page has older distinct records and proper buttons");
        var olderIds=vm.OperationHistory.Select(e=>e.Id).ToArray();
        vm.RefreshCommand.Execute(); Pump(() => !vm.IsBusy && !vm.IsHistoryBusy);
        Require(vm.OperationHistory.Select(e=>e.Id).SequenceEqual(olderIds), "automatic detail refresh preserves history page");
        vm.HistoryCategory=vm.HistoryCategories.Single(c=>c.Value==TaskHistoryCategory.Recovery);
        Complete(vm.ApplyHistoryFilterAsync());
        Require(vm.OperationHistory.Count==50 && vm.OperationHistory.All(e=>e.Category=="恢复核对") && !vm.PreviousHistoryPageCommand.CanExecute(), "category filtering resets page and queries backend");
        vm.HistorySession=vm.HistorySessions.Single(s=>s.Value=="ui-recovery-batch");
        vm.HistoryFromText=historyAt.AddSeconds(30).ToString("yyyy-MM-dd HH:mm:ss");
        vm.HistoryToText=historyAt.AddSeconds(35).ToString("yyyy-MM-dd HH:mm:ss");
        Complete(vm.ApplyHistoryFilterAsync());
        Require(vm.OperationHistory.Count==6 && vm.OperationHistory.All(e=>e.SessionId=="ui-recovery-batch") && vm.HistoryMessage.Contains("未记录批次"), "combined UI category time batch filter includes both endpoints");
        var exportDestination = Path.Combine(Path.GetTempPath(), "agv-history-ui-" + Guid.NewGuid().ToString("N") + ".zip");
        // Edit unapplied draft values; export must still use the six-record applied filter.
        vm.HistoryCategory=vm.HistoryCategories.Single(c=>c.Value==TaskHistoryCategory.Cancellation);
        var exportTask=vm.ExportHistoryToAsync(exportDestination);
        Require(vm.IsHistoryExporting && !vm.ExportHistoryCommand.CanExecute() && !vm.ApplyHistoryFilterCommand.CanExecute(), "export disables duplicate request and changing applied filter while saving");
        Complete(exportTask);
        Require(File.Exists(exportDestination) && vm.HistoryExportFile==exportDestination && vm.HistoryExportMessage.Contains("全部 6 条") && proxy.LastExportRequest!.Filter.Category==TaskHistoryCategory.Recovery,
            "export uses applied filter across all pages and saves verified package at chosen destination");
        using(var archive=ZipFile.OpenRead(exportDestination))
        using(var stream=archive.GetEntry("manifest.json")!.Open())
        using(var manifest=JsonDocument.Parse(stream))
            Require(manifest.RootElement.GetProperty("recordCount").GetInt32()==6 && manifest.RootElement.GetProperty("filter").GetProperty("sessionId").GetString()=="ui-recovery-batch", "saved ZIP manifest matches active combined filter");
        Require(vm.HistoryExportProgress==100 && vm.HistoryExportProgressText.Contains("已校验并保存"), "UI progress reaches 100 only after local verification and atomic save");
        var protectedTarget=Path.Combine(Path.GetTempPath(), "agv-history-protected-"+Guid.NewGuid().ToString("N")+".zip");
        File.WriteAllText(protectedTarget,"original-file");
        proxy.DelayNextExport=true;
        var activeCancelledExport=vm.ExportHistoryToAsync(protectedTarget);
        Pump(()=>vm.HistoryExportProgress>0);
        Require(vm.CancelHistoryExportCommand.CanExecute() && vm.HistoryExportProgressText.Contains("写入"), "live progress and explicit cancel action available during export");
        ((System.Windows.Controls.TabItem)view.FindName("TaskOperationHistoryTab")).IsSelected=true;
        ((System.Windows.Controls.Expander)view.FindName("TaskOperationsPanel")).IsExpanded=false;
        view.Height=920; view.Measure(new Size(1280,920)); view.Arrange(new Rect(0,0,1280,920)); view.UpdateLayout();
        var activeHistoryGrid=(System.Windows.Controls.DataGrid)view.FindName("TaskOperationHistoryGrid");
        Pump(()=>activeHistoryGrid.Columns[0].ActualWidth>=180); view.UpdateLayout();
        var progressBar=(System.Windows.Controls.ProgressBar)view.FindName("TaskHistoryExportProgressBar");
        Pump(()=>progressBar.Value==vm.HistoryExportProgress && progressBar.ActualHeight>=18);
        view.UpdateLayout();
        Require(progressBar.Visibility==Visibility.Visible && progressBar.Value==vm.HistoryExportProgress && progressBar.ActualHeight>=18,"active export progress renders visible bound percentage without clipping");
        var progressBitmap=new RenderTargetBitmap(1280,920,96,96,PixelFormats.Pbgra32); progressBitmap.Render(view);
        var progressEncoder=new PngBitmapEncoder(); progressEncoder.Frames.Add(BitmapFrame.Create(progressBitmap));
        using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"task-export-progress.png"))) progressEncoder.Save(file);
        view.Height=760; view.Measure(new Size(1280,760)); view.Arrange(new Rect(0,0,1280,760)); view.UpdateLayout();
        vm.CancelHistoryExportCommand.Execute(); Complete(activeCancelledExport);
        Require(proxy.CancelExportRequestSeen && !vm.IsHistoryExporting && vm.HistoryExportMessage.Contains("后台导出已取消") && File.ReadAllText(protectedTarget)=="original-file", "cancel button uses independent backend request and preserves target");
        proxy.DelayedExportReply!.TrySetResult(proxy.LastExportResult!);
        proxy.CorruptExportHash=true; Complete(vm.ExportHistoryToAsync(protectedTarget));
        Require(File.ReadAllText(protectedTarget)=="original-file" && vm.HistoryExportMessage.Contains("校验不一致") && vm.HistoryExportFile=="", "corrupt package never overwrites existing destination");
        Complete(vm.ExportHistoryToAsync(Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"),"missing-folder.zip")));
        Require(vm.HistoryExportMessage.Contains("导出失败") && !vm.IsHistoryExporting && vm.ExportHistoryCommand.CanExecute(), "save failure gives recoverable error and re-enables export");
        vm.HistoryCategory=vm.HistoryCategories.Single(c=>c.Value==TaskHistoryCategory.Recovery);
        vm.HistoryFromText=historyAt.AddSeconds(40).ToString("yyyy-MM-dd HH:mm:ss"); Complete(vm.ApplyHistoryFilterAsync());
        Require(vm.HistoryMessage.Contains("不得晚于") && vm.OperationHistory.Count==6, "reversed time rejected without replacing current results");
        vm.HistoryFromText="bad-time"; Complete(vm.ApplyHistoryFilterAsync());
        Require(vm.HistoryMessage.Contains("时间格式") && vm.OperationHistory.Count==6, "malformed time rejected visibly");
        vm.HistoryFromText=""; vm.HistoryToText=""; vm.HistorySession=new("unknown-batch","unknown-batch"); Complete(vm.ApplyHistoryFilterAsync());
        Require(vm.OperationHistory.Count==0 && vm.SelectedOperation==null && vm.HistoryPageCount==1, "empty batch clears previous rows and evidence");
        vm.ResetHistoryFilterCommand.Execute(); Pump(() => !vm.IsHistoryBusy);
        Require(vm.OperationHistory.Count==50 && vm.HistoryFromText=="" && vm.HistorySession.Value=="" && vm.HistoryCategory.Value==TaskHistoryCategory.All, "reset restores all categories and batches at page one");
        ((System.Windows.Controls.TabItem)view.FindName("TaskOperationHistoryTab")).IsSelected = true;
        ((System.Windows.Controls.Expander)view.FindName("TaskOperationsPanel")).IsExpanded = false;
        view.UpdateLayout();
        Complete(vm.RefreshExportCacheAsync());
        Require(vm.ExportCacheItems.Count>0 && vm.ExportCacheMessage.Contains("后台缓存共"), "cache panel loads owned export artifacts and retention summary");
        vm.ExportCacheRetention=90; Complete(vm.CleanExportCacheAsync());
        Require(vm.ExportCacheMessage.Contains("已清理") && File.Exists(exportDestination), "cache cleanup leaves selected saved handover package intact");
        var sessionCombo = (System.Windows.Controls.ComboBox)view.FindName("TaskHistorySessionCombo");
        Pump(() => sessionCombo.SelectedItem != null);
        Require(sessionCombo.SelectedItem is HistorySessionChoice { Label: "全部批次" }, "batch selector retains visible all-batches choice after refresh and reset");
        var historyGrid = (System.Windows.Controls.DataGrid)view.FindName("TaskOperationHistoryGrid");
        Pump(() => historyGrid.Columns[0].ActualWidth >= 180);
        view.UpdateLayout();
        Require(historyGrid.Items.Count == vm.OperationHistory.Count && historyGrid.ActualHeight > 40, "compact history tab renders records");
        var bitmap = new RenderTargetBitmap(1280, 760, 96, 96, PixelFormats.Pbgra32); bitmap.Render(view);
        var historyEncoder = new PngBitmapEncoder(); historyEncoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(AppContext.BaseDirectory, "task-operation-history.png"))) historyEncoder.Save(file);
        ((System.Windows.Controls.TabItem)view.FindName("TaskHistoryCacheTab")).IsSelected=true;
        view.Height=920; view.Measure(new Size(1280,920)); view.Arrange(new Rect(0,0,1280,920)); view.UpdateLayout();
        var cacheGrid=(System.Windows.Controls.DataGrid)view.FindName("TaskHistoryCacheGrid");
        Pump(()=>cacheGrid.Columns[0].ActualWidth>100); view.UpdateLayout();
        Require(cacheGrid.Items.Count==vm.ExportCacheItems.Count && cacheGrid.ActualHeight>40, "cache tab renders names size timestamps and eligible status");
        var cacheBitmap=new RenderTargetBitmap(1280,920,96,96,PixelFormats.Pbgra32); cacheBitmap.Render(view);
        var cacheEncoder=new PngBitmapEncoder(); cacheEncoder.Frames.Add(BitmapFrame.Create(cacheBitmap));
        using(var file=File.Create(Path.Combine(AppContext.BaseDirectory,"task-export-cache.png"))) cacheEncoder.Save(file);
        ((System.Windows.Controls.TabItem)view.FindName("TaskHistoryEvidenceTab")).IsSelected = true;
        view.UpdateLayout();
        var evidenceText = (System.Windows.Controls.TextBox)view.FindName("TaskHistoryEvidenceText");
        Pump(() => evidenceText.Text == vm.SelectedOperation!.EvidenceDisplay);
        Require(evidenceText.IsReadOnly && evidenceText.Text.Length > 0, "selected history evidence is readable and immutable");
        var stalePage = new TaskHistoryPage(vm.OperationHistory.ToArray(), 83, 1, 50, ["ui-recovery-batch"]);
        proxy.DelayNextExport = true;
        var cancelledExport = vm.ExportHistoryToAsync(protectedTarget);
        proxy.DelayNextHistory = true;
        var delayedHistory = vm.ApplyHistoryFilterAsync();
        vm.SelectedTask = null;
        proxy.DelayedHistoryReply!.SetResult(stalePage); Complete(delayedHistory);
        proxy.DelayedExportReply!.SetResult(proxy.LastExportResult!); Complete(cancelledExport);
        Require(File.ReadAllText(protectedTarget)=="original-file" && !vm.IsHistoryExporting, "task switch cancels export before publishing and preserves destination");
        Require(!vm.IsHistoryBusy && vm.OperationHistory.Count==0, "late history response cannot restore previous task after selection cleared");
        Require(vm.OperationHistory.Count == 0 && vm.SelectedOperation == null && vm.OperationHistorySummary.Contains("请选择"), "clearing task selection clears previous task history and evidence");
        vm.OnNavigatedFrom(null!); Console.WriteLine(output);
    }
    private static void CheckLegacyMigration()
    {
        var path = Path.Combine(Path.GetTempPath(), "agv-task-legacy-" + Guid.NewGuid().ToString("N") + ".db");
        using var database = DatabaseFactory.Create(new DatabaseOptions { DatabasePath = path });
        database.Ado.ExecuteCommand("CREATE TABLE dispatch_task (TaskId TEXT PRIMARY KEY NOT NULL, StationId TEXT NOT NULL, VehicleId TEXT NULL, VendorTaskId TEXT NULL, Stage INTEGER NOT NULL, Priority INTEGER NOT NULL, CreatedAt DATETIME NOT NULL, UpdatedAt DATETIME NOT NULL, ErrorMessage TEXT NULL)");
        database.Ado.ExecuteCommand("INSERT INTO dispatch_task (TaskId, StationId, Stage, Priority, CreatedAt, UpdatedAt) VALUES ('legacy', 'CNC-OLD', 90, 0, '2000-01-01', '2000-01-01')");
        Complete(new DatabaseInitializer(database).InitializeAsync(default));
        var old = Complete(database.Queryable<DispatchTaskEntity>().Where(task => task.TaskId == "legacy").FirstAsync());
        Require(old.StationId == "CNC-OLD" && old.RequestedVehicleId is null && old.CreateRequestId is null, "legacy schema upgrades without losing task or inventing requested vehicle");
        Complete(new DatabaseInitializer(database).InitializeAsync(default));
        Require(Complete(database.Queryable<DispatchTaskEntity>().CountAsync()) == 1, "schema initialization remains repeatable");
    }
}

public class CreationClient : DispatchProxy
{
    public ITaskCancellationService Cancellation = null!; public bool FailCancelAfterCommit;
    public IManualTaskCreationService Creation = null!; public ITaskCenterQueryService Query = null!; public TrafficUserContext User = null!;
    public ITaskHistoryExportService HistoryExports = null!;
    public bool CorruptExportHash, DelayNextExport, CancelExportRequestSeen;
    private Task<TaskHistoryExportProgress> GetExportProgress(TaskHistoryExportJobRequest request, CancellationToken ct)
    {
        if(!CancelExportRequestSeen && DelayedExportReply is { Task.IsCompleted:false } && LastExportRequest?.ExportId==request.ExportId)
            return Task.FromResult(new TaskHistoryExportProgress(request.ExportId,"Writing","正在写入处置记录 3 / 6",3,6,50));
        return HistoryExports.GetProgressAsync(User,request,ct);
    }
    private Task<TaskHistoryExportCancelResult> CancelExport(TaskHistoryExportJobRequest request,CancellationToken ct)
    { CancelExportRequestSeen=true; return HistoryExports.CancelAsync(User,request,ct); }
    public TaskHistoryExportRequest? LastExportRequest;
    public TaskHistoryExportResult? LastExportResult;
    public TaskCompletionSource<TaskHistoryExportResult>? DelayedExportReply;
    private async Task<TaskHistoryExportResult> ExportHistory(TaskHistoryExportRequest request, CancellationToken ct)
    {
        LastExportRequest=request; CancelExportRequestSeen=false;
        if (DelayNextExport) { DelayNextExport=false; DelayedExportReply=new(TaskCreationOptions.RunContinuationsAsynchronously); return await DelayedExportReply.Task; }
        var result=await HistoryExports.ExportAsync(User,request,ct); LastExportResult=result;
        if(CorruptExportHash) { CorruptExportHash=false; return result with { Sha256=new string('0',64) }; }
        return result;
    }
    public ITaskParameterService Parameters = null!;
    public bool DelayNextHistory;
    public TaskCompletionSource<TaskHistoryPage>? DelayedHistoryReply;
    private Task<TaskHistoryPage> GetHistory(TaskHistoryQuery query, CancellationToken ct)
    {
        if (!DelayNextHistory) return Query.QueryHistoryAsync(query, ct);
        DelayNextHistory = false; DelayedHistoryReply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return DelayedHistoryReply.Task;
    }
    public bool FailParameterAfterCommit; public TaskParameterRequest? LastParameterRequest;
    public bool FailAfterCommit; public ManualTaskCreationRequest? LastRequest; public string? LastCreatedId;
    private async Task<TaskCancelResult> Cancel(TaskCancelRequest request)
    {
        var result = await Cancellation.RequestAsync(User, request, default);
        if (result.Accepted && FailCancelAfterCommit) { FailCancelAfterCommit = false; throw new IOException("lost cancel receipt"); }
        return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "CancelDispatchTaskAsync" => Cancel((TaskCancelRequest)args![0]!),
        "GetTaskCreationOptionsAsync" => Creation.GetOptionsAsync((CancellationToken)args![0]!),
        "CreateTaskAsync" => Create((ManualTaskCreationRequest)args![0]!, (CancellationToken)args[1]!),
        "GetTasksAsync" => Query.QueryAsync((TaskListQuery)args![0]!, (CancellationToken)args[1]!),
        "GetTaskHistoryExportProgressAsync" => GetExportProgress((TaskHistoryExportJobRequest)args![0]!, (CancellationToken)args[1]!),
        "CancelTaskHistoryExportAsync" => CancelExport((TaskHistoryExportJobRequest)args![0]!, (CancellationToken)args[1]!),
        "GetTaskHistoryExportCacheAsync" => HistoryExports.GetCacheAsync(User, (TaskHistoryExportCacheRequest)args![0]!, (CancellationToken)args[1]!),
        "CleanupTaskHistoryExportCacheAsync" => HistoryExports.CleanupAsync(User, (TaskHistoryExportCacheRequest)args![0]!, (CancellationToken)args[1]!),
        "ExportTaskHistoryAsync" => ExportHistory((TaskHistoryExportRequest)args![0]!, (CancellationToken)args[1]!),
        "GetTaskHistoryAsync" => GetHistory((TaskHistoryQuery)args![0]!, (CancellationToken)args[1]!),
        "GetTaskDetailAsync" => Query.GetDetailAsync(new TaskDetailRequest((string)args![0]!, false), (CancellationToken)args[1]!),
        "GetTaskParametersAsync" => Parameters.GetAsync((CancellationToken)args![0]!),
        "ValidateTaskParametersAsync" => Parameters.ValidateAsync((TaskParameterRequest)args![0]!, (CancellationToken)args[1]!),
        "SaveTaskParametersAsync" => SaveParameters((TaskParameterRequest)args![0]!, (CancellationToken)args[1]!),
        _ => throw new NotSupportedException(method.Name)
    };
    private async Task<ManualTaskCreationResult> Create(ManualTaskCreationRequest request, CancellationToken ct)
    {
        LastRequest = request; var result = await Creation.CreateAsync(User, request, ct); LastCreatedId = result.TaskId;
        if (FailAfterCommit) { FailAfterCommit = false; throw new IOException("Simulated lost reply"); } return result;
    }
    private async Task<TaskParameterResult> SaveParameters(TaskParameterRequest request, CancellationToken ct)
    {
        LastParameterRequest = request; var result = await Parameters.SaveAsync(User, request, ct);
        if (result.Accepted && FailParameterAfterCommit) { FailParameterAfterCommit = false; throw new IOException("Simulated lost parameter save response"); }
        return result;
    }
}
sealed class FixtureGate : IMapExecutionGate
{
    private readonly FixtureRecoveryBarrier _recovery = new();
    private readonly MapExecutionGate _inner;
    public FixtureGate() { _inner = new(_recovery); _inner.SetMode(MapExecutionMode.Running); }
    public bool RecoveryOpen { get => _recovery.IsOpen; set => _recovery.IsOpen = value; }
    public bool Allowed { get => _inner.CanStartNewWork; set => _inner.SetMode(value ? MapExecutionMode.Running : MapExecutionMode.Held); }
    public MapExecutionMode Mode => _inner.Mode;
    public bool CanStartNewWork => _inner.CanStartNewWork;
    public ValueTask<IDisposable?> EnterAsync(MapExecutionOperation operation, CancellationToken ct) => _inner.EnterAsync(operation, ct);
    public ValueTask<IDisposable> EnterControlAsync(CancellationToken ct) => _inner.EnterControlAsync(ct);
    public void SetMode(MapExecutionMode mode) => _inner.SetMode(mode);
}
sealed class FixtureRecoveryBarrier : IRecoveryBarrier { public bool IsOpen { get; set; } = true; }
sealed class FixtureStations : IStationStateService
{
    public bool Allowed = true; public event Action<StationStateChanged>? StationStateChanged { add { } remove { } } public long Version => 1;
    public IReadOnlyList<StationStateSnapshot> GetAll() => [];
    public bool TryGet(string id, out StationStateSnapshot snapshot)
    { snapshot = new(id, default, Allowed, Allowed ? null : "PLC not ready", true, true, false, true, SignalQuality.Good, null, 0, DateTimeOffset.Now, DateTimeOffset.Now, 1); return true; }
}
sealed class FixtureStates : IAgvStateCache
{
    public bool Blocked; public event Action<AgvRobotStateChanged>? StateChanged { add { } remove { } } public long Version => 1;
    public IReadOnlyList<AgvRobotSnapshot> GetAll() => [];
    public bool TryGet(string id, out AgvRobotSnapshot snapshot)
    { snapshot = new(id, id, "", 0, ConnectionState.Online, SignalQuality.Good, 0, 0, 0, 1, "HOME", null, 80, Blocked, false, "Idle", null, null, null, [], [], DateTimeOffset.Now, DateTimeOffset.Now, 0, null, null, 1); return true; }
}
sealed class FixtureGateway : IAgvGateway
{
    public ConnectionState Connection = ConnectionState.Online;
    public Task<IReadOnlyList<AgvVehicleStatus>> GetVehiclesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AgvVehicleStatus>>([new("AGV-01", Connection, "Idle", 80, "HOME", DateTimeOffset.Now)]);
    public Task<DeviceConnectionStatus> GetConnectionStatusAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<string> CreateTransportTaskAsync(string id, string? vehicle, IReadOnlyList<string> path, AgvTransportAction? action, CancellationToken ct) => throw new Exception("Test must not dispatch hardware");
    public Task<AgvTransportTaskStatus?> GetTransportTaskStatusAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task CancelTaskAsync(string id, CancellationToken ct) => throw new NotSupportedException();
}
sealed class FixtureSignals : IPlcSignalCache
{
    public event Action<PlcSignalChanged>? SignalChanged { add { } remove { } } public long Version => 0;
    public IReadOnlyList<PlcSignalSnapshot> GetAll() => []; public IReadOnlyList<PlcSignalSnapshot> GetStation(string id) => [];
    public bool TryGet(string id, string variable, out PlcSignalSnapshot snapshot) { snapshot = null!; return false; }
}

sealed class FixturePlcConfiguration : IPlcConfigurationInspector
{
    public PlcOptions Options { get; } = new() { Enabled = true, AllowWrites = true };
    public List<PlcSignalDefinition> Signals { get; } = [];
    public FixturePlcConfiguration()
    {
        foreach (var (station, names) in new[]
        {
            ("CNC001", new[] { "FullBoxRequested", "BoxPresent", "Faulted", "CommunicationOnline", "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced" }),
            ("DUMPER-001", new[] { "Faulted", "DumperReady", "AgvAtWaitingPosition", "AgvAtDumpingPosition", "DumpingCompletedMayLeave" })
        })
            foreach (var name in names)
                Signals.Add(new("DB1.DBX" + Signals.Count + ".0", name, name, station, "fixture", 0, "Bool", "", 
                    new[] { "FullBoxRequestAcknowledged", "FullBoxRemoved", "EmptyBoxTaskAcknowledged", "EmptyBoxPlaced", "AgvAtWaitingPosition", "AgvAtDumpingPosition" }.Contains(name) ? "Write" : "Read", "Confirmed", true, "", "", "isolated"));
    }
    public void Replace(PlcSignalDefinition previous, PlcSignalDefinition replacement) => Signals[Signals.IndexOf(previous)] = replacement;
    public string? GetSignalConfigurationError(string stationId, string variableName, bool write) =>
        PlcTaskConfigurationInspector.Inspect(Options, PlcSignalCatalog.FromSignals(Signals), stationId, variableName, write);
    public string? GetSignalPreparationError(string stationId, string variableName, bool write) =>
        PlcTaskConfigurationInspector.InspectForPreparation(Options, PlcSignalCatalog.FromSignals(Signals), stationId, variableName, write);
    public sealed class Preflight : IRobokitTaskMapPreflight
    {
        public bool Accept; public Action? After;
        public Task<RobokitTaskMapCheck> CheckAsync(string vehicleId,string currentSite,string pickup,string waiting,string dumping,string mapId,string versionId,CancellationToken ct)
        { After?.Invoke();return Task.FromResult(new RobokitTaskMapCheck(Accept,"模拟地图映射核验")); }
    }

}
