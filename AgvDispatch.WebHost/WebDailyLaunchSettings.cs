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
}
