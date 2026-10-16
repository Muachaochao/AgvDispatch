using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Domain.Devices;
using AgvDispatch.Domain.Maps;
using Prism.Mvvm;

namespace AgvDispatch.Wpf.ViewModels;

// Read-only rendering of the service's active version. Never use the editor draft as a runtime map.
public sealed class RuntimeMapScene : BindableBase
{
    private MapDefinition? _map;
    private double _minX, _minY, _scale, _offsetX, _offsetY;
    private string _message = "尚未激活运行地图，请在地图管理中发布并激活。";
    public ObservableCollection<MapOverviewNode> Nodes { get; } = [];
    public ObservableCollection<MapOverviewEdge> Edges { get; } = [];
    public ObservableCollection<RuntimeAgvMarker> Vehicles { get; } = [];
    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public void SetMap(MapDefinition? map)
    {
        _map = null; Nodes.Clear(); Edges.Clear(); Vehicles.Clear();
        if (map is null) { Message = "尚未激活运行地图，请在地图管理中发布并激活。"; return; }
        // Robot poses are metres in the Robokit map coordinate system, not CAD pixels or millimetres.
        if (!string.Equals(map.Unit, "m", StringComparison.OrdinalIgnoreCase))
        { Message = $"运行地图单位为 {map.Unit}，不能直接叠加机器人米制定位，请先完成坐标转换。"; return; }
        var nodes = map.Nodes.Where(n => double.IsFinite(n.X) && double.IsFinite(n.Y))
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (nodes.Count == 0) { Message = "运行地图没有有效站点。"; return; }
        var curves = new List<(MapEdge Edge, IReadOnlyList<MapPoint2D> Samples)>();
        foreach (var edge in map.Edges)
            if (nodes.TryGetValue(edge.From, out var from) && nodes.TryGetValue(edge.To, out var to) &&
                MapEdgeGeometry.TryValidate(edge, from, to, out _))
                curves.Add((edge, MapEdgeGeometry.Sample(edge, from, to, 128)));
        var points = nodes.Values.Select(n => new MapPoint2D(n.X, n.Y))
            .Concat(curves.SelectMany(c => c.Samples)).ToArray();
        _minX = points.Min(p => p.X); _minY = points.Min(p => p.Y);
        var width = Math.Max(1, points.Max(p => p.X) - _minX);
        var height = Math.Max(1, points.Max(p => p.Y) - _minY);
        _scale = Math.Min(800 / width, 420 / height);
        _offsetX = (900 - width * _scale) / 2;
        _offsetY = (520 + height * _scale) / 2;
        foreach (var (edge, samples) in curves)
        {
            var projected = new PointCollection(samples.Select(p => Project(p.X, p.Y))); projected.Freeze();
            Edges.Add(new(projected[0].X, projected[0].Y, projected[^1].X, projected[^1].Y,
                !edge.Enabled ? "#475569" : edge.Bidirectional ? "#487088" : "#D5A652", edge.Enabled ? 3 : 1)
                { Points = projected });
        }
        foreach (var node in nodes.Values)
        {
            var p = Project(node.X, node.Y);
            Nodes.Add(new(node.Id, p.X - 6, p.Y - 6, !node.Enabled ? "#475569" : node.Type switch
            { MapNodeType.Station => "#34C799", MapNodeType.Intersection => "#A78BFA",
                MapNodeType.ChargingPoint => "#F5CC66", _ => "#7298AE" }));
        }
        _map = map;
        Message = $"{map.Name} · {nodes.Count} 站点 / {Edges.Count} 路段 · 坐标 m";
    }

    public void UpdateVehicles(IEnumerable<AgvDiagnostic> vehicles, DateTimeOffset now)
    {
        Vehicles.Clear();
        if (_map is null) return;
        foreach (var vehicle in vehicles)
        {
            if (vehicle.X is not { } x || vehicle.Y is not { } y || !double.IsFinite(x) || !double.IsFinite(y)) continue;
            var p = Project(x, y);
            // Do not clamp bad positions onto the route or silently rescale the map around an outlier.
            if (p.X < 22 || p.X > 878 || p.Y < 22 || p.Y > 498) continue;
            var stale = OverviewVehicle.IsStale(vehicle, now);
            var fill = OverviewVehicle.ColorFor(vehicle, now);
            var angle = vehicle.Angle is { } rad && double.IsFinite(rad) ? -rad * 180 / Math.PI : 0;
            Vehicles.Add(new(vehicle.VehicleId, p.X - 18, p.Y - 18, angle, fill,
                stale ? 0.45 : 1, stale ? $"{vehicle.VehicleId} · 旧位置" : vehicle.VehicleId,
                $"{vehicle.DisplayName}\n{vehicle.MapPositionMessage}\nX {x:F2} / Y {y:F2} m"));
        }
    }
    public Point Project(double x, double y) => new(_offsetX + (x - _minX) * _scale, _offsetY - (y - _minY) * _scale);
}

public sealed record RuntimeAgvMarker(string VehicleId, double Left, double Top, double Rotation,
    string Fill, double Opacity, string Label, string Tooltip);

public sealed record OverviewVehicle(AgvDiagnostic Source, DateTimeOffset GeneratedAt)
{
    public string VehicleId => Source.VehicleId;
    public string Name => Source.DisplayName;
    public string Endpoint => Source.Endpoint;
    public string Connection => Source.Connection switch
    { ConnectionState.Online => "在线", ConnectionState.Connecting => "连接中", ConnectionState.Degraded => "不稳定", _ => "离线" };
    public string Color => ColorFor(Source, GeneratedAt);
    public string Battery => Source.BatteryPercent is { } b ? $"{b:F0}%" : "—";
    public string Station => Source.CurrentStation ?? "—";
    public string Navigation => Source.NavigationState switch
    { "0" => "无任务", "1" => "等待", "2" => "运行中", "3" => "暂停", "4" => "完成", "5" => "失败", "6" => "取消", null => "未返回", var text => text };
    public string Speed => Source.VelocityX is { } x && Source.VelocityY is { } y && double.IsFinite(x) && double.IsFinite(y)
        ? $"{Math.Sqrt(x*x + y*y):F2} m/s" : "—";
    public string AngularSpeed => Value(Source.AngularVelocity, "rad/s");
    public string Charging => Source.Charging is true ? "充电中" : Source.Charging is false ? "未充电" : "未返回";
    public string BatteryDetails => $"{Value(Source.BatteryVoltage, "V")} / {Value(Source.BatteryCurrent, "A")} / {Value(Source.BatteryTemperature, "℃")}";
    private static string Value(double? value, string unit) => value is { } v && double.IsFinite(v) ? $"{v:F1} {unit}" : "—";
    public string Coordinates => Source.X is { } x && Source.Y is { } y && double.IsFinite(x) && double.IsFinite(y)
        ? $"{x:F2}, {y:F2} m" : "未定位";
    public string Heading => Source.Angle is { } a && double.IsFinite(a) ? $"{a * 180 / Math.PI:F1}°" : "—";
    public string Confidence => Source.LocalizationConfidence is { } c && double.IsFinite(c) ? c.ToString("P0", CultureInfo.CurrentCulture) : "—";
    public string Tasks => $"{Source.TaskCount} 条 / 报警 {Source.AlarmCount}";
    public string Safety => $"急停 {Flag(Source.EmergencyStopped)} · 阻挡 {Flag(Source.Blocked)}";
    public string Heartbeat => Source.LastSuccessfulPollAt?.LocalDateTime.ToString("HH:mm:ss") ?? "尚无成功采集";
    public string Quality => IsStale(Source, GeneratedAt) ? "离线 / 过期（历史值）" : Source.Quality.ToString();
    public string Position => Source.MapPositionMessage;
    public string Error => Source.ErrorMessage ?? "—";
    public string Response => Source.LastResponseTimeMs is { } ms ? $"{ms:F0} ms" : "—";
    private static string Flag(bool? value) => value is true ? "是" : value is false ? "否" : "未知";
    public static bool IsStale(AgvDiagnostic v, DateTimeOffset now) => v.Connection != ConnectionState.Online ||
        v.Quality != SignalQuality.Good || v.MapPositionState == AgvMapPositionState.Stale ||
        v.LastSuccessfulPollAt is null || now - v.LastSuccessfulPollAt.Value > TimeSpan.FromSeconds(15);
    public static string ColorFor(AgvDiagnostic v, DateTimeOffset now) => IsStale(v, now) ? "#8192A6" :
        v.EmergencyStopped is true || v.AlarmCount > 0 ? "#EF7272" :
        v.Blocked is true || v.MapPositionState is AgvMapPositionState.LowConfidence or AgvMapPositionState.OffRoute ? "#F2BF60" : "#38D6AA";
}
