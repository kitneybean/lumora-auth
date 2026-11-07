using System.Security.Cryptography;
using System.Text;

namespace Lumora.Auth.Core.Yggdrasil;

/// <summary>
/// RSA key that signs profile properties (SHA1withRSA, as required by Minecraft and authlib-injector).
/// The public key is published in the Yggdrasil metadata as <c>signaturePublickey</c>.
/// </summary>
public sealed class SigningKey : IDisposable
{
    public const int KeySize = 4096;

    private readonly RSA _rsa;
    private readonly Lock _lock = new();

    private SigningKey(RSA rsa)
    {
        _rsa = rsa;
        PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem() + "\n";
    }

    /// <summary>PEM "PUBLIC KEY" (SubjectPublicKeyInfo).</summary>
    public string PublicKeyPem { get; }

    public static SigningKey Generate(int keySize = KeySize) => new(RSA.Create(keySize));

    public static SigningKey FromPem(string privateKeyPem)
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return new SigningKey(rsa);
    }

    /// <summary>Loads the PKCS#8 PEM key from <paramref name="path"/>, creating it on first use.</summary>
    public static SigningKey LoadOrCreate(string path, int keySize = KeySize)
    {
        if (File.Exists(path))
            return FromPem(File.ReadAllText(path));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var key = Generate(keySize);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, key.ExportPrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: false);
        return key;
    }

    public string ExportPrivateKeyPem()
    {
        lock (_lock)
            return _rsa.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>Base64 SHA1withRSA signature of the UTF-8 bytes of <paramref name="value"/>.</summary>
    public string Sign(string value)
    {
        var data = Encoding.UTF8.GetBytes(value);
        lock (_lock)
            return Convert.ToBase64String(_rsa.SignData(data, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
    }

    /// <summary>Verifies a signature against a public key PEM (used in tests and diagnostics).</summary>
    public static bool Verify(string publicKeyPem, string value, string signature)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return rsa.VerifyData(Encoding.UTF8.GetBytes(value), Convert.FromBase64String(signature),
            HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
    }

    public void Dispose() => _rsa.Dispose();
}
