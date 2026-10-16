using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public interface IMapValidationService
{
    MapValidationReport Validate(MapDefinition definition, double coordinateTolerance = 0.01);
}
