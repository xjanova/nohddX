using System.Net.Http.Json;
using System.Text.Json;
using NohddX.Agent.Hardware;

namespace NohddX.Agent.Communication;

/// <summary>
/// HTTP client used by the agent to communicate with the NoHddX server.
/// All methods catch their own exceptions and return null on failure
/// so the caller can render a friendly error in the TUI.
/// </summary>
public class AgentApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public AgentApiClient(string serverUrl)
    {
        _baseUrl = serverUrl.TrimEnd('/');
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public string BaseUrl => _baseUrl;

    public async Task<RegisterResponse?> RegisterAsync(HardwareInfo hardware, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents/register", hardware, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<RegisterResponse>(JsonOpts, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SendStatusAsync(string agentId, string status, double progress = 0, CancellationToken ct = default)
    {
        try
        {
            var body = new AgentStatusUpdate(status, progress, DateTime.UtcNow);
            using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents/{agentId}/status", body, JsonOpts, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<InstallInstructions?> RequestInstallAsync(string agentId, string mode, CancellationToken ct = default)
    {
        try
        {
            var body = new { mode };
            using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/api/agents/{agentId}/install", body, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<InstallInstructions>(JsonOpts, ct);
        }
        catch
        {
            return null;
        }
    }

    public async Task<Stream?> DownloadImageAsync(string url, CancellationToken ct = default)
    {
        try
        {
            // Use a fresh client with no timeout for long downloads
            var dl = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var resp = await dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                dl.Dispose();
                return null;
            }
            return await resp.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record RegisterResponse(string AgentId, string Message);

public record InstallInstructions(
    string ImageUrl,
    long ImageSize,
    string TargetDisk,
    string PartitionScheme,
    Dictionary<string, string> Metadata
);

public record AgentStatusUpdate(string Status, double Progress, DateTime Timestamp);
