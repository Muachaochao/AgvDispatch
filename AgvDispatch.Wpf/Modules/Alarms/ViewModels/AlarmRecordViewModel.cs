using System.Collections.ObjectModel;
using System.Windows.Threading;
using AgvDispatch.Application.Abstractions;
using Prism.Commands;
using Prism.Mvvm;
using Prism.Regions;

namespace AgvDispatch.Wpf.ViewModels;

public sealed class AlarmRecordViewModel : BindableBase, INavigationAware
{
    private readonly ITrafficControlClient _client;
    private readonly DispatcherTimer _timer;
    private TrafficControlSession? _session;
    private AlarmSnapshot? _selectedAlarm;
    private bool _activeOnly = true;
    private bool _isBusy;
    private string _acknowledgeComment = string.Empty;
    private string _statusMessage = "等待加载报警记录。";
    private int _openCount;
    private int _acknowledgedCount;
    private int _criticalCount;

    public AlarmRecordViewModel(ITrafficControlClient client)
    {
        _client = client;
        RefreshCommand = new DelegateCommand(async () => await RefreshAsync(), () => !IsBusy);
        AcknowledgeCommand = new DelegateCommand(async () => await AcknowledgeAsync(), CanAcknowledge);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += async (_, _) => await RefreshAsync(true);
    }

    public ObservableCollection<AlarmSnapshot> Alarms { get; } = [];
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand AcknowledgeCommand { get; }

    public AlarmSnapshot? SelectedAlarm
    {
        get => _selectedAlarm;
        set
        {
            if (SetProperty(ref _selectedAlarm, value))
            {
                AcknowledgeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ActiveOnly
    {
        get => _activeOnly;
        set
        {
            if (SetProperty(ref _activeOnly, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string CurrentUser => _session is null
        ? "未连接"
        : $"{_session.DisplayName}（{_session.WindowsIdentity}）";
    public string CurrentPermissions => _session?.Permissions.ToString() ?? "None";

    public string AcknowledgeComment
    {
        get => _acknowledgeComment;
        set => SetProperty(ref _acknowledgeComment, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                AcknowledgeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public int OpenCount
    {
        get => _openCount;
        private set => SetProperty(ref _openCount, value);
    }

    public int AcknowledgedCount
    {
        get => _acknowledgedCount;
        private set => SetProperty(ref _acknowledgedCount, value);
    }

    public int CriticalCount
    {
        get => _criticalCount;
        private set => SetProperty(ref _criticalCount, value);
    }

    public async void OnNavigatedTo(NavigationContext navigationContext)
    {
        await RefreshAsync();
        _timer.Start();
    }

    public bool IsNavigationTarget(NavigationContext navigationContext) => true;
    public void OnNavigatedFrom(NavigationContext navigationContext) => _timer.Stop();

    private async Task RefreshAsync(bool silent = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _session = await _client.GetSessionAsync(CancellationToken.None);
            RaisePropertyChanged(nameof(CurrentUser));
            RaisePropertyChanged(nameof(CurrentPermissions));
            var selectedId = SelectedAlarm?.AlarmId;
            var rows = await _client.GetAlarmsAsync(500, ActiveOnly, CancellationToken.None);
            Alarms.Clear();
            foreach (var row in rows) Alarms.Add(row);
            SelectedAlarm = Alarms.FirstOrDefault(item => item.AlarmId == selectedId);
            OpenCount = rows.Count(item => item.Status == AlarmLifecycleStatus.Open);
            AcknowledgedCount = rows.Count(item => item.Status == AlarmLifecycleStatus.Acknowledged);
            CriticalCount = rows.Count(item => item.Status != AlarmLifecycleStatus.Recovered &&
                                               item.Severity == AlarmSeverity.Critical);
            if (!silent)
            {
                StatusMessage = $"已加载{rows.Count}条报警记录。";
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"报警记录加载失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task AcknowledgeAsync()
    {
        var selected = SelectedAlarm;
        if (selected is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _client.AcknowledgeAlarmAsync(
                new AcknowledgeAlarmRequest(selected.AlarmId, AcknowledgeComment),
                CancellationToken.None);
            StatusMessage = result.Message;
            if (result.Accepted)
            {
                AcknowledgeComment = string.Empty;
                SelectedAlarm = null;
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"报警确认失败：{exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
        await RefreshAsync(true);
    }

    private bool CanAcknowledge() => !IsBusy &&
        SelectedAlarm?.Status == AlarmLifecycleStatus.Open &&
        _session is not null &&
        (_session.Permissions.HasFlag(TrafficPermission.Administer) ||
         _session.Permissions.HasFlag(TrafficPermission.AcknowledgeAlarm));
}
