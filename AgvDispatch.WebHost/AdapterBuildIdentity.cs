using System.Reflection;
using System.Security.Cryptography;

namespace AgvDispatch.WebHost;

public static class AdapterBuildIdentity
{
    public static readonly string InformationalVersion = typeof(AdapterBuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static readonly string Sha256 = ReadHash();
    private static string ReadHash()
    {
        try { using var stream=File.OpenRead(typeof(AdapterBuildIdentity).Assembly.Location);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        catch(IOException){return "unavailable";}
        catch(UnauthorizedAccessException){return "unavailable";}
    }
}
