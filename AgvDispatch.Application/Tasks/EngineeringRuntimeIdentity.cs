using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgvDispatch.Domain.Maps;

namespace AgvDispatch.Application.Abstractions;

public static class EngineeringRuntimeIdentity
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    public static string MapHash(MapDefinition map) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(map, Json)));
}
