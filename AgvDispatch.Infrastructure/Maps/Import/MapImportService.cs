using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Infrastructure.Maps;

public sealed class MapImportService : IMapImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public MapDefinition Import(MapImportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        if (!double.IsFinite(request.CoordinateScale) || request.CoordinateScale <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "地图坐标缩放比例必须是大于0的有限数值。");
        }

        var fullPath = Path.GetFullPath(request.Path);
        var format = request.Format == MapImportFormat.Auto
            ? DetectFormat(fullPath)
            : request.Format;
        return format switch
        {
            MapImportFormat.NormalizedJson => ImportJson(fullPath, request.CoordinateScale),
            MapImportFormat.CsvDirectory => ImportCsvDirectory(fullPath, request.CoordinateScale),
            MapImportFormat.RobokitSmap => ImportSmap(fullPath,request.CoordinateScale),
            _ => throw new NotSupportedException($"不支持地图导入格式{format}。")
        };
    }

    private static MapImportFormat DetectFormat(string path)
    {
        if(File.Exists(path)&&string.Equals(Path.GetExtension(path),".smap",StringComparison.OrdinalIgnoreCase))return MapImportFormat.RobokitSmap;
        if (Directory.Exists(path))
        {
            return MapImportFormat.CsvDirectory;
        }

        if (File.Exists(path) && string.Equals(
                Path.GetExtension(path),
                ".json",
                StringComparison.OrdinalIgnoreCase))
        {
            return MapImportFormat.NormalizedJson;
        }

        throw new FileNotFoundException(
            "地图导入路径必须是标准JSON文件或包含三张CSV的目录。",
            path);
    }

    private static MapDefinition ImportSmap(string path,double scale)
    {
        if(scale!=1)throw new InvalidDataException("Smap使用厂家米制世界坐标，不允许额外缩放；坐标确认须单独核定。");
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("Smap文件超过64MB。");
        var bytes=File.ReadAllBytes(path);
        if(Smap3DPackageReader.IsZip(bytes))
        {
            var package=new Smap3DPackageReader().Read(bytes,path);
            return RobokitPathEvidenceReader.Attach(package.PlaneBytes,bytes,package.Plane.Definition);
        }
        return RobokitPathEvidenceReader.Attach(bytes,bytes,new Smap2DImportService().Read(bytes,path).Definition);
    }

    private static MapDefinition ImportJson(string path, double scale)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("地图JSON不存在。", path);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;
        if (TryGetProperty(root, "map", out var nested))
        {
            root = nested;
        }
        else if (TryGetProperty(root, "version", out var version) &&
                 TryGetProperty(version, "definition", out var backupDefinition))
        {
            root = backupDefinition;
        }

        var dto = root.Deserialize<MapDefinitionDto>(JsonOptions) ??
            throw new InvalidDataException("地图JSON内容为空。");
        if (dto.Nodes is null || dto.Edges is null || dto.Resources is null)
        {
            throw new InvalidDataException("地图JSON的nodes、edges和resources不能为null。");
        }
        return Convert(dto, path, scale);
    }

    private static MapDefinition ImportCsvDirectory(string path, double scale)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(path);
        }

        var nodesPath = Path.Combine(path, "nodes.csv");
        var edgesPath = Path.Combine(path, "edges.csv");
        var resourcesPath = Path.Combine(path, "resources.csv");
        foreach (var required in new[] { nodesPath, edgesPath, resourcesPath })
        {
            if (!File.Exists(required))
            {
                throw new FileNotFoundException(
                    $"CSV地图包缺少{Path.GetFileName(required)}。",
                    required);
            }
        }

        var nodes = ReadCsv(nodesPath).Select(row => new MapNode(
            Required(row, "Id"),
            Number(row, "X") * scale,
            Number(row, "Y") * scale,
            EnumValue(row, "Type", MapNodeType.Waypoint),
            Boolean(row, "Enabled", true),
            List(row, "ResourceIds"),
            Optional(row, "AgvSiteId"))).ToArray();
        var nodeById = nodes
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var edges = ReadCsv(edgesPath).Select(row =>
        {
            var from = Required(row, "From");
            var to = Required(row, "To");
            var distance = OptionalNumber(row, "Distance") * scale;
            if (distance <= 0 && nodeById.TryGetValue(from, out var fromNode) &&
                nodeById.TryGetValue(to, out var toNode))
            {
                distance = Math.Sqrt(
                    Math.Pow(toNode.X - fromNode.X, 2) +
                    Math.Pow(toNode.Y - fromNode.Y, 2));
            }

            var edge = new MapEdge(
                Required(row, "Id"),
                from,
                to,
                distance,
                OptionalNumber(row, "SpeedLimit", 1),
                Direction(row),
                Boolean(row, "Enabled", true),
                List(row, "ResourceIds"))
            {
                GeometryKind = EnumValue(row, "GeometryKind", MapEdgeGeometryKind.Line),
                ControlPoints = Boundary(row, "ControlPoints", scale)
            };
            return WithGeometryDistance(edge, nodeById);
        }).ToArray();
        var resources = ReadCsv(resourcesPath).Select(row => new TrafficResourceDefinition(
            Required(row, "Id"),
            EnumValue(row, "Type", TrafficResourceType.Intersection),
            Integer(row, "Capacity", 1),
            Boundary(row, "Boundary", scale))).ToArray();
        return new MapDefinition(
            new DirectoryInfo(path).Name,
            path,
            "LocalCartesian",
            scale == 1 ? "source-unit" : "scaled",
            nodes,
            edges,
            resources);
    }

    private static MapDefinition Convert(MapDefinitionDto dto, string source, double scale)
    {
        var nodes = dto.Nodes.Select(node => new MapNode(
            node.Id.Trim(),
            node.X * scale,
            node.Y * scale,
            node.Type,
            node.Enabled,
            CleanList(node.ResourceIds),
            string.IsNullOrWhiteSpace(node.AgvSiteId) ? null : node.AgvSiteId.Trim()) { LayerId = node.LayerId }).ToArray();
        var nodeById = nodes
            .GroupBy(node => node.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var edges = dto.Edges.Select(edge =>
        {
            var distance = edge.Distance * scale;
            if (distance <= 0 && nodeById.TryGetValue(edge.From, out var from) &&
                nodeById.TryGetValue(edge.To, out var to))
            {
                distance = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
            }

            var importedEdge = new MapEdge(
                edge.Id.Trim(),
                edge.From.Trim(),
                edge.To.Trim(),
                distance,
                edge.SpeedLimit,
                edge.Bidirectional,
                edge.Enabled,
                CleanList(edge.ResourceIds))
            {
                LayerId = edge.LayerId, GeometryKind = edge.GeometryKind, VendorPath = edge.VendorPath,
                ControlPoints = edge.ControlPoints?.Select(point => new MapPoint2D(point.X * scale, point.Y * scale)).ToArray()
            };
            return WithGeometryDistance(importedEdge, nodeById);
        }).ToArray();
        return new MapDefinition(
            string.IsNullOrWhiteSpace(dto.Name) ? Path.GetFileNameWithoutExtension(source) : dto.Name,
            source,
            string.IsNullOrWhiteSpace(dto.CoordinateSystem) ? "LocalCartesian" : dto.CoordinateSystem,
            string.IsNullOrWhiteSpace(dto.Unit) ? "source-unit" : dto.Unit,
            nodes,
            edges,
            dto.Resources.Select(resource => new TrafficResourceDefinition(
                resource.Id.Trim(),
                resource.Type,
                resource.Capacity,
                resource.Boundary?.Select(point => new MapPoint2D(point.X * scale, point.Y * scale)).ToArray()) { LayerId = resource.LayerId }).ToArray()) { Layers = dto.Layers, EditorSettings = dto.EditorSettings,ImportWarnings=dto.ImportWarnings,RobokitSource=dto.RobokitSource };
    }

    private static MapEdge WithGeometryDistance(MapEdge edge, IReadOnlyDictionary<string, MapNode> nodes) =>
        edge.GeometryKind != MapEdgeGeometryKind.Line &&
        nodes.TryGetValue(edge.From, out var from) && nodes.TryGetValue(edge.To, out var to) &&
        MapEdgeGeometry.TryValidate(edge, from, to, out _)
            ? edge with { Distance = MapEdgeGeometry.Length(edge, from, to) }
            : edge;

    private static IReadOnlyList<Dictionary<string, string>> ReadCsv(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'))
            .ToArray();
        if (lines.Length == 0)
        {
            throw new InvalidDataException($"CSV文件为空：{path}");
        }

        var headers = ParseCsvLine(lines[0]).Select(value => value.Trim()).ToArray();
        var rows = new List<Dictionary<string, string>>();
        for (var index = 1; index < lines.Length; index++)
        {
            var values = ParseCsvLine(lines[index]);
            if (values.Count != headers.Length)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)}第{index + 1}行字段数与表头不一致。");
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < headers.Length; column++)
            {
                row[headers[column]] = values[column].Trim();
            }
            rows.Add(row);
        }

        return rows;
    }

    private static IReadOnlyList<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("CSV存在未闭合引号。");
        }
        values.Add(current.ToString());
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new InvalidDataException($"CSV必填字段{name}为空。");

    private static string? Optional(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static double Number(IReadOnlyDictionary<string, string> row, string name) =>
        double.TryParse(Required(row, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value)
            ? value
            : throw new InvalidDataException($"CSV字段{name}不是有效数字。");

    private static double OptionalNumber(
        IReadOnlyDictionary<string, string> row,
        string name,
        double defaultValue = 0) =>
        !row.TryGetValue(name, out var text) || string.IsNullOrWhiteSpace(text)
            ? defaultValue
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
              double.IsFinite(value)
                ? value
                : throw new InvalidDataException($"CSV字段{name}不是有效数字。");

    private static int Integer(
        IReadOnlyDictionary<string, string> row,
        string name,
        int defaultValue) =>
        !row.TryGetValue(name, out var text) || string.IsNullOrWhiteSpace(text)
            ? defaultValue
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new InvalidDataException($"CSV字段{name}不是有效整数。");

    private static bool Boolean(
        IReadOnlyDictionary<string, string> row,
        string name,
        bool defaultValue) =>
        !row.TryGetValue(name, out var text) || string.IsNullOrWhiteSpace(text)
            ? defaultValue
            : bool.TryParse(text, out var value)
                ? value
                : text == "1" ? true : text == "0" ? false
                : throw new InvalidDataException($"CSV字段{name}不是有效布尔值。");

    private static T EnumValue<T>(
        IReadOnlyDictionary<string, string> row,
        string name,
        T defaultValue) where T : struct, Enum =>
        !row.TryGetValue(name, out var text) || string.IsNullOrWhiteSpace(text)
            ? defaultValue
            : Enum.TryParse<T>(text, true, out var value)
                ? value
                : throw new InvalidDataException($"CSV字段{name}不是有效的{name}枚举。");

    private static bool Direction(IReadOnlyDictionary<string, string> row)
    {
        if (row.TryGetValue("Bidirectional", out var bidirectional) &&
            !string.IsNullOrWhiteSpace(bidirectional))
        {
            return Boolean(row, "Bidirectional", false);
        }

        if (row.TryGetValue("Direction", out var direction) &&
            !string.IsNullOrWhiteSpace(direction))
        {
            if (direction.Equals("Bidirectional", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("TwoWay", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("双向", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (direction.Equals("OneWay", StringComparison.OrdinalIgnoreCase) ||
                direction.Equals("单向", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            throw new InvalidDataException($"CSV Direction值无效：{direction}。");
        }

        return false;
    }

    private static string[] List(IReadOnlyDictionary<string, string> row, string name) =>
        row.TryGetValue(name, out var value) ? CleanList(value.Split('|', ';')) : [];

    private static IReadOnlyList<MapPoint2D>? Boundary(
        IReadOnlyDictionary<string, string> row,
        string name,
        double scale)
    {
        if (!row.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value)) return null;
        return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item =>
            {
                var pair = item.Split(':', StringSplitOptions.TrimEntries);
                if (pair.Length != 2 ||
                    !double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.IsFinite(x) || !double.IsFinite(y))
                    throw new InvalidDataException($"CSV字段{name}边界点无效：{item}，应使用x:y|x:y格式。");
                return new MapPoint2D(x * scale, y * scale);
            }).ToArray();
    }

    private static string[] CleanList(IEnumerable<string>? values) => values?
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray() ?? [];

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed class MapDefinitionDto
    {
        public List<string> ImportWarnings {get;set;}=[];
        public RobokitMapSource? RobokitSource { get; set; }
        public string Name { get; set; } = string.Empty;
        public string CoordinateSystem { get; set; } = "LocalCartesian";
        public string Unit { get; set; } = "m";
        public MapEditorSettings? EditorSettings { get; set; }
        public List<MapEditorLayer> Layers { get; set; } = [];
        public List<MapNodeDto> Nodes { get; set; } = [];
        public List<MapEdgeDto> Edges { get; set; } = [];
        public List<TrafficResourceDto> Resources { get; set; } = [];
    }

    private sealed class MapNodeDto
    {
        public string LayerId { get; set; } = "navigation";
        public string Id { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public MapNodeType Type { get; set; }
        public bool Enabled { get; set; } = true;
        public string[] ResourceIds { get; set; } = [];
        public string? AgvSiteId { get; set; }
    }

    private sealed class MapEdgeDto
    {
        public RobokitPathEvidence? VendorPath { get; set; }
        public string LayerId { get; set; } = "navigation";
        public string Id { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public double Distance { get; set; }
        public double SpeedLimit { get; set; } = 1;
        public bool Bidirectional { get; set; }
        public bool Enabled { get; set; } = true;
        public string[] ResourceIds { get; set; } = [];
        public MapEdgeGeometryKind GeometryKind { get; set; } = MapEdgeGeometryKind.Line;
        public MapPointDto[]? ControlPoints { get; set; }
    }

    private sealed class TrafficResourceDto
    {
        public string LayerId { get; set; } = "navigation";
        public string Id { get; set; } = string.Empty;
        public TrafficResourceType Type { get; set; }
        public int Capacity { get; set; } = 1;
        public MapPointDto[]? Boundary { get; set; }
    }

    private sealed class MapPointDto
    {
        public double X { get; set; }
        public double Y { get; set; }
    }
}
