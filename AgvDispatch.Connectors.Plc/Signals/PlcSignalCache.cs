using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Connectors.Plc;

public sealed class PlcSignalCache : IPlcSignalCache
{
    private readonly object _sync = new();
    private readonly Dictionary<string, PlcSignalSnapshot> _snapshots;
    private long _version;

    public PlcSignalCache(PlcSignalCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _snapshots = catalog.Signals.ToDictionary(
            signal => CreateKey(signal.StationId, signal.VariableName),
            signal => new PlcSignalSnapshot(
                signal.StationId,
                signal.VariableName,
                signal.ChineseName,
                signal.Address,
                signal.DataType,
                signal.Direction,
                signal.Access,
                signal.Enabled,
                null,
                signal.Enabled ? SignalQuality.Unknown : SignalQuality.Disabled,
                null,
                null,
                0),
            StringComparer.OrdinalIgnoreCase);
    }

    public event Action<PlcSignalChanged>? SignalChanged;

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

    public IReadOnlyList<PlcSignalSnapshot> GetAll()
    {
        lock (_sync)
        {
            return _snapshots.Values
                .OrderBy(snapshot => snapshot.StationId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyList<PlcSignalSnapshot> GetStation(string stationId)
    {
        lock (_sync)
        {
            return _snapshots.Values
                .Where(snapshot => string.Equals(
                    snapshot.StationId, stationId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(snapshot => snapshot.Address, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public bool TryGet(
        string stationId,
        string variableName,
        out PlcSignalSnapshot snapshot)
    {
        lock (_sync)
        {
            return _snapshots.TryGetValue(CreateKey(stationId, variableName), out snapshot!);
        }
    }

    public void ApplyReadings(IReadOnlyList<PlcSignalReading> readings)
    {
        var changes = new List<PlcSignalChanged>();
        lock (_sync)
        {
            foreach (var reading in readings)
            {
                var key = CreateKey(reading.StationId, reading.VariableName);
                if (!_snapshots.TryGetValue(key, out var previous) || !previous.Enabled)
                {
                    continue;
                }

                var valueChanged = previous.Value != reading.Value;
                var qualityChanged = previous.Quality != SignalQuality.Good;
                var version = ++_version;
                var current = previous with
                {
                    Value = reading.Value,
                    Quality = SignalQuality.Good,
                    UpdatedAt = reading.ReadAt,
                    ChangedAt = valueChanged ? reading.ReadAt : previous.ChangedAt,
                    Version = version,
                    ErrorMessage = null
                };
                _snapshots[key] = current;

                if (valueChanged || qualityChanged)
                {
                    changes.Add(new PlcSignalChanged(
                        current.StationId,
                        current.VariableName,
                        previous.Value,
                        current.Value,
                        previous.Quality,
                        current.Quality,
                        reading.ReadAt,
                        version));
                }
            }
        }

        Publish(changes);
    }

    public void MarkEnabledSignals(SignalQuality quality, string? errorMessage,
        IReadOnlySet<string>? stationIds = null)
    {
        if (quality == SignalQuality.Good)
        {
            throw new ArgumentOutOfRangeException(nameof(quality));
        }

        var occurredAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
        var changes = new List<PlcSignalChanged>();
        lock (_sync)
        {
            foreach (var (key, previous) in _snapshots.ToArray())
            {
                if (!previous.Enabled || (stationIds is not null && !stationIds.Contains(previous.StationId)) ||
                    (previous.Quality == quality && previous.ErrorMessage == errorMessage))
                {
                    continue;
                }

                var version = ++_version;
                var current = previous with
                {
                    Quality = quality,
                    Version = version,
                    ErrorMessage = errorMessage
                };
                _snapshots[key] = current;
                changes.Add(new PlcSignalChanged(
                    current.StationId,
                    current.VariableName,
                    previous.Value,
                    current.Value,
                    previous.Quality,
                    current.Quality,
                    occurredAt,
                    version));
            }
        }

        Publish(changes);
    }

    private void Publish(IEnumerable<PlcSignalChanged> changes)
    {
        var subscribers = SignalChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (var change in changes)
        {
            foreach (Action<PlcSignalChanged> subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    subscriber(change);
                }
                catch
                {
                    // 缓存更新不能被订阅方异常中断。
                }
            }
        }
    }

    private static string CreateKey(string stationId, string variableName) =>
        $"{stationId}\u001F{variableName}";
}
