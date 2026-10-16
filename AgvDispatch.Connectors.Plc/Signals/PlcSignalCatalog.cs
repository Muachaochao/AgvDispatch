using System.Globalization;
using System.Text;

namespace AgvDispatch.Connectors.Plc;

public sealed record PlcSignalDefinition(
    string Address,
    string VariableName,
    string ChineseName,
    string StationId,
    string DeviceType,
    int InstanceIndex,
    string DataType,
    string Direction,
    string Access,
    string AddressStatus,
    bool Enabled,
    string SetConditionZh,
    string ResetConditionZh,
    string SourceNote);

public sealed class PlcSignalCatalog
{
    private readonly Dictionary<string, PlcSignalDefinition> _signals;
    private readonly HashSet<string> _stations;

    private PlcSignalCatalog(IEnumerable<PlcSignalDefinition> signals)
    {
        _signals = new Dictionary<string, PlcSignalDefinition>(StringComparer.OrdinalIgnoreCase);
        _stations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var signal in signals)
        {
            var key = CreateKey(signal.StationId, signal.VariableName);
            if (!_signals.TryAdd(key, signal))
            {
                throw new InvalidDataException(
                    $"PLC CSV存在重复变量：{signal.StationId}/{signal.VariableName}。");
            }

            _stations.Add(signal.StationId);
        }
    }

    public int Count => _signals.Count;
    public static PlcSignalCatalog FromSignals(IEnumerable<PlcSignalDefinition> signals) => new(signals);
    public int EnabledCount => _signals.Values.Count(signal => signal.Enabled);
    public int InferredCount => _signals.Values.Count(
        signal => string.Equals(signal.AddressStatus, "Inferred", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyCollection<PlcSignalDefinition> Signals => _signals.Values;

    public bool ContainsStation(string stationId) => _stations.Contains(stationId);

    public bool TryGet(
        string stationId,
        string variableName,
        out PlcSignalDefinition definition) =>
        _signals.TryGetValue(CreateKey(stationId, variableName), out definition!);

    public static PlcSignalCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到PLC信号CSV配置文件。", path);
        }

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var rows = ParseRows(reader).GetEnumerator();
        if (!rows.MoveNext())
        {
            throw new InvalidDataException("PLC信号CSV为空。");
        }

        var headers = rows.Current
            .Select((name, index) => (Name: name.TrimStart('\uFEFF'), Index: index))
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        var required = new[]
        {
            "address", "variableName", "chineseName", "stationId", "deviceType",
            "instanceIndex", "dataType", "direction", "access", "addressStatus", "enabled",
            "setConditionZh", "resetConditionZh", "sourceNote"
        };
        var missing = required.Where(name => !headers.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"PLC信号CSV缺少字段：{string.Join(", ", missing)}。");
        }

        var signals = new List<PlcSignalDefinition>();
        var lineNumber = 1;
        while (rows.MoveNext())
        {
            lineNumber++;
            var row = rows.Current;
            if (row.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            string Value(string name) => headers[name] < row.Length ? row[headers[name]].Trim() : string.Empty;
            if (!int.TryParse(Value("instanceIndex"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out var instanceIndex))
            {
                throw new InvalidDataException($"PLC信号CSV第{lineNumber}行instanceIndex无效。");
            }

            if (!bool.TryParse(Value("enabled"), out var enabled))
            {
                throw new InvalidDataException($"PLC信号CSV第{lineNumber}行enabled必须为true或false。");
            }

            var signal = new PlcSignalDefinition(
                Value("address"), Value("variableName"), Value("chineseName"),
                Value("stationId"), Value("deviceType"), instanceIndex, Value("dataType"),
                Value("direction"), Value("access"), Value("addressStatus"), enabled,
                Value("setConditionZh"), Value("resetConditionZh"), Value("sourceNote"));

            if (string.IsNullOrWhiteSpace(signal.Address) ||
                string.IsNullOrWhiteSpace(signal.VariableName) ||
                string.IsNullOrWhiteSpace(signal.StationId))
            {
                throw new InvalidDataException($"PLC信号CSV第{lineNumber}行地址、变量名或工位编号为空。");
            }

            signals.Add(signal);
        }

        return new PlcSignalCatalog(signals);
    }

    private static string CreateKey(string stationId, string variableName) =>
        $"{stationId}\u001F{variableName}";

    private static IEnumerable<string[]> ParseRows(TextReader reader)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        while (reader.Read() is var value && value >= 0)
        {
            var character = (char)value;
            if (quoted)
            {
                if (character == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    yield return row.ToArray();
                    row.Clear();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("PLC信号CSV包含未闭合的引号。");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row.ToArray();
        }
    }
}
