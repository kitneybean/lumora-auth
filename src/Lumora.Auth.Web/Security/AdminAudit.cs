using Microsoft.AspNetCore.Components.Authorization;

namespace Lumora.Auth.Web.Security;

/// <summary>Journal entries for admin actions made in interactive (Blazor Server) pages, attributed to the signed-in admin.</summary>
public sealed class AdminAudit(AuthenticationStateProvider auth, AuditService audit)
{
    public async Task LogAsync(string action, string? details = null, bool success = true)
    {
        var state = await auth.GetAuthenticationStateAsync();
        audit.Log(state.User, "admin." + action, success, details);
    }
}
