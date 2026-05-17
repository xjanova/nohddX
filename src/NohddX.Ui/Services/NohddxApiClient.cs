using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NohddX.Api.DTOs;

namespace NohddX.Ui.Services;

/// <summary>
/// Thin typed client over the NohddX HTTP API. Lives for the lifetime of the
/// app; rebuilds its inner <see cref="HttpClient"/> when <see cref="AppSettings"/>
/// changes so the operator can re-target a different server without restarting.
/// </summary>
public sealed class NohddxApiClient : IDisposable
{
    private readonly AppSettings _settings;
    private HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public NohddxApiClient(AppSettings settings)
    {
        _settings = settings;
        _http = Build(settings);
        _settings.Changed += (_, _) =>
        {
            var old = _http;
            _http = Build(_settings);
            old.Dispose();
        };
    }

    private static HttpClient Build(AppSettings s)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri(s.ServerUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15)
        };
        if (!string.IsNullOrWhiteSpace(s.AdminApiKey))
            http.DefaultRequestHeaders.Add("X-Admin-Api-Key", s.AdminApiKey);
        return http;
    }

    public string ServerUrl => _settings.ServerUrl;

    /// <summary>
    /// Lightweight probe — used by the main window status LED. Hits the
    /// agent-ping endpoint because it's open even with auth on.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync("api/agents/ping", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─── Clients ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ClientResponse>> GetClientsAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ClientResponse>>("api/clients?take=1000", JsonOpts, ct);
        return result ?? new List<ClientResponse>();
    }

    public async Task<ClientResponse?> RegisterClientAsync(string mac, string? hostname, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("api/clients",
            new CreateClientRequest(mac, hostname), JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ClientResponse>(JsonOpts, ct);
    }

    public async Task DeleteClientAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"api/clients/{id}", ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AssignImageAsync(Guid clientId, Guid imageId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync(
            $"api/clients/{clientId}/assign", new AssignImageRequest(imageId), JsonOpts, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task WakeAsync(Guid clientId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"api/clients/{clientId}/wake", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ResetAsync(Guid clientId, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"api/clients/{clientId}/reset", null, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ─── Images ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ImageResponse>> GetImagesAsync(CancellationToken ct = default)
    {
        var result = await _http.GetFromJsonAsync<List<ImageResponse>>("api/images?take=1000", JsonOpts, ct);
        return result ?? new List<ImageResponse>();
    }

    // ─── Cluster ───────────────────────────────────────────────────────────

    public async Task<ClusterStatusResponse?> GetClusterStatusAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<ClusterStatusResponse>("api/cluster/status", JsonOpts, ct);
        }
        catch
        {
            // Cluster is optional; return null on failure so the UI shows "standalone"
            return null;
        }
    }

    // ─── Storage ───────────────────────────────────────────────────────────

    public async Task<StorageHealthResponse?> GetStorageHealthAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<StorageHealthResponse>("api/storage/health", JsonOpts, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DiskInfoResponse>> GetDisksAsync(CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<DiskInfoResponse>>("api/storage/disks", JsonOpts, ct)
                ?? new List<DiskInfoResponse>();
        }
        catch
        {
            return Array.Empty<DiskInfoResponse>();
        }
    }

    // ─── Monitoring ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AuditLogResponse>> GetAuditAsync(int take = 200, CancellationToken ct = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<AuditLogResponse>>($"api/monitoring/audit?take={take}", JsonOpts, ct)
                ?? new List<AuditLogResponse>();
        }
        catch
        {
            return Array.Empty<AuditLogResponse>();
        }
    }

    public void Dispose() => _http.Dispose();
}
