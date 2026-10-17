using System.Reflection;
using System.Security.Cryptography;
using AgvDispatch.Application.Abstractions;

namespace AgvDispatch.Service;

public static class BackendBuildIdentity
{
    private static readonly Assembly Assembly = typeof(BackendBuildIdentity).Assembly;
    private static readonly string BuildId = ReadHash();
    private static string ReadHash()
    {
        try { using var stream = File.OpenRead(Assembly.Location); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        catch (IOException) { return "unavailable"; }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }
    public static BackendExecutionDiagnostic Read(HandshakeExecutionOptions? handshake, DispatchTaskCreationOptions? creation,
        DispatchTaskStepOptions? steps, AgvCommandOutboxOptions? outbox) => new(BuildId,
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        handshake?.Enabled == true ? "PersistentHandshake" : "OriginalExecutor",
        handshake?.Enabled == true, handshake?.PlcContractVersion ?? "", HandshakeExecutionOptions.Version,
        creation?.Enabled == true, steps?.Enabled == true, outbox?.Enabled == true);
}
