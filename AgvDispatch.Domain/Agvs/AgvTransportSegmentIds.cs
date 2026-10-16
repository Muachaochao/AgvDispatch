namespace AgvDispatch.Domain.Agvs;

public static class AgvTransportSegmentIds
{
    public static IReadOnlyList<string> For(string taskId, int nodeCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if(nodeCount is < 2 or > 2048) throw new ArgumentException("运输路径须包含2～2048个节点。");
        return Enumerable.Range(1,nodeCount-1).Select(i => i==nodeCount-1 ? taskId : $"{taskId}-{i:D2}").ToArray();
    }
}
