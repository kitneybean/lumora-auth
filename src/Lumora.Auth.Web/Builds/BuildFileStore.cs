using System.Security.Cryptography;

namespace Lumora.Auth.Web.Builds;

/// <summary>Content-addressed storage of build files (mods, configs) by SHA-1.</summary>
public sealed class BuildFileStore(LumoraPaths paths)
{
    public static bool IsSha1(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public string PathOf(string sha1) => paths.BuildFile(sha1);

    public bool Exists(string sha1) => IsSha1(sha1) && File.Exists(PathOf(sha1));

    /// <summary>Copies the stream into the store and returns its SHA-1 and size.</summary>
    public async Task<(string Sha1, long Size)> SaveAsync(Stream source, CancellationToken ct = default)
    {
        Directory.CreateDirectory(paths.Temp);
        var tmp = Path.Combine(paths.Temp, Guid.NewGuid().ToString("N"));
        string sha1;
        long size;
        try
        {
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
            await using (var target = File.Create(tmp))
            {
                var buffer = new byte[81920];
                int read;
                size = 0;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                    size += read;
                }
                sha1 = Convert.ToHexStringLower(hash.GetHashAndReset());
            }

            var final = PathOf(sha1);
            if (File.Exists(final))
                return (sha1, size);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try
            {
                File.Move(tmp, final);
            }
            catch (IOException) when (File.Exists(final))
            {
                // Same content stored concurrently.
            }
            return (sha1, size);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
