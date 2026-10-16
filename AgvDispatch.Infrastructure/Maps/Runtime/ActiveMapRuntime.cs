using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class ActiveMapRuntime : IActiveMapRuntime
{
    private ActiveMapRuntimeSnapshot? _current;
    public ActiveMapRuntimeSnapshot? Current => Volatile.Read(ref _current);
    public MapGraph? CurrentGraph => Current?.Graph;
    public void Replace(ActiveMapRuntimeSnapshot? snapshot)
    {
        Volatile.Write(ref _current, snapshot);
    }
}
