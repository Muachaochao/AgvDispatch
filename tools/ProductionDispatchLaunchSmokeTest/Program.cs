using AgvDispatch.WebHost;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (Exception error) when (error is ArgumentException or InvalidDataException or System.Text.Json.JsonException or InvalidOperationException or IOException)
    { checks++; return; }
    throw new InvalidOperationException(message);
}
string Value(WebLocalLaunchMode mode, string name) => mode.ServiceArguments("private-test-pipe")
    .Single(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal)).Split('=', 2)[1];
bool Enabled(WebLocalLaunchMode mode, string name) => bool.Parse(Value(mode, name));

foreach (var capacity in new[] { 1, 2, 7, 300, 1000 })
{
    var production = WebLocalLaunchMode.Parse(["--production-dispatch", "--production-max-concurrent", capacity.ToString()]);
    Check(production.ProductionDispatch && production.IsBusiness && production.Page == "/#/tasks", "Production uses ordinary task dispatch.");
    Check(production.ProductionMaxConcurrentTasks == capacity && Value(production, "Dispatching:MaxConcurrentTasks") == capacity.ToString(), "Concurrency comes from configuration, not a fixed fleet size.");
    foreach (var permission in new[] { "Plc:AllowWrites", "Agv:AllowCommands", "TaskCreation:Enabled",
        "TaskCreation:AutomaticCreationEnabled", "Dispatching:Enabled", "Dispatching:RequireGlobalMatching", "AgvCommandOutbox:Enabled",
        "DispatchTaskSteps:Enabled", "HandshakeExecution:Enabled" })
        Check(Enabled(production, permission), "Production enables " + permission);
    foreach (var isolated in new[] { "FleetTest:Enabled", "NavigationTest:Enabled", "NavigationTest:ForkEnabled",
        "NavigationTest:AutoDetourEnabled", "PlcCycleTest:Enabled" })
        Check(!Enabled(production, isolated), "Production does not inherit isolated-test permission " + isolated);
    Check(production.NavigationVehicle == "" && production.BusinessVehicle == "" && production.FleetVehicles.Length == 0,
        "Production leaves vehicle selection to configured pools and the optimizer.");
}
var monitor = WebLocalLaunchMode.Parse([]);
foreach (var permission in new[] { "Plc:AllowWrites", "Agv:AllowCommands", "TaskCreation:Enabled",
    "TaskCreation:AutomaticCreationEnabled", "Dispatching:Enabled", "AgvCommandOutbox:Enabled" })
    Check(!Enabled(monitor, permission), "A fresh unconfigured daily entry remains passive: " + permission);
foreach (var capacity in new[] { "0", "-1", "1001", "1.5", "text" })
    Reject(() => WebLocalLaunchMode.Parse(["--production-dispatch", "--production-max-concurrent", capacity]), "Invalid production concurrency was accepted.");
Reject(() => WebLocalLaunchMode.Parse(["--production-dispatch"]), "Unspecified production concurrency was accepted.");
Reject(() => WebLocalLaunchMode.Parse(["--production-max-concurrent", "2"]), "A concurrency limit enabled production implicitly.");
Reject(() => WebLocalLaunchMode.Parse(["--production-dispatch", "--production-dispatch", "--production-max-concurrent", "2"]), "Repeated production selection was accepted.");
Reject(() => WebLocalLaunchMode.Parse(["--production-dispatch", "--production-max-concurrent", "2", "--production-max-concurrent", "3"]), "Repeated concurrency was accepted.");
string[][] tests = [["--navigation-test-vehicle", "CART-A"], ["--forklift-test-vehicle", "FORK-A"],
    ["--business-run-vehicle", "CART-B"], ["--fleet-test-vehicles", "CART-A,CART-B"],
    ["--plc-cycle-test-vehicle", "CART-A", "--plc-cycle-test-station", "CELL-A"]];
foreach (var test in tests)
{
    Reject(() => WebLocalLaunchMode.Parse(["--production-dispatch", "--production-max-concurrent", "2", ..test]), "Production combined with a test mode.");
    Check(!Enabled(WebLocalLaunchMode.Parse(test), "TaskCreation:AutomaticCreationEnabled"), "Existing test mode retains one call coordinator.");
    Check(!Enabled(WebLocalLaunchMode.Parse(test), "Dispatching:RequireGlobalMatching"), "Isolated tests do not inherit production matching requirements.");
}

var folder = Directory.CreateTempSubdirectory("agv-production-launch-").FullName;
var settingsPath = Path.Combine(folder, "daily.json");
string[] daily = ["--daily-data", "--daily-launch-settings", settingsPath];
try
{
    void Save(string json) => File.WriteAllText(settingsPath, json);
    WebLocalLaunchMode Daily(params string[] extra) => WebLocalLaunchMode.Parse(WebDailyLaunchSettings.Apply([..daily, ..extra]));
    Save("{\"directory\":\"test\"}");
    Check(!Daily().ProductionDispatch, "Older daily pointers do not grant production permission.");
    Save("{\"productionDispatch\":false,\"maxConcurrentTasks\":7}");
    Check(!Daily().ProductionDispatch, "A concurrency field alone never grants production permission.");
    Save("{\"productionDispatch\":true,\"maxConcurrentTasks\":7}");
    Check(Daily().ProductionDispatch && Daily().ProductionMaxConcurrentTasks == 7, "Explicitly saved production policy survives daily restarts.");
    var replacedDefaults = Daily("--default-navigation-test-vehicle", "CART-A", "--default-auto-navigation-detour");
    Check(replacedDefaults.ProductionDispatch && !replacedDefaults.AutoNavigationDetour && replacedDefaults.NavigationVehicle == "",
        "Saved production policy never combines old navigation defaults.");
    foreach (var test in tests)
        Check(!Daily(test).ProductionDispatch && !Enabled(Daily(test), "TaskCreation:AutomaticCreationEnabled"), "Explicit isolated selection replaces the daily production policy.");
    foreach (var invalid in new[] { "[]", "{", "{\"productionDispatch\":\"true\",\"maxConcurrentTasks\":2}",
        "{\"productionDispatch\":true}", "{\"productionDispatch\":true,\"maxConcurrentTasks\":0}",
        "{\"productionDispatch\":true,\"maxConcurrentTasks\":1001}", "{\"productionDispatch\":true,\"maxConcurrentTasks\":1.5}" })
    {
        Save(invalid);
        Reject(() => Daily(), "Invalid saved authorization was accepted.");
    }
    File.Delete(settingsPath);
    Reject(() => Daily(), "Missing configured daily settings file was ignored.");
    Check(!Daily("--stop").ProductionDispatch, "Stopping owned processes remains possible when daily settings are missing.");
    Check(!Daily("--stop", "--production-dispatch").ProductionDispatch, "Stop parsing grants no mode permission and ignores incomplete launch-only flags.");
    Reject(() => WebDailyLaunchSettings.Apply(["--daily-launch-settings", settingsPath]), "Daily settings applied to an independent bundle.");
    Reject(() => WebDailyLaunchSettings.Apply([..daily, "--daily-launch-settings", settingsPath]), "Repeated settings files were accepted.");
}
finally
{
    if (File.Exists(settingsPath)) File.Delete(settingsPath);
    Directory.Delete(folder);
}
Console.WriteLine($"PASS: {checks} production launch checks. No service, vehicle or PLC was started.");
