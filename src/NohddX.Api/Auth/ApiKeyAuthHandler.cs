using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;

namespace NohddX.Api.Auth;

public static class NohddxAuthSchemes
{
    public const string Scheme = "NohddxApiKey";
    public const string AdminPolicy = "Admin";
    public const string AgentPolicy = "Agent";
    public const string AdminRole = "admin";
    public const string AgentRole = "agent";
}

public class ApiKeyAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authentication handler that recognizes:
/// <list type="bullet">
/// <item><c>X-Admin-Api-Key</c>: pre-shared admin key from
///   <see cref="SecurityOptions.AdminApiKey"/>. Grants <c>admin</c> role.</item>
/// <item><c>Authorization: Bearer &lt;agent-token&gt;</c>: HMAC-signed agent
///   token issued at <c>/api/agents/register</c>. Grants <c>agent</c> role
///   with the agent's GUID as the NameIdentifier claim.</item>
/// </list>
/// In Development, if no admin key is configured and
/// <see cref="SecurityOptions.AllowAnonymousAdminInDev"/> is true, the request
/// is treated as an anonymous admin (logged with a warning).
/// </summary>
public class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthOptions>
{
    private readonly SecurityOptions _security;
    private readonly AgentTokenService _agentTokens;
    private readonly IHostEnvironment _env;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IOptions<SecurityOptions> security,
        AgentTokenService agentTokens,
        IHostEnvironment env)
        : base(options, loggerFactory, encoder)
    {
        _security = security.Value;
        _agentTokens = agentTokens;
        _env = env;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_security.AuthEnabled)
        {
            // Auth fully disabled — treat everyone as admin (with warning logged once at startup elsewhere).
            return Task.FromResult(AuthenticateResult.Success(BuildTicket(NohddxAuthSchemes.AdminRole, "anonymous")));
        }

        // 1. Admin API key
        if (Request.Headers.TryGetValue("X-Admin-Api-Key", out var adminKeyHdr))
        {
            var provided = adminKeyHdr.ToString();

            if (!string.IsNullOrEmpty(_security.AdminApiKey) &&
                FixedTimeStringEquals(provided, _security.AdminApiKey))
            {
                var fingerprint = ComputeFingerprint(provided);
                return Task.FromResult(AuthenticateResult.Success(BuildTicket(NohddxAuthSchemes.AdminRole, fingerprint)));
            }

            return Task.FromResult(AuthenticateResult.Fail("Invalid admin API key."));
        }

        // 2. Agent bearer token
        if (Request.Headers.TryGetValue("Authorization", out var authHdr))
        {
            var raw = authHdr.ToString();
            if (raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var token = raw.Substring(7).Trim();
                var agentId = _agentTokens.Validate(token);
                if (agentId.HasValue)
                {
                    return Task.FromResult(AuthenticateResult.Success(
                        BuildTicket(NohddxAuthSchemes.AgentRole, agentId.Value.ToString())));
                }

                return Task.FromResult(AuthenticateResult.Fail("Invalid or expired agent token."));
            }
        }

        // 3. Dev fallback
        if (_env.IsDevelopment() &&
            string.IsNullOrWhiteSpace(_security.AdminApiKey) &&
            _security.AllowAnonymousAdminInDev)
        {
            return Task.FromResult(AuthenticateResult.Success(
                BuildTicket(NohddxAuthSchemes.AdminRole, "dev-anonymous")));
        }

        return Task.FromResult(AuthenticateResult.NoResult());
    }

    private AuthenticationTicket BuildTicket(string role, string actorId)
    {
        var identity = new ClaimsIdentity(NohddxAuthSchemes.Scheme);
        identity.AddClaim(new Claim(ClaimTypes.Role, role));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, actorId));
        identity.AddClaim(new Claim(ClaimTypes.Name, actorId));
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, NohddxAuthSchemes.Scheme);
    }

    private static bool FixedTimeStringEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    /// <summary>
    /// First 8 hex chars of SHA-256(key). Safe to log; allows tracing actions
    /// to a key without revealing the key itself.
    /// </summary>
    private static string ComputeFingerprint(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }
}
