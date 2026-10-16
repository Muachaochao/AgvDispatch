using AgvDispatch.Domain.Maps;
namespace AgvDispatch.Application.Abstractions;

// Local editor assets only. No database, device, publishing or activation operations.
public interface ILocalMapEditorStore : ICadDetailClient
{
    string DirectoryPath { get; }
    Task<IReadOnlyList<MapProjectSummary>> ListAsync();
    Task<MapWorkspaceSnapshot> LoadAsync(string mapId);
    MapWorkspaceSnapshot Create(CreateMapRequest request);
    Task SaveAsync(MapWorkspaceSnapshot workspace);
    Task<MapWorkspaceSnapshot> OpenFileAsync(string path);
    Task ExportAsync(MapWorkspaceSnapshot workspace, string path);
    Task<MapWorkspaceSnapshot> RenderAsync(MapWorkspaceSnapshot workspace, GenerateMapUnderlayRequest request);
    MapCalibrationSnapshot Calibrate(IReadOnlyList<MapCalibrationPoint> points);
    MapDefinition Import(MapImportRequest request);
    MapValidationReport Validate(MapDefinition definition);
}
