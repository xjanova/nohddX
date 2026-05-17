using System.IO;
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

    public async Task<ImageResponse?> UploadImageAsync(
        string localPath,
        string name,
        NohddX.Core.Models.OsType osType,
        string version,
        bool isDefault,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        // Image uploads can be many GBs; the shared HttpClient's 15s timeout
        // would trip a long copy. Spin up a one-off client with no timeout —
        // the caller's CancellationToken is the only stop signal.
        using var bigClient = new HttpClient
        {
            BaseAddress = _http.BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        foreach (var header in _http.DefaultRequestHeaders)
            bigClient.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);

        await using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 1 << 20, useAsync: true);

        // Wrap the file stream so we can report bytes-read progress to the
        // UI without depending on HttpClient internals.
        using var reporting = new ProgressStream(fs, progress);

        var ext = Path.GetExtension(localPath).TrimStart('.');
        var qs = $"?name={Uri.EscapeDataString(name)}&osType={osType}" +
                 $"&version={Uri.EscapeDataString(version)}" +
                 $"&isDefault={(isDefault ? "true" : "false")}" +
                 (string.IsNullOrEmpty(ext) ? "" : $"&extension={Uri.EscapeDataString(ext)}");

        using var content = new StreamContent(reporting);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/images/upload" + qs)
        {
            Content = content
        };

        using var resp = await bigClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<ImageResponse>(JsonOpts, ct);
    }

    /// <summary>
    /// Read-through wrapper that pings an <see cref="IProgress{T}"/> with
    /// the running byte count so the operator sees progress instead of a
    /// frozen dialog during a multi-gigabyte upload.
    /// </summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly IProgress<long>? _progress;
        private long _read;

        public ProgressStream(Stream inner, IProgress<long>? progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0) { _read += n; _progress?.Report(_read); }
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var n = await _inner.ReadAsync(buffer, ct);
            if (n > 0) { _read += n; _progress?.Report(_read); }
            return n;
        }
    }

    public async Task DeleteImageAsync(Guid id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"api/images/{id}", ct);
        resp.EnsureSuccessStatusCode();
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
