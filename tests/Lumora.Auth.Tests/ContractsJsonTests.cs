using System.Text.Json;
using Lumora.Contracts;

namespace Lumora.Auth.Tests;

public class ContractsJsonTests
{
    [Fact]
    public void Enums_are_serialized_as_camel_case_strings()
    {
        var file = new BuildFileDto("mods/a.jar", "abc", 1, "https://x/files/abc", FileInstallMode.OptionalOn, null);

        var json = JsonSerializer.Serialize(file, LumoraJson.Options);

        Assert.Contains("\"mode\":\"optionalOn\"", json);
        Assert.Equal(file, JsonSerializer.Deserialize<BuildFileDto>(json, LumoraJson.Options));
    }
}
