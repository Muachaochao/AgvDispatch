using System.Globalization;
using System.Text.Json;

namespace AgvDispatch.WebHost;

// Only an explicitly saved production policy is inherited. Navigation and fork
// permissions are never recovered from this file or from an earlier process.
internal static class WebDailyLaunchSettings
{
    public static string[] Apply(string[] args)
    {
        // The caller uses this result only for mode parsing; its original args
        // still select the owned-process stop branch. Broken daily settings must
        // never prevent stopping the exact processes recorded by the launcher.
        if (args.Contains("--stop")) return [];
        var settingIndex = Array.IndexOf(args, "--daily-launch-settings");
        if (settingIndex < 0) return args;
        if (!args.Contains("--daily-data") || args.Count(a => a == "--daily-launch-settings") != 1 ||
            settingIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[settingIndex + 1]) ||
            args[settingIndex + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Daily launch settings require --daily-data and one settings file.");

        // Explicit selections replace the daily default; they never combine the
        // permissions of production and an isolated test.
        string[] explicitModes = ["--production-dispatch", "--navigation-test-vehicle", "--forklift-test-vehicle",
            "--business-run-vehicle", "--fleet-test-vehicles", "--plc-cycle-test-vehicle", "--plc-cycle-test-station"];
        if (explicitModes.Any(args.Contains)) return args;

        using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(args[settingIndex + 1])));
        var settings = document.RootElement;
        if (settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Daily launch settings must be a JSON object.");
        if (!settings.TryGetProperty("productionDispatch", out var enabled)) return args;
        if (enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("productionDispatch must be a boolean.");
        if (!enabled.GetBoolean()) return args;
        if (!settings.TryGetProperty("maxConcurrentTasks", out var limit) ||
            !limit.TryGetInt32(out var maximum) || maximum is < 1 or > 1000)
            throw new InvalidDataException("Production dispatch requires maxConcurrentTasks between 1 and 1000.");
        return [..args, "--production-dispatch", "--production-max-concurrent", maximum.ToString(CultureInfo.InvariantCulture)];
    }

    public static string[] SimulationArguments(string[] args)
    {
        if (args.Contains("--stop")) return [];
        var settingIndex = Array.IndexOf(args, "--daily-launch-settings");
        if (settingIndex < 0) return [];
        if (settingIndex + 1 >= args.Length || string.IsNullOrWhiteSpace(args[settingIndex + 1]) ||
            args[settingIndex + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException("Daily launch settings require one settings file.");

        using var document = JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(args[settingIndex + 1])));
        var settings = document.RootElement;
        if (settings.ValueKind != JsonValueKind.Object ||
            !settings.TryGetProperty("simulation", out var simulation) ||
            simulation.ValueKind == JsonValueKind.Null)
            return [];
        if (simulation.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("simulation must be a JSON object.");

        HashSet<string> known = ["enabled", "autoDiscoverLatest", "artifactsPath", "root", "pythonPath", "controllerPath", "port"];
        if (simulation.EnumerateObject().Any(property => !known.Contains(property.Name)))
            throw new InvalidDataException("simulation contains an unknown setting.");
        var enabled = simulation.TryGetProperty("enabled", out var enabledValue) && enabledValue.ValueKind == JsonValueKind.True;
        if (!enabled)
        {
            if (simulation.TryGetProperty("enabled", out var disabledValue) && disabledValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("simulation.enabled must be a boolean.");
            return [];
        }

        string RequiredPath(string name)
        {
            var value = simulation.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new InvalidDataException($"simulation.{name} must be an absolute path.");
            return Path.GetFullPath(value);
        }
        var autoDiscover = simulation.TryGetProperty("autoDiscoverLatest", out var autoValue) && autoValue.ValueKind == JsonValueKind.True;
        if (simulation.TryGetProperty("autoDiscoverLatest", out var autoCheck) && autoCheck.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("simulation.autoDiscoverLatest must be a boolean.");
        var python = RequiredPath("pythonPath");
        var controller = RequiredPath("controllerPath");
        var portValue = simulation.TryGetProperty("port", out var portElement) ? portElement : default;
        if (portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out var port) || port is < 1024 or > 65535)
            throw new InvalidDataException("simulation.port must be between 1024 and 65535.");

        var result = new List<string>
        {
            "--Web:Simulation:Enabled=true",
            $"--Web:Simulation:PythonPath={python}",
            $"--Web:Simulation:ControllerPath={controller}",
            $"--Web:Simulation:Port={port}"
        };
        if (autoDiscover)
        {
            result.Add("--Web:Simulation:AutoDiscoverLatest=true");
            result.Add($"--Web:Simulation:ArtifactsPath={RequiredPath("artifactsPath")}");
            if (simulation.TryGetProperty("root", out var root) && root.ValueKind != JsonValueKind.Null)
                result.Add($"--Web:Simulation:Root={RequiredPath("root")}");
        }
        else
        {
            result.Add($"--Web:Simulation:Root={RequiredPath("root")}");
        }
        return [.. result];
    }
}
