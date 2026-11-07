using Lumora.Auth.Core.Builds;
using Lumora.Auth.Web.Launcher;

namespace Lumora.Auth.Tests;

public class BuildPathTests
{
    [Theory]
    [InlineData("mods/jei.jar", "mods/jei.jar")]
    [InlineData(@"config\create\client.toml", "config/create/client.toml")]
    [InlineData("./mods/a.jar", "mods/a.jar")]
    [InlineData("/options.txt", "options.txt")]
    public void Normalizes_safe_paths(string input, string expected) => Assert.Equal(expected, BuildPath.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("../x.jar")]
    [InlineData("mods/../../x.jar")]
    [InlineData("C:/Windows/x.dll")]
    [InlineData("mods//a.jar")]
    [InlineData("saves/world/level.dat")]
    [InlineData("servers.dat")]
    [InlineData("mods/a?.jar")]
    public void Rejects_unsafe_or_reserved_paths(string input) => Assert.Null(BuildPath.Normalize(input));

    [Fact]
    public void Combine_uses_only_the_file_name() =>
        Assert.Equal("mods/x.jar", BuildPath.Combine("mods", @"C:\Users\me\Downloads\x.jar"));

    [Theory]
    [InlineData("1.21.1", "21.1.")]
    [InlineData("1.21", "21.0.")]
    [InlineData("1.20.4", "20.4.")]
    [InlineData("24w14a", null)]
    public void NeoForge_prefix_follows_minecraft_version(string mc, string? prefix) =>
        Assert.Equal(prefix, GameVersionsService.NeoForgePrefix(mc));
}
