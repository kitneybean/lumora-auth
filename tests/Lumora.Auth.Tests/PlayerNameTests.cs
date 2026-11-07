using Lumora.Auth.Core.Profiles;

namespace Lumora.Auth.Tests;

public class PlayerNameTests
{
    [Theory]
    [InlineData("Steve")]
    [InlineData("abc")]
    [InlineData("Player_123")]
    [InlineData("ABCDEFGHIJKLMNOP")]
    public void Accepts_valid_names(string name) => Assert.True(PlayerName.IsValid(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("ABCDEFGHIJKLMNOPQ")]
    [InlineData("with space")]
    [InlineData("Игрок")]
    [InlineData("dash-name")]
    public void Rejects_invalid_names(string? name) => Assert.False(PlayerName.IsValid(name));
}
