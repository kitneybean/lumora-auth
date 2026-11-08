using System.Security.Claims;
using System.Text.Encodings.Web;
using Lumora.Auth.Data;
using Lumora.Auth.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Lumora.Auth.Web.Agents;

/// <summary>"Authorization: Bearer lma_..." of a registered, not revoked agent.</summary>
public sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDbContextFactory<LumoraDbContext> dbFactory,
    AutoBanService autoBan)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AgentKey";
    public const string Role = "Agent";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var key = header[7..].Trim();
        if (!key.StartsWith(AgentKeys.KeyPrefix, StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        var hash = AgentKeys.Hash(key);
        await using var db = await dbFactory.CreateDbContextAsync(Context.RequestAborted);
        var agent = await db.Agents.AsNoTracking()
            .Where(a => a.KeyHash == hash && !a.IsRevoked)
            .Select(a => new { a.Id, a.Name })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (agent is null)
        {
            autoBan.Record(ClientIp.Of(Context), AutoBanService.Offence.FailedLogin);
            return AuthenticateResult.Fail("Unknown or revoked agent key");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, agent.Id.ToString()),
            new Claim(ClaimTypes.Name, agent.Name),
            new Claim(ClaimTypes.Role, Role),
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    public static Guid AgentIdOf(ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("Not an agent"));
}
