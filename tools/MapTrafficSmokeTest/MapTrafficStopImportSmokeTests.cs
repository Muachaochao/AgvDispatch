using System.Text.Json;
using AgvDispatch.Application.Abstractions;
using AgvDispatch.Infrastructure.Maps;

internal static class MapTrafficStopImportSmokeTests
{
    public static void Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "agv-map-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var importer = new MapImportService();
            var json = Path.Combine(folder, "map.json");
            File.WriteAllText(json, """
                {"nodes":[
                  {"id":"A","allowTrafficStop":false,"allowTransitThrough":true},
                  {"id":"B","allowTrafficStop":true,"allowDumperWaiting":false},
                  {"id":"C","allowTrafficStop":null},
                  {"id":"D"}],"edges":[],"resources":[]}
                """);
            var map = importer.Import(new(json));
            Check(map.Nodes[0].AllowTrafficStop == false && map.Nodes[0].AllowTransitThrough, "JSON false/transit");
            Check(map.Nodes[1].AllowTrafficStop == true && map.Nodes[1].AllowDumperWaiting == false, "JSON traffic-only stop");
            Check(map.Nodes.Skip(2).All(n => n.AllowTrafficStop is null && !n.AllowTransitThrough), "JSON legacy/null");
            File.WriteAllText(json, JsonSerializer.Serialize(map, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var reimported = importer.Import(new(json));
            Check(reimported.Nodes[0].AllowTrafficStop == false && reimported.Nodes[0].AllowDumperWaiting is null &&
                reimported.Nodes[1].AllowDumperWaiting == false, "JSON roundtrip");
            File.WriteAllText(Path.Combine(folder, "nodes.csv"), "Id,X,Y,AllowTrafficStop,AllowTransitThrough,AllowDumperWaiting\nA,0,0,false,true,\nB,1,0,true,false,false\nC,2,0,,,\n");
            File.WriteAllText(Path.Combine(folder, "edges.csv"), "Id,From,To\n");
            File.WriteAllText(Path.Combine(folder, "resources.csv"), "Id\n");
            map = importer.Import(new(folder));
            Check(map.Nodes[0].AllowTrafficStop == false && map.Nodes[0].AllowTransitThrough, "CSV false/transit");
            Check(map.Nodes[1].AllowTrafficStop == true && map.Nodes[1].AllowDumperWaiting == false &&
                map.Nodes[2].AllowTrafficStop is null && map.Nodes[2].AllowDumperWaiting is null, "CSV traffic-only stop and legacy/null");
            File.WriteAllText(Path.Combine(folder, "nodes.csv"), "Id,X,Y\nA,0,0\n");
            Check(importer.Import(new(folder)).Nodes[0].AllowTrafficStop is null, "CSV legacy");
            Console.WriteLine("PASS: map import preserves stop/transit permissions and legacy defaults (7 checks).");
        }
        finally
        {
            var absolute = Path.GetFullPath(folder);
            if (!string.Equals(Path.GetDirectoryName(absolute)?.TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(absolute).StartsWith("agv-map-stop-", StringComparison.Ordinal))
                throw new IOException("Unexpected temporary cleanup target.");
            Directory.Delete(absolute, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Map import regression: " + message);
    }
}
