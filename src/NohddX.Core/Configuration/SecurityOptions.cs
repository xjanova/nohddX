namespace NohddX.Core.Configuration;

/// <summary>
/// Security and authentication configuration for the NoHddX server.
/// </summary>
/// <remarks>
/// All keys are loaded from <c>appsettings.json</c> under <c>NohddX:Security</c> or
/// from environment variables (e.g. <c>NohddX__Security__AdminApiKey</c>). Never
/// commit real keys to source control. Use a secret manager / Windows credential
/// store / Linux keyring in production.
/// </remarks>
public class SecurityOptions
{
    public const string SectionName = "NohddX:Security";

    /// <summary>
    /// When true, all management endpoints (Clients, Images, Storage, Cluster,
    /// Groups, Monitoring, agent install/status) require authentication. Boot
    /// script endpoint and agent ping/register stay anonymous (PXE/UDP clients
    /// can't carry headers).
    /// </summary>
    public bool AuthEnabled { get; set; } = true;

    /// <summary>
    /// Pre-shared key required in the <c>X-Admin-Api-Key</c> header for any
    /// admin-scope action (managing clients, images, cluster). MUST be set in
    /// production. If left empty AND <see cref="AllowAnonymousAdminInDev"/> is
    /// true and the host is in Development, admin endpoints are allowed
    /// without auth — for local debug only.
    /// </summary>
    public string? AdminApiKey { get; set; }

    /// <summary>
    /// HMAC-SHA256 secret used to sign agent bearer tokens at registration.
    /// Must be at least 32 bytes when base64-decoded. If left empty a random
    /// secret is generated on startup; in that case all previously-issued
    /// tokens become invalid on restart.
    /// </summary>
    public string? AgentTokenSecret { get; set; }

    /// <summary>
    /// Lifetime of an issued agent token. Default 24 hours.
    /// </summary>
    public int AgentTokenLifetimeHours { get; set; } = 24;

    /// <summary>
    /// Optional bootstrap token. If set, an agent must include
    /// <c>X-Bootstrap-Token</c> on its first <c>/api/agents/register</c> call,
    /// otherwise registration is rejected. Use this on networks where rogue
    /// PXE devices might register and pivot. The token can be embedded in the
    /// agent ISO/USB at build time.
    /// </summary>
    public string? BootstrapToken { get; set; }

    /// <summary>
    /// In Development environment only: skip admin auth if no AdminApiKey is
    /// configured. Has no effect in Production / Staging.
    /// </summary>
    public bool AllowAnonymousAdminInDev { get; set; } = true;

    /// <summary>
    /// Per-IP rate limit on the agent registration endpoint (requests/minute).
    /// Defaults to 30 — enough for a fleet of clients PXE-booting concurrently
    /// after a power event but tight enough to slow brute-force registration.
    /// </summary>
    public int AgentRegisterRatePerMinute { get; set; } = 30;

    /// <summary>
    /// Per-IP rate limit on management endpoints (requests/minute). Defaults
    /// to 600 (≈10 RPS) — operator UIs are bursty but bounded.
    /// </summary>
    public int AdminRatePerMinute { get; set; } = 600;

    /// <summary>
    /// Maximum size (bytes) accepted for agent JSON bodies (register, status,
    /// install). Default 256 KB. Image download/upload is exempted.
    /// </summary>
    public int MaxAgentBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// When true, every admin and security-sensitive action is appended to
    /// the <c>AuditLogEntry</c> table.
    /// </summary>
    public bool AuditLogEnabled { get; set; } = true;
}
