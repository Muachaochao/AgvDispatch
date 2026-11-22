using System.Threading.Channels;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Connectors.Plc;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Stations;
using AgvDispatch.Domain.Tasks;
using AgvDispatch.Application.Runtime;

namespace AgvDispatch.Service;

public sealed class StationStateService : IsolatedBackgroundService, IStationStateService
{
    private readonly object _sync = new();
    private readonly ILogger<StationStateService> _logger;
    private readonly StationStateOptions _options;
    private readonly IPlcSignalCache _signalCache;
    private readonly IActiveTaskLookup _activeTaskLookup;
    private readonly TimeProvider _clock;
    private readonly string[] _stationIds;
    private readonly Channel<string> _refreshRequests = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<string, StationStateSnapshot> _snapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<ActiveDispatchTask>> _activeTasks =
        new Dictionary<string, IReadOnlyList<ActiveDispatchTask>>(StringComparer.OrdinalIgnoreCase);
    private long _version;

    public StationStateService(
        ILogger<StationStateService> logger,
        StationStateOptions options,
        IPlcSignalCache signalCache,
        IActiveTaskLookup activeTaskLookup,
        PlcSignalCatalog catalog,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _options = options;
        _signalCache = signalCache;
        _activeTaskLookup = activeTaskLookup;
        _clock = clock ?? TimeProvider.System;
        _stationIds = catalog.Signals
            .Where(signal => string.Equals(
                signal.DeviceType, "ChipRemovalStation", StringComparison.OrdinalIgnoreCase))
            .Where(signal => string.Equals(
                signal.VariableName, "FullBoxRequested", StringComparison.OrdinalIgnoreCase))
            .Select(signal => signal.StationId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(stationId => stationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (_options.RefreshIntervalMs < 250)
        {
            throw new InvalidOperationException("StationState:RefreshIntervalMs不能小于250ms。");
        }
    }

    public event Action<StationStateChanged>? StationStateChanged;

    public long Version
    {
        get
        {
            lock (_sync)
            {
                return _version;
            }
        }
    }

    public IReadOnlyList<StationStateSnapshot> GetAll()
    {
        lock (_sync)
        {
            return _snapshots.Values
                .OrderBy(snapshot => snapshot.StationId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(string stationId, out StationStateSnapshot snapshot)
    {
        lock (_sync)
        {
            return _snapshots.TryGetValue(stationId, out snapshot!);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _signalCache.SignalChanged += OnSignalChanged;
        try
        {
            await RefreshAllAsync(stoppingToken);
            var initial = GetAll();
            _logger.LogInformation(
                "工位状态服务启动。工位数={StationCount}，可创建任务={ReadyCount}，冲突={ConflictCount}，故障={FaultCount}，离线={OfflineCount}，任务刷新周期={RefreshIntervalMs}ms",
                initial.Count,
                initial.Count(station => station.CanCreateTask),
                initial.Count(station => station.State == StationOperationalState.SignalConflict),
                initial.Count(station => station.State == StationOperationalState.Faulted),
                initial.Count(station => station.State == StationOperationalState.Offline),
                _options.RefreshIntervalMs);

            while (!stoppingToken.IsCancellationRequested)
            {
                var signalTask = _refreshRequests.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var periodicTask = Task.Delay(_clock is SimulationTimeProvider simulation
                    ? simulation.ScaleForBusiness(TimeSpan.FromMilliseconds(_options.RefreshIntervalMs))
                    : TimeSpan.FromMilliseconds(_options.RefreshIntervalMs), stoppingToken);
                var completed = await Task.WhenAny(signalTask, periodicTask);

                if (completed == periodicTask)
                {
                    await RefreshAllAsync(stoppingToken);
                    continue;
                }

                if (!await signalTask)
                {
                    break;
                }

                var stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (_refreshRequests.Reader.TryRead(out var stationId))
                {
                    stations.Add(stationId);
                }

                foreach (var stationId in stations)
                {
                    EvaluateAndUpdate(stationId, _clock.GetLocalNow());
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常停止。
        }
        finally
        {
            _signalCache.SignalChanged -= OnSignalChanged;
            _refreshRequests.Writer.TryComplete();
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tasks = await _activeTaskLookup.GetActiveTasksAsync(cancellationToken);
            _activeTasks = tasks
                .GroupBy(task => task.StationId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<ActiveDispatchTask>)group.ToArray(),
                    StringComparer.OrdinalIgnoreCase);

            var evaluatedAt = _clock.GetLocalNow();
            foreach (var stationId in _stationIds)
            {
                EvaluateAndUpdate(stationId, evaluatedAt);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "刷新工位状态时读取未完成任务失败，本轮保留上次任务快照。");
        }
    }

    private void EvaluateAndUpdate(string stationId, DateTimeOffset evaluatedAt)
    {
        var signals = _signalCache.GetStation(stationId);
        var tasks = _activeTasks.TryGetValue(stationId, out var activeTasks)
            ? activeTasks
            : Array.Empty<ActiveDispatchTask>();
        var evaluation = StationStateEvaluator.Evaluate(stationId, signals, tasks);

        StationStateSnapshot? previous;
        StationStateSnapshot current;
        lock (_sync)
        {
            _snapshots.TryGetValue(stationId, out previous);
            if (previous is not null && HasSameBusinessState(previous, evaluation))
            {
                return;
            }

            var version = ++_version;
            current = new StationStateSnapshot(
                stationId,
                evaluation.State,
                evaluation.CanCreateTask,
                evaluation.BlockReason,
                evaluation.FullBoxRequested,
                evaluation.BoxPresent,
                evaluation.Faulted,
                evaluation.CommunicationOnline,
                evaluation.SignalQuality,
                evaluation.ActiveTaskId,
                evaluation.ActiveTaskCount,
                evaluatedAt,
                previous?.State == evaluation.State
                    ? previous.StateChangedAt
                    : evaluatedAt,
                version);
            _snapshots[stationId] = current;
        }

        Publish(new StationStateChanged(stationId, previous, current, evaluatedAt));
    }

    private void OnSignalChanged(PlcSignalChanged change)
    {
        if (Array.BinarySearch(
                _stationIds,
                change.StationId,
                StringComparer.OrdinalIgnoreCase) >= 0)
        {
            _refreshRequests.Writer.TryWrite(change.StationId);
        }
    }

    private void Publish(StationStateChanged change)
    {
        var subscribers = StationStateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<StationStateChanged> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(change);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "工位状态变化订阅方处理失败。StationId={StationId}",
                    change.StationId);
            }
        }
    }

    private static bool HasSameBusinessState(
        StationStateSnapshot snapshot,
        StationStateEvaluation evaluation) =>
        snapshot.State == evaluation.State &&
        snapshot.CanCreateTask == evaluation.CanCreateTask &&
        snapshot.BlockReason == evaluation.BlockReason &&
        snapshot.FullBoxRequested == evaluation.FullBoxRequested &&
        snapshot.BoxPresent == evaluation.BoxPresent &&
        snapshot.Faulted == evaluation.Faulted &&
        snapshot.CommunicationOnline == evaluation.CommunicationOnline &&
        snapshot.SignalQuality == evaluation.SignalQuality &&
        snapshot.ActiveTaskId == evaluation.ActiveTaskId &&
        snapshot.ActiveTaskCount == evaluation.ActiveTaskCount;
}
