using NLog;
using NLog.Config;
using NLog.Filters;
using NLog.Targets;
using NLog.Targets.Wrappers;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var output = Path.Combine(Path.GetTempPath(), "AgvDispatch-LoggingSmokeTest", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);

foreach (var project in new[] { "AgvDispatch.Service", "AgvDispatch.Wpf" })
{
    using var factory = new LogFactory { ThrowConfigExceptions = true };
    var config = new XmlLoggingConfiguration(Path.Combine(root, project, "NLog.config"), factory);
    var target = config.AllTargets.Select(item => item is AsyncTargetWrapper wrapper
        ? wrapper.WrappedTarget : item).OfType<FileTarget>().Single();
    var expectedSize = project.EndsWith("Service") ? 10485760 : 5242880;
    Require(target.ArchiveAboveSize == expectedSize, "Size limit mismatch");
    Require(target.MaxArchiveFiles == 10 && target.MaxArchiveDays == 7, "Retention mismatch");
    var path = Path.Combine(output, project + ".log");
    target.FileName = path;
    target.KeepFileOpen = false;
    var filter = config.LoggingRules.SelectMany(rule => rule.Filters).OfType<WhenRepeatedFilter>().Single();
    filter.TimeoutSeconds = 1;
    factory.Configuration = config;
    var logger = factory.GetLogger("SmokeTest");
    for (var i = 0; i < 100; i++)
    {
        logger.Debug("snapshot polling");
        logger.Warn("vehicle A disconnected");
    }
    logger.Warn("vehicle B disconnected");
    logger.Info("task created");
    factory.Flush();
    var lines = File.ReadAllLines(path);
    Require(lines.Length == 3, $"Expected 3 useful lines, got {lines.Length}");
    Require(!lines.Any(line => line.Contains("snapshot polling")), "Debug polling leaked");
    Thread.Sleep(1200);
    logger.Warn("vehicle A disconnected");
    factory.Flush();
    Require(File.ReadAllLines(path).Length == 4, "Persistent failure must be logged again after timeout");

    // Exercise size rotation using the same target, with small test-only limits.
    target.ArchiveAboveSize = 1024;
    target.MaxArchiveFiles = 3;
    factory.ReconfigExistingLoggers();
    for (var i = 0; i < 100; i++)
    {
        logger.Info("rotation {0}: {1}", i, new string('x', 500));
        factory.Flush();
    }
    factory.Flush();
    factory.Shutdown();
    var files = Directory.GetFiles(output, project + "*.log");
    Require(files.Length <= 4, $"Archive count unbounded: {files.Length}");
    Require(files.All(file => new FileInfo(file).Length < 2048), "Size rotation failed");
    Console.WriteLine($"PASS {project}: config, level filtering, duplicate suppression, timeout and bounded rotation");
}
Console.WriteLine($"Test logs: {output}");

static void Require(bool condition, string message)
{
    if (!condition)
    {
        Console.Error.WriteLine(message);
        Environment.Exit(1);
    }
}
