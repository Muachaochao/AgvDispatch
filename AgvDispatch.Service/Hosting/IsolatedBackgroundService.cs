namespace AgvDispatch.Service;

/// <summary>
/// Starts each long-lived worker without inheriting the startup AsyncLocal context.
/// SqlSugarScope resolves connections through that context; inheriting a warmed
/// startup scope would let parallel workers share and close the same connection.
/// Per-operation evidence scopes and identities are established inside the worker.
/// </summary>
public abstract class IsolatedBackgroundService : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (ExecutionContext.IsFlowSuppressed()) return base.StartAsync(cancellationToken);
        using (ExecutionContext.SuppressFlow())
        {
            // Do not await while flow is suppressed: restore on this same thread.
            return base.StartAsync(cancellationToken);
        }
    }
}
