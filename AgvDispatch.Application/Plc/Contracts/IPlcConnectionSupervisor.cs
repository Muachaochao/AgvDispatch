using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

public interface IPlcConnectionSupervisor
{
    event Action<PlcConnectionChanged>? ConnectionChanged;

    PlcConnectionSnapshot Current { get; }
}
