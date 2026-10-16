using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Service;

public sealed class MapDefinitionProvider
{
    public MapDefinitionProvider(
        MapOptions options,
        IMapImportService importer,
        IMapValidationService validator)
    {
        if (!options.Enabled)
        {
            return;
        }

        var error = options.GetConfigurationError();
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        Definition = string.IsNullOrWhiteSpace(options.ImportPath)
            ? options.BuildDefinition()
            : importer.Import(new MapImportRequest(
                options.ImportPath,
                options.ImportFormat,
                options.CoordinateScale));
        ValidationReport = validator.Validate(Definition, options.CoordinateTolerance);
        WriteReport(options.ValidationReportPath, Definition, ValidationReport);
        if (!ValidationReport.IsValid)
        {
            throw new InvalidOperationException(
                $"地图校验失败：错误{ValidationReport.ErrorCount}项、" +
                $"警告{ValidationReport.WarningCount}项。首个错误：" +
                ValidationReport.Issues.First(issue =>
                    issue.Severity == MapValidationSeverity.Error).Message);
        }
    }

    public MapDefinition? Definition { get; }
    public MapValidationReport? ValidationReport { get; }

    private static void WriteReport(
        string path,
        MapDefinition definition,
        MapValidationReport report)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(new
        {
            GeneratedAt = DateTimeOffset.Now,
            Map = definition.Name,
            definition.Source,
            definition.CoordinateSystem,
            definition.Unit,
            report.IsValid,
            report.NodeCount,
            report.EdgeCount,
            report.ResourceCount,
            report.ErrorCount,
            report.WarningCount,
            report.Issues
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        File.WriteAllText(path, json);
    }
}
