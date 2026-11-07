using Lumora.Auth.Core.Profiles;

namespace Lumora.Auth.Tests;

public class MinecraftUuidTests
{
    [Fact]
    public void Unsigned_format_has_no_dashes_and_round_trips()
    {
        var id = Guid.NewGuid();
        var text = MinecraftUuid.ToUnsigned(id);

        Assert.Equal(32, text.Length);
        Assert.DoesNotContain('-', text);
        Assert.True(MinecraftUuid.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
    }

    [Fact]
    public void Dashed_format_is_accepted() =>
        Assert.True(MinecraftUuid.TryParse("069a79f4-44e9-4726-a5be-fca90e38aaf5", out _));
}
