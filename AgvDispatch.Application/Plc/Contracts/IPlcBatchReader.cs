using AgvDispatch.Domain.Devices;

namespace AgvDispatch.Application.Abstractions;

public interface IPlcBatchReader
{
    Task<IReadOnlyList<PlcSignalReading>> ReadEnabledSignalsAsync(
        CancellationToken cancellationToken);
}
