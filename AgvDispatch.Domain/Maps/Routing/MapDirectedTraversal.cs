namespace AgvDispatch.Domain.Maps;

/// <summary>A traversal direction of a concrete edge, including reverse use of bidirectional edges.</summary>
public sealed record MapDirectedTraversal(string EdgeId, string FromNodeId, string ToNodeId)
{
    public bool Matches(string edgeId, string from, string to) =>
        string.Equals(EdgeId, edgeId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(FromNodeId, from, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ToNodeId, to, StringComparison.OrdinalIgnoreCase);
}
