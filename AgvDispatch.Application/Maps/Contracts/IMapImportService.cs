using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public enum MapImportFormat
{
    Auto = 0,
    NormalizedJson = 10,
    CsvDirectory = 20,
    RobokitSmap = 30
}

public sealed record MapImportRequest(
    string Path,
    MapImportFormat Format = MapImportFormat.Auto,
    double CoordinateScale = 1.0);

public interface IMapImportService
{
    MapDefinition Import(MapImportRequest request);
}
