using System.IO.Compression;
using System.Text;
using Lumora.Auth.Core.Builds;

namespace Lumora.Auth.Tests;

public class ModCheckTests
{
    private const string CreateToml = """
        modLoader="javafml" # comment
        loaderVersion="[4,)"
        license="MIT"

        [[mods]]
        modId="create"
        version="6.0.4"
        displayName="Create"
        description='''
        Multi-line text with [[mods]] inside must be ignored
        '''

        [[dependencies.create]]
            modId="neoforge"
            type="required"
            versionRange="[21.1.0,)"
        [[dependencies.create]]
            modId="flywheel"
            type="required"
            versionRange="[1.0,)"
        [[dependencies.create]]
            modId="jei"
            type="optional"
        """;

    public static Stream Jar(string? toml = null, params (string Name, Stream Content)[] nested)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (toml is not null)
                using (var w = new StreamWriter(zip.CreateEntry("META-INF/neoforge.mods.toml").Open(), Encoding.UTF8))
                    w.Write(toml);
            foreach (var (name, content) in nested)
                using (var e = zip.CreateEntry("META-INF/jarjar/" + name).Open())
                    content.CopyTo(e);
        }
        ms.Position = 0;
        return ms;
    }

    private static string Simple(string id) => $"[[mods]]\nmodId=\"{id}\"\nversion=\"1.0\"\n";

    [Fact]
    public void Reads_mod_id_and_required_dependencies()
    {
        var info = ModMetadata.Read(Jar(CreateToml));

        var mod = Assert.Single(info.Mods);
        Assert.Equal(("create", "6.0.4", "Create"), (mod.ModId, mod.Version, mod.Name));
        Assert.Equal(["neoforge", "flywheel"], mod.Dependencies.Where(d => d.Required).Select(d => d.ModId).ToArray());
    }

    [Fact]
    public void Reports_missing_dependency_unless_bundled_or_present()
    {
        var create = ModMetadata.Read(Jar(CreateToml));

        var alone = ModCheck.Analyze([("mods/create.jar", create)]);
        Assert.Contains(alone, p => p.Kind == "missing" && p.Message.Contains("flywheel"));
        Assert.DoesNotContain(alone, p => p.Message.Contains("neoforge") || p.Message.Contains("«jei»"));

        var withFlywheel = ModCheck.Analyze([("mods/create.jar", create), ("mods/flywheel.jar", ModMetadata.Read(Jar(Simple("flywheel"))))]);
        Assert.Empty(withFlywheel);

        var bundled = ModMetadata.Read(Jar(CreateToml, ("flywheel.jar", Jar(Simple("flywheel")))));
        Assert.Contains("flywheel", bundled.BundledModIds);
        Assert.Empty(ModCheck.Analyze([("mods/create.jar", bundled)]));
    }

    [Fact]
    public void Reports_duplicates()
    {
        var problems = ModCheck.Analyze(
        [
            ("mods/jei-19.21.jar", ModMetadata.Read(Jar(Simple("jei")))),
            ("mods/jei-19.22.jar", ModMetadata.Read(Jar(Simple("jei")))),
        ]);

        var dup = Assert.Single(problems);
        Assert.Equal("duplicate", dup.Kind);
        Assert.Contains("jei-19.21.jar", dup.Message);
    }

    [Fact]
    public void Jar_without_metadata_is_ignored() => Assert.Empty(ModMetadata.Read(Jar()).Mods);

    [Theory]
    [InlineData(new[] { "mods/a.jar", "config/b.toml" }, "")]
    [InlineData(new[] { ".minecraft/mods/a.jar", "instance.cfg", "mmc-pack.json" }, ".minecraft/")]
    [InlineData(new[] { "overrides/config/b.toml", "manifest.json" }, "overrides/")]
    [InlineData(new[] { "MyPack/mods/a.jar", "MyPack/config/b.toml" }, "MyPack/")]
    [InlineData(new[] { "MyPack/minecraft/mods/a.jar", "MyPack/instance.cfg" }, "MyPack/minecraft/")]
    [InlineData(new[] { "readme.txt", "options.txt" }, "")]
    public void Detects_game_folder_inside_zip(string[] entries, string prefix) =>
        Assert.Equal(prefix, ZipLayout.DetectPrefix(entries));
}
