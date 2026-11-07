namespace Lumora.Auth.Tests.Infrastructure;

/// <summary>Integration test classes share one site and one PostgreSQL container.</summary>
[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<LumoraAppFactory>
{
    public const string Name = "app";
}
