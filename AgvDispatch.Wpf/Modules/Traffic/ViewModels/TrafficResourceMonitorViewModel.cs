using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed partial class TrafficResourceMonitorViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer;
    private TrafficLeaseRow? _selectedLease;
    private ManualReleaseRequestRow? _selectedRequest;
    private TrafficControlSession? _session;
    private string _reason = string.Empty;
    private string _confirmationText = string.Empty;
    private string _reviewComment = string.Empty;
    private bool _vehicleClearConfirmed;
    private bool _isBusy;
    private string _statusMessage = "等待连接后台服务。";
    private int _occupiedResourceCount;
    private int _expiredHeartbeatCount;
    private int _congestedEdgeCount;
    private int _deadlockCandidateCount;

    public TrafficResourceMonitorViewModel(ITrafficControlClient client)
    {
        _client = client;
        InitializeWaitingCommands();
        InitializeGeometryCommands();
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !IsBusy);
        SubmitReleaseCommand = new DelegateCommand(async () => await SubmitReleaseAsync(), CanSubmitRelease);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
    }

    public ObservableCollection<TrafficRequestRow> TrafficRequests { get; } = [];
    public ObservableCollection<TrafficEventRow> TrafficEvents { get; } = [];
    private TrafficRequestRow? _selectedTrafficRequest;
    public TrafficRequestRow? SelectedTrafficRequest
    {
        get => _selectedTrafficRequest;
        set
        {
            if (SetProperty(ref _selectedTrafficRequest, value))
            {
                if (value != null && SelectedLease != null && value.StepId != SelectedLease.StepId) SelectedLease = null;
                RaisePropertyChanged(nameof(TrafficContext));
            }
            RebuildTrafficMap();
        }
    }
    public string TrafficContext => SelectedTrafficRequest == null ? "选择路权请求查看任务、车辆、路段与阻塞方。" :
        $"任务 {SelectedTrafficRequest.TaskId}\n步骤 {SelectedTrafficRequest.StepId} · 车辆 {SelectedTrafficRequest.VehicleId}\n路径 {SelectedTrafficRequest.Route}\n路段 {SelectedTrafficRequest.Edges}\n资源 {SelectedTrafficRequest.Resources}\n当前持有 {SelectedTrafficRequest.HeldResources}\n状态 {SelectedTrafficRequest.State}\n{SelectedTrafficRequest.Reason}\n阻塞方 {SelectedTrafficRequest.Blockers}";
    private string _lifecycleNotice = "路权过程等待加载。";
    public string LifecycleNotice { get => _lifecycleNotice; private set => SetProperty(ref _lifecycleNotice, value); }
    public ObservableCollection<TrafficLeaseRow> Leases { get; } = [];
    public ObservableCollection<TrafficReleaseAuditRow> Audits { get; } = [];
    public ObservableCollection<ManualReleaseRequestRow> Requests { get; } = [];
    public ObservableCollection<EdgeTrafficRow> EdgeTraffic { get; } = [];
    public ObservableCollection<TrafficDeadlockRow> DeadlockCandidates { get; } = [];
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand SubmitReleaseCommand { get; }

    public TrafficLeaseRow? SelectedLease
    {
        get => _selectedLease;
        set
        {
            var changedLease = _selectedLease?.LeaseId != value?.LeaseId;
            if (SetProperty(ref _selectedLease, value))
            {
                if (changedLease) { ConfirmationText = string.Empty; VehicleClearConfirmed = false; }
                if (changedLease && value != null) SelectedTrafficRequest = TrafficRequests.FirstOrDefault(r => r.StepId == value.StepId);
                RaisePropertyChanged(nameof(RequiredConfirmationText));
                RaiseCommandStates();
            }
        }
    }

    public ManualReleaseRequestRow? SelectedRequest
    {
        get => _selectedRequest;
        set
        {
            if (SetProperty(ref _selectedRequest, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string CurrentUser => _session is null
        ? "未连接"
        : $"{_session.DisplayName}（{_session.WindowsIdentity}）";
    public string CurrentPermissions => _session?.Permissions.ToString() ?? "None";

    public string Reason
    {
        get => _reason;
        set { if (SetProperty(ref _reason, value)) RaiseCommandStates(); }
    }

    public string ConfirmationText
    {
        get => _confirmationText;
        set { if (SetProperty(ref _confirmationText, value)) RaiseCommandStates(); }
    }

    public string ReviewComment
    {
        get => _reviewComment;
        set => SetProperty(ref _reviewComment, value);
    }

    public string RequiredConfirmationText => SelectedLease is null
        ? "请先选择一条路权记录"
        : !SelectedLease.CanRelease ? "空闲、活动任务或任务缺失的资源禁止人工解锁"
        : $"请输入：RELEASE {SelectedLease.ResourceId}";

    public bool VehicleClearConfirmed
    {
        get => _vehicleClearConfirmed;
        set { if (SetProperty(ref _vehicleClearConfirmed, value)) RaiseCommandStates(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int OccupiedResourceCount
    {
        get => _occupiedResourceCount;
        private set => SetProperty(ref _occupiedResourceCount, value);
    }

    public int ExpiredHeartbeatCount
    {
        get => _expiredHeartbeatCount;
        private set => SetProperty(ref _expiredHeartbeatCount, value);
    }

    public int CongestedEdgeCount
    {
        get => _congestedEdgeCount;
        private set => SetProperty(ref _congestedEdgeCount, value);
    }

    public int DeadlockCandidateCount
    {
        get => _deadlockCandidateCount;
        private set => SetProperty(ref _deadlockCandidateCount, value);
    }

    public async void OnNavigatedTo(NavigationContext navigationContext)
    {
        await RefreshAsync();
        _timer.Start();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) { _timer.Stop();InvalidateGeometry("已离开调度监控，请重新检查。"); }

    private async Task RefreshAsync(bool silent = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _session = await _client.GetSessionAsync(CancellationToken.None);
            RaisePropertyChanged(nameof(CurrentUser));
            RaisePropertyChanged(nameof(CurrentPermissions));
            if (!Has(TrafficPermission.View))
            {
                throw new UnauthorizedAccessException(
                    $"账号{_session.WindowsIdentity}没有查看权限，请联系软件管理员。");
            }

            var resources = await _client.GetResourcesAsync(CancellationToken.None);
            var audits = await _client.GetRecentAuditsAsync(50, CancellationToken.None);
            var requests = await _client.GetReleaseRequestsAsync(100, CancellationToken.None);
            var trafficFlow = await _client.GetTrafficFlowAsync(CancellationToken.None);
            try
            {
                var lifecycle = await _client.GetTrafficLifecycleAsync(CancellationToken.None);
                var selectedStep = SelectedTrafficRequest?.StepId;
                Replace(TrafficRequests, lifecycle.Requests.Select(TrafficRequestRow.From));
                Replace(TrafficEvents, lifecycle.Events.Select(TrafficEventRow.From));
                SelectedTrafficRequest = TrafficRequests.FirstOrDefault(r => r.StepId == selectedStep) ?? TrafficRequests.FirstOrDefault();
                LifecycleNotice = $"本列表 {TrafficRequests.Count} 个请求 · 等待 {TrafficRequests.Count(r => r.State == "冲突等待")} · 最新100条过程记录" +
                    (lifecycle.RequestsTruncated ? "；请求只显示最近200条。" : "");
            }
            catch (NotSupportedException) { TrafficRequests.Clear(); TrafficEvents.Clear(); SelectedTrafficRequest = null; LifecycleNotice = "后台未支持路权过程监控，请更新后台服务。"; }
            await LoadWaitingQueueAsync();
            await RefreshTrafficMapAsync();
            RefreshGeometrySelection();
            var selectedLeaseId = SelectedLease?.LeaseId;
            var selectedRequestId = SelectedRequest?.RequestId;
            var rows = resources.SelectMany(resource => resource.Leases.Count == 0
                ? new[] { TrafficLeaseRow.Empty(resource) }
                : resource.Leases.Select(lease => TrafficLeaseRow.From(resource, lease))).ToArray();
            Replace(Leases, rows);
            SelectedLease = rows.FirstOrDefault(row => row.LeaseId == selectedLeaseId);

            Replace(Audits, audits.Select(audit => new TrafficReleaseAuditRow(
                audit.ResourceId, audit.TaskId, audit.VehicleId, audit.Operator,
                audit.RequestedBy ?? "—", audit.ApprovedBy ?? "—", audit.Reason,
                audit.PreviousTaskStage?.ToString() ?? "任务缺失",
                audit.HeartbeatExpired, audit.ReleasedAt.LocalDateTime)));
            Replace(Requests, requests.Select(item => new ManualReleaseRequestRow(
                item.RequestId, item.ResourceId, item.TaskId, item.VehicleId,
                item.RequestedBy, item.RequestedByDisplayName, item.Reason,
                item.Status, item.RequestedAt.LocalDateTime, item.ExpiresAt.LocalDateTime,
                item.ReviewedBy ?? "—", item.ReviewComment ?? string.Empty)));
            SelectedRequest = Requests.FirstOrDefault(row => row.RequestId == selectedRequestId);
            Replace(EdgeTraffic, trafficFlow.Edges.Select(edge => new EdgeTrafficRow(
                edge.EdgeId, edge.From, edge.To, edge.State,
                string.Join(',', edge.VehicleIds), string.Join(',', edge.ResourceIds),
                edge.StateSince.LocalDateTime, edge.DynamicPenalty, edge.Message)));
            Replace(DeadlockCandidates, trafficFlow.Deadlocks.Select(candidate => new TrafficDeadlockRow(
                candidate.CandidateId, candidate.EdgeId, string.Join(',', candidate.VehicleIds),
                string.Join(',', candidate.ResourceIds), candidate.DetectedAt.LocalDateTime,
                candidate.Reason, candidate.Suggestion)));

            OccupiedResourceCount = resources.Count(resource => resource.OccupiedCount > 0);
            ExpiredHeartbeatCount = rows.Count(row => row.HeartbeatExpired);
            CongestedEdgeCount = trafficFlow.Edges.Count(edge =>
                edge.State is EdgeTrafficState.Congested or EdgeTrafficState.Stalled);
            DeadlockCandidateCount = trafficFlow.Deadlocks.Count;
            if (!silent)
            {
                StatusMessage = $"已通过后台服务加载{resources.Count}个资源、{rows.Length}条路权记录。";
            }
        }
        catch (Exception exception)
        {
            ClearTrafficMap("交通数据加载失败，已清除地图关联：" + exception.Message);
            InvalidateGeometry("后台连接或查看权限异常，已清除旧预检："+exception.Message);
            StatusMessage = $"交通资源加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SubmitReleaseAsync()
    {
        var selected = SelectedLease;
        if (selected?.CanRelease != true || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _client.SubmitReleaseAsync(
                new SubmitManualReleaseRequest(
                    selected.LeaseId, Reason, VehicleClearConfirmed, ConfirmationText),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Accepted)
            {
                Reason = string.Empty;
                ConfirmationText = string.Empty;
                VehicleClearConfirmed = false;
                SelectedLease = null;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"提交人工解锁申请失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync(silent: true);
    }


    private bool CanSubmitRelease() =>
        !IsBusy && SelectedLease?.CanRelease == true && Has(TrafficPermission.SubmitManualRelease) &&
        VehicleClearConfirmed && Reason.Trim().Length >= 5 &&
        string.Equals(ConfirmationText.Trim(), $"RELEASE {SelectedLease.ResourceId}", StringComparison.OrdinalIgnoreCase);


    private bool Has(TrafficPermission permission) => _session is not null &&
        (_session.Permissions.HasFlag(TrafficPermission.Administer) ||
         _session.Permissions.HasFlag(permission));

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        CheckGeometryCommand?.RaiseCanExecuteChanged();
        SubmitReleaseCommand.RaiseCanExecuteChanged();
        ApplyWaitingFilterCommand?.RaiseCanExecuteChanged(); ResetWaitingFilterCommand?.RaiseCanExecuteChanged();
        PreviousWaitingPageCommand?.RaiseCanExecuteChanged(); NextWaitingPageCommand?.RaiseCanExecuteChanged();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}

public sealed record TrafficLeaseRow(
    string LeaseId, string ResourceId, string ResourceType, int Capacity,
    string TaskId, string StepId, string VehicleId, string TaskStage,
    DateTime? AcquiredAt, DateTime? RenewedAt, DateTime? ExpiresAt, bool HeartbeatExpired)
{
    public bool CanRelease => !string.IsNullOrWhiteSpace(LeaseId) &&
        TaskStage is "Completed" or "Faulted" or "Cancelled";
    public string HeartbeatState => string.IsNullOrWhiteSpace(LeaseId) ? "空闲" : HeartbeatExpired ? "已过期（仍锁定）" : "正常";

    public static TrafficLeaseRow Empty(TrafficResourceSnapshot resource) => new(
        string.Empty, resource.ResourceId, resource.Type?.ToString() ?? "Unknown",
        resource.Capacity, "—", "—", "—", "空闲", null, null, null, false);

    public static TrafficLeaseRow From(TrafficResourceSnapshot resource, TrafficResourceLeaseSnapshot lease) => new(
        lease.LeaseId, resource.ResourceId, resource.Type?.ToString() ?? "Unknown",
        resource.Capacity, lease.TaskId, lease.OwnerStepId, lease.VehicleId,
        lease.TaskStage?.ToString() ?? "任务缺失", lease.AcquiredAt.LocalDateTime,
        lease.RenewedAt.LocalDateTime, lease.ExpiresAt.LocalDateTime, lease.HeartbeatExpired);
}

public sealed record ManualReleaseRequestRow(
    string RequestId, string ResourceId, string TaskId, string VehicleId,
    string RequestedBy, string RequestedByDisplayName, string Reason,
    ManualReleaseWorkflowStatus Status, DateTime RequestedAt, DateTime ExpiresAt,
    string ReviewedBy, string ReviewComment);

public sealed record TrafficReleaseAuditRow(
    string ResourceId, string TaskId, string VehicleId, string Operator,
    string RequestedBy, string ApprovedBy, string Reason,
    string PreviousTaskStage, bool HeartbeatExpired, DateTime ReleasedAt);

public sealed record EdgeTrafficRow(
    string EdgeId, string From, string To, EdgeTrafficState State,
    string VehicleIds, string ResourceIds, DateTime StateSince,
    double DynamicPenalty, string Message);

public sealed record TrafficDeadlockRow(
    string CandidateId, string EdgeId, string VehicleIds, string ResourceIds,
    DateTime DetectedAt, string Reason, string Suggestion);

public sealed record TrafficRequestRow(string StepId, string TaskId, string VehicleId, string Route, string Edges,
    string Resources, string State, string Reason, string Blockers, DateTime RequestedAt, DateTime UpdatedAt)
{
    public string HeldResources { get; init; } = "无";
    public TrafficRequestSnapshot? SourceSnapshot { get; init; }
    public string WaitingAlertText => SourceSnapshot?.WaitingAlertLevel switch
    {
        TrafficWaitingAlertLevel.Normal=>"等待中", TrafficWaitingAlertLevel.Warning=>"超时预警",
        TrafficWaitingAlertLevel.Critical=>"长时间等待", TrafficWaitingAlertLevel.Unknown=>"时长未知",
        TrafficWaitingAlertLevel.ClockAnomaly=>"时钟异常", _=>"未评估"
    };
    public bool WaitingWarning => SourceSnapshot?.WaitingAlertLevel==TrafficWaitingAlertLevel.Warning;
    public bool WaitingCritical => SourceSnapshot?.WaitingAlertLevel==TrafficWaitingAlertLevel.Critical;
    public string BlockingObservationText => SourceSnapshot is not { } r ? "未知" : r.Blockers.Count==0 ? "无当前资源阻塞" :
        !r.BlockingClockValid ? "阻塞时钟异常" :
        !r.BlockingObservationCurrent || r.BlockingSince==null ? "当前受阻，起点待观察" :
        (r.BlockingStartKnown ? "持续阻塞自 " : "至少受阻自 ")+r.BlockingSince.Value.LocalDateTime.ToString("MM-dd HH:mm:ss");
    public string LastBlockingClearedText => SourceSnapshot?.LastBlockingClearedAt?.LocalDateTime.ToString("MM-dd HH:mm:ss")??"未观察到解除";
    public string PriorityText => SourceSnapshot?.Priority?.ToString() ?? "未知";
    public string WaitingStartText => SourceSnapshot?.WaitingSince is not { } start ? "起点未知" : start.LocalDateTime.ToString("MM-dd HH:mm:ss") + (SourceSnapshot.WaitingStartKnown ? "" : "（首次观察）");
    public string WaitingDurationText => SourceSnapshot?.State != "Waiting" ? "—" : SourceSnapshot.WaitingClockValid == false ? "时钟异常" :
        SourceSnapshot.WaitingSeconds is not { } seconds ? "未知" : (SourceSnapshot.WaitingStartKnown ? "" : "至少 ") + FormatWait(seconds);
    private static string FormatWait(double seconds)
    {
        var span=TimeSpan.FromSeconds(Math.Max(0,seconds));
        return (span.Days>0?span.Days+"天 ":"")+span.ToString(@"hh\:mm\:ss");
    }
    public string MapCaption => TaskId + " · " + VehicleId + " · " + State;
    public static string StateText(string state) => state switch
    { "Requested" => "申请中", "Waiting" => "冲突等待", "Acquired" => "已获得", "Held" => "占用保持", "Released" => "已释放",
      "BlockingStarted" => "阻塞首次观察", "BlockersChanged" => "阻塞方变化", "BlockingCleared" => "阻塞解除观察", "Retained" => "异常保留", "Inactive" => "任务非运行", "NoLease" => "当前无占用", "NotRequired" => "无需路权", _ => state };
    public static TrafficRequestRow From(TrafficRequestSnapshot r) => new(r.StepId, r.TaskId, r.VehicleId,
        string.IsNullOrWhiteSpace(r.Source) && string.IsNullOrWhiteSpace(r.Destination) ? "路径未记录" : r.Source + " → " + r.Destination, r.EdgeIds.Count == 0 ? "未记录" : string.Join(", ", r.EdgeIds), string.Join(", ", r.ResourceIds), StateText(r.State), r.Reason,
        r.Blockers.Count == 0 ? "无当前阻塞方" : string.Join("；", r.Blockers.Select(b => $"{b.ResourceId}: {b.TaskId}/{b.VehicleId}/{b.StepId}" + (b.HeartbeatExpired ? "（心跳过期仍锁定）" : ""))),
        r.RequestedAt.LocalDateTime, r.UpdatedAt.LocalDateTime) { SourceSnapshot = r, HeldResources = r.HeldResourceIds.Count == 0 ? "无" : string.Join(", ", r.HeldResourceIds) };
}
public sealed record TrafficEventRow(string StepId, string TaskId, string VehicleId, string Kind, string Resources, string Reason, DateTime OccurredAt)
{
    public static TrafficEventRow From(TrafficLifecycleEvent e) => new(e.StepId, e.TaskId, e.VehicleId,
        TrafficRequestRow.StateText(e.Kind), string.Join(", ", e.ResourceIds), e.Reason, e.OccurredAt.LocalDateTime);
}
