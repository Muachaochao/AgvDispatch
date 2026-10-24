using System.Reflection;
using System.Security.Cryptography;

namespace AgvDispatch.WebHost;

public static class AdapterBuildIdentity
{
    // .NET can append +SourceRevisionId. Build metadata does not change the batch identity.
    public static bool MatchesRelease(string? actual,string expected)=>actual==expected||
        (actual is not null&&actual.StartsWith(expected+"+",StringComparison.Ordinal)&&
         actual.Length==expected.Length+41&&actual[(expected.Length+1)..].All(Uri.IsHexDigit));
    public static readonly string InformationalVersion = typeof(AdapterBuildIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static readonly string Sha256 = ReadHash();
    private static string ReadHash()
    {
        try { using var stream=File.OpenRead(typeof(AdapterBuildIdentity).Assembly.Location);return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
        catch(IOException){return "unavailable";}
        catch(UnauthorizedAccessException){return "unavailable";}
    }
}
