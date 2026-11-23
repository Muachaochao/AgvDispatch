using AgvDispatch.Service;

// D16/E9: dispatch configuration errors that used to surface inside the first dispatch cycle are rejected at startup.
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }
DispatchOrchestratorOptions Valid(Action<DispatchOrchestratorOptions>? change = null)
{
    var options = new DispatchOrchestratorOptions
    {
        Enabled = true, MaxConcurrentTasks = 1, DumperWaitingSite = "B", DumperDumpingSite = "C",
        StationPickupSites = new(StringComparer.OrdinalIgnoreCase) { ["ST-1"] = "A" },
        StationVehiclePools = new(StringComparer.OrdinalIgnoreCase) { ["ST-1"] = ["V1"] },
    };
    change?.Invoke(options);
    return options;
}
string[] Fleet = ["V1", "V2"];

Check(new DispatchOrchestratorOptions { Enabled = false }.GetStartupError([]) is null,
    "a disabled dispatcher needs no fleet or sites at startup");
Check(Valid().GetStartupError(Fleet) is null, "a complete configuration passes startup validation");
Check(Valid(o => { o.StationPickupSites = new(StringComparer.OrdinalIgnoreCase); }).GetStartupError(Fleet) is { } noPickup &&
      noPickup.Contains("StationPickupSites"), "an enabled dispatcher without pickup sites is rejected at startup");
Check(Valid(o => { o.StationPickupSites = new(StringComparer.OrdinalIgnoreCase) { ["ST-1"] = " " }; }).GetStartupError(Fleet) is not null,
    "a blank pickup site is rejected at startup");
Check(Valid(o => { o.StationPickupSites = new(StringComparer.OrdinalIgnoreCase) { ["ST-1"] = "C" }; }).GetStartupError(Fleet) is { } conflict &&
      conflict.Contains("冲突"), "a pickup site that is the dumper site is rejected at startup");
Check(Valid(o => { o.StationVehiclePools = new(StringComparer.OrdinalIgnoreCase) { ["ST-1"] = ["V9"] }; }).GetStartupError(Fleet) is { } unknown &&
      unknown.Contains("V9"), "a vehicle pool that names an unconfigured AGV is rejected at startup");
Check(Valid(o => { o.MaxConcurrentTasks = 3; }).GetStartupError(Fleet) is { } concurrency && concurrency.Contains("MaxConcurrentTasks"),
    "more concurrent tasks than configured vehicles is rejected at startup");
Check(Valid().GetStartupError([]) is not null, "an enabled dispatcher without any configured vehicle is rejected at startup");
Check(Valid(o => { o.DumperWaitingSite = ""; }).GetStartupError(Fleet) is not null,
    "the runtime configuration check still applies at startup");

Console.WriteLine($"PASS: {checks} startup configuration checks; no hardware or database used.");
