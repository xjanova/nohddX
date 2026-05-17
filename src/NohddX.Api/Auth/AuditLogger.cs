using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Auth;

/// <summary>
/// Convenience facade that controllers / middleware call to record an audit
/// event. Pulls actor / actor-id / remote-ip from the current HTTP context.
/// Failures to persist are logged but never surface to the caller — auditing
/// is best-effort and must not break the actual request.
/// </summary>
public class AuditLogger
{
    private readonly IHttpContextAccessor _http;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SecurityOptions _options;
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(
        IHttpContextAccessor http,
        IServiceScopeFactory scopeFactory,
        IOptions<SecurityOptions> options,
        ILogger<AuditLogger> logger)
    {
        _http = http;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task RecordAsync(
        string action,
        bool success,
        string? targetType = null,
        string? targetId = null,
        string? detail = null,
        CancellationToken ct = default)
    {
        if (!_options.AuditLogEnabled) return;

        var ctx = _http.HttpContext;
        var (actor, actorId) = ResolveActor(ctx);
        var remoteIp = ctx?.Connection.RemoteIpAddress?.ToString();

        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTime.UtcNow,
            Actor = actor,
            ActorId = actorId,
            RemoteIp = remoteIp,
            Action = action,
            Success = success,
            TargetType = targetType,
            TargetId = targetId,
            Detail = Truncate(detail, 2048)
        };

        try
        {
            // Use a scoped repo from a fresh scope — we may be called from a
            // background task or a place where the request scope has already
            // been disposed.
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IAuditLogRepository>();
            await repo.AddAsync(entry, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to persist audit entry: actor={Actor} action={Action} target={TargetType}/{TargetId}",
                actor, action, targetType, targetId);
        }
    }

    private static (string actor, string? actorId) ResolveActor(HttpContext? ctx)
    {
        if (ctx?.User.Identity is null || !ctx.User.Identity.IsAuthenticated)
            return ("anonymous", null);

        var role = ctx.User.IsInRole(NohddxAuthSchemes.AdminRole) ? "admin"
                 : ctx.User.IsInRole(NohddxAuthSchemes.AgentRole) ? "agent"
                 : "unknown";

        var id = ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return (role, id);
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s.Substring(0, max);
}
