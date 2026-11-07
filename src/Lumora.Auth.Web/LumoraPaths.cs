using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web;

/// <summary>Resolved locations inside <see cref="LumoraOptions.DataPath"/>.</summary>
public sealed class LumoraPaths
{
    public LumoraPaths(IOptions<LumoraOptions> options, IHostEnvironment env)
    {
        Root = Path.GetFullPath(options.Value.DataPath, env.ContentRootPath);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Keys => Path.Combine(Root, "keys");

    public string SigningKeyFile => Path.Combine(Keys, "yggdrasil-signing.pem");

    public string DataProtectionKeys => Path.Combine(Keys, "data-protection");

    public string Textures => Path.Combine(Root, "textures");

    public string TextureFile(string hash) => Path.Combine(Textures, hash + ".png");

    /// <summary>Build files, content-addressed by SHA-1: files/ab/abcdef….</summary>
    public string Files => Path.Combine(Root, "files");

    public string BuildFile(string sha1) => Path.Combine(Files, sha1[..2], sha1);

    public string Temp => Path.Combine(Root, "tmp");

    public string AuthlibInjector => Path.Combine(Root, "authlib-injector");
}
