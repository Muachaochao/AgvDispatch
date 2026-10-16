using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public interface IRoutePlanner
{
    RoutePlan? Plan(
        string source,
        string destination,
        IReadOnlySet<string> unavailableResourceIds);
}
