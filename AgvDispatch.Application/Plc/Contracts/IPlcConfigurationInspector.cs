namespace AgvDispatch.Application.Abstractions;

// Inspects the loaded runtime configuration without reading or writing any device.
public interface IPlcConfigurationInspector
{
    string? GetSignalConfigurationError(string stationId, string variableName, bool requiresWrite);

    // Configuration preparation validates mapping and direction without granting live output permission.
    // Inspectors without an explicit preparation implementation retain the stricter execution check.
    string? GetSignalPreparationError(string stationId, string variableName, bool requiresWrite) =>
        GetSignalConfigurationError(stationId, variableName, requiresWrite);
}
