namespace AgvDispatch.Application.Abstractions;

/// <summary>Conservative admission tickets backed by durable task ownership, not road leases or PLC levels.</summary>
public sealed class DownstreamAdmissionOptions
{
    public bool Enabled { get; set; }
    public int Capacity { get; set; } = 3;
}

public sealed record DownstreamAdmissionSnapshot(bool Enabled, bool Allowed, int Capacity,
    IReadOnlyList<string> HoldingTasks, string Reason);
