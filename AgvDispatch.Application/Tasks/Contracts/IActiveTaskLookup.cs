using AgvDispatch.Domain.Tasks;

namespace AgvDispatch.Application.Abstractions;

public interface IActiveTaskLookup
{
    Task<IReadOnlyList<ActiveDispatchTask>> GetActiveTasksAsync(
        CancellationToken cancellationToken);
}
