namespace AgvDispatch.Application.Abstractions;

// Inspects the loaded runtime configuration without reading or writing any device.
public interface IPlcConfigurationInspector
{
    string? GetSignalConfigurationError(string stationId, string variableName, bool requiresWrite);
}
