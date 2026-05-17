using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;

namespace NohddX.Api.Auth;

/// <summary>
/// Issues and validates HMAC-SHA256 signed bearer tokens for agents.
/// </summary>
/// <remarks>
/// Format (single line, base64url): <c>agentId.expiryUnix.signature</c> where
/// signature = base64url(HMAC-SHA256(<see cref="SecurityOptions.AgentTokenSecret"/>,
/// $"{agentId}.{expiryUnix}")).
///
/// We deliberately do NOT use JWT — this is a closed control plane between the
/// server and its own agents, so a hand-rolled HMAC envelope keeps the
/// dependency surface minimal and the signature is constant-time verifiable.
/// </remarks>
public class AgentTokenService
{
    private readonly byte[] _secret;
    private readonly TimeSpan _lifetime;
    private readonly ILogger<AgentTokenService> _logger;

    public AgentTokenService(IOptions<SecurityOptions> options, ILogger<AgentTokenService> logger)
    {
        _logger = logger;

        var opts = options.Value;
        // Keep the literal value the operator configured. A non-positive value
        // means tokens are issued already-expired — useful for testing and for
        // disabling the agent control plane in an emergency.
        _lifetime = TimeSpan.FromHours(opts.AgentTokenLifetimeHours);

        if (!string.IsNullOrWhiteSpace(opts.AgentTokenSecret))
        {
            // If the secret is base64, decode it; otherwise use UTF-8 bytes
            try
            {
                _secret = Convert.FromBase64String(opts.AgentTokenSecret);
                if (_secret.Length < 16)
                {
                    _logger.LogWarning("AgentTokenSecret is shorter than 16 bytes after base64 decode; falling back to UTF-8.");
                    _secret = Encoding.UTF8.GetBytes(opts.AgentTokenSecret);
                }
            }
            catch (FormatException)
            {
                _secret = Encoding.UTF8.GetBytes(opts.AgentTokenSecret);
            }
        }
        else
        {
            // Generate a random per-process secret. Tokens won't survive restart.
            _secret = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "No AgentTokenSecret configured. Generated a random per-process secret. " +
                "All agents will need to re-register after each server restart. " +
                "Set NohddX:Security:AgentTokenSecret to a base64-encoded 32-byte key for production.");
        }
    }

    /// <summary>
    /// Issue a bearer token for the given agent ID, valid for
    /// <see cref="SecurityOptions.AgentTokenLifetimeHours"/>.
    /// </summary>
    public string IssueToken(Guid agentId)
    {
        var expiry = DateTimeOffset.UtcNow.Add(_lifetime).ToUnixTimeSeconds();
        var payload = $"{agentId:N}.{expiry}";
        var signature = ComputeSignature(payload);
        return $"{payload}.{signature}";
    }

    /// <summary>
    /// Validate a bearer token and return the agent ID it was issued for.
    /// Returns null if signature is bad, expired, or malformed.
    /// </summary>
    public Guid? Validate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        if (!Guid.TryParseExact(parts[0], "N", out var agentId))
            return null;

        if (!long.TryParse(parts[1], out var expiryUnix))
            return null;

        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expiryUnix < nowUnix)
            return null; // Expired

        var expectedSig = ComputeSignature($"{parts[0]}.{parts[1]}");

        // Constant-time comparison
        var providedSigBytes = Encoding.ASCII.GetBytes(parts[2]);
        var expectedSigBytes = Encoding.ASCII.GetBytes(expectedSig);
        if (providedSigBytes.Length != expectedSigBytes.Length) return null;

        return CryptographicOperations.FixedTimeEquals(providedSigBytes, expectedSigBytes)
            ? agentId
            : null;
    }

    private string ComputeSignature(string payload)
    {
        using var hmac = new HMACSHA256(_secret);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
