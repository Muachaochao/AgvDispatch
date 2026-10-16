namespace AgvDispatch.Connectors.Plc;

public static class PlcTaskConfigurationInspector
{
    public static string? Inspect(PlcOptions options, PlcSignalCatalog catalog, string stationId, string variableName, bool requiresWrite)
    {
        if (!catalog.TryGet(stationId, variableName, out var signal)) return "缺少信号映射";
        if (!signal.Enabled) return "点位未启用";
        if (!options.Enabled) return "所属 PLC 通信未启用";
        if (options.GetConfigurationError() is { } error) return error;
        if (requiresWrite && !options.AllowWrites) return "所属 PLC 写入开关未开启";
        if (requiresWrite && !string.Equals(signal.Access, "Write", StringComparison.OrdinalIgnoreCase)) return "点位没有 Write 权限";
        // The polling service reads enabled outputs too, so Write points can supply handshake readback.
        if (!string.Equals(signal.Access, "Read", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(signal.Access, "Write", StringComparison.OrdinalIgnoreCase)) return "点位 Access 必须为 Read 或 Write";
        return HslPlcGateway.GetBooleanMappingError(signal);
    }
}
