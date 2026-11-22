using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Connectors.Plc;

public sealed class PlcConnectionSupervisor : IPlcConnectionSupervisor
{
    private readonly object _sync = new();
    private readonly PlcOptions _options;
    private PlcConnectionSnapshot _current;

    public PlcConnectionSupervisor(PlcOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _current = new PlcConnectionSnapshot(
            options.DeviceId,
            options.DisplayName,
            ConnectionState.Disconnected,
            0,
            AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),
            null,
            null,
            null,
            null,
            options.Enabled ? "等待PLC采集服务连接。" : "PLC通信未启用。");
    }

    public event Action<PlcConnectionChanged>? ConnectionChanged;

    public PlcConnectionSnapshot Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public void ReportConnecting()
    {
        Update(previous => previous with
        {
            State = ConnectionState.Connecting,
            UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),
            Message = $"正在连接Siemens {_options.SiemensSeries} {_options.IpAddress}:{_options.Port}。"
        });
    }

    public void ReportSuccess(TimeSpan elapsed)
    {
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
        Update(previous => previous with
        {
            State = ConnectionState.Online,
            ConsecutiveFailures = 0,
            UpdatedAt = now,
            LastConnectedAt = previous.State == ConnectionState.Online
                ? previous.LastConnectedAt
                : now,
            LastSuccessfulReadAt = now,
            LastResponseTimeMs = elapsed.TotalMilliseconds,
            Message = null
        });
    }

    public void ReportFailure(Exception exception, TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var now = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow();
        Update(previous =>
        {
            var failures = previous.ConsecutiveFailures + 1;
            return previous with
            {
                State = failures >= _options.InterruptedFailureThreshold
                    ? ConnectionState.Interrupted
                    : ConnectionState.Degraded,
                ConsecutiveFailures = failures,
                UpdatedAt = now,
                LastFailureAt = now,
                LastResponseTimeMs = elapsed.TotalMilliseconds,
                Message = exception.Message
            };
        });
    }

    public void ReportFault(string message)
    {
        Update(previous => previous with
        {
            State = ConnectionState.Faulted,
            UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),
            Message = message
        });
    }

    public void ReportStopped(string message)
    {
        Update(previous => previous with
        {
            State = ConnectionState.Disconnected,
            UpdatedAt = AgvDispatch.Application.Runtime.BusinessClock.Provider.GetLocalNow(),
            Message = message
        });
    }

    private void Update(Func<PlcConnectionSnapshot, PlcConnectionSnapshot> update)
    {
        PlcConnectionChanged? change = null;
        lock (_sync)
        {
            var previous = _current;
            _current = update(previous);
            if (previous.State != _current.State)
            {
                change = new PlcConnectionChanged(
                    previous.State,
                    _current.State,
                    _current.UpdatedAt,
                    _current.Message);
            }
        }

        if (change is not null)
        {
            Publish(change);
        }
    }

    private void Publish(PlcConnectionChanged change)
    {
        var subscribers = ConnectionChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (Action<PlcConnectionChanged> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(change);
            }
            catch
            {
                // 连接状态更新不能被订阅方异常中断。
            }
        }
    }
}
