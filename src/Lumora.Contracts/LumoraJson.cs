using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lumora.Contracts;

/// <summary>Shared JSON settings for the launcher API.</summary>
public static class LumoraJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
