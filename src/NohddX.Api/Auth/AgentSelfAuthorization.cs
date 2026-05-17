using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace NohddX.Api.Auth;

/// <summary>
/// Helper for agent-scoped endpoints. An authenticated agent must only be
/// able to act on its own resource — i.e. POST /api/agents/{id}/status with
/// id == its own GUID. Admins bypass the check.
/// </summary>
public static class AgentSelfAuthorization
{
    public static bool CallerIsSelfOrAdmin(HttpContext ctx, Guid resourceAgentId)
    {
        if (ctx.User.IsInRole(NohddxAuthSchemes.AdminRole))
            return true;

        if (!ctx.User.IsInRole(NohddxAuthSchemes.AgentRole))
            return false;

        var idStr = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(idStr, out var callerId) && callerId == resourceAgentId;
    }
}
