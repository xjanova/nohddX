using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace NohddX.Agent.Discovery;

/// <summary>
/// Discovers a NoHddX server using (in order):
/// 1. Static URL/IP from <see cref="AgentConfig"/>.
/// 2. UDP broadcast on <see cref="AgentConfig.DiscoveryPort"/>
///    with the literal payload "NOHDDX_DISCOVER".
/// All failures are swallowed and surface as <c>null</c>.
/// </summary>
public class ServerDiscovery
{
    private readonly AgentConfig _config;

    public ServerDiscovery(AgentConfig config)
    {
        _config = config;
    }

    public async Task<string?> DiscoverServerAsync(CancellationToken ct = default)
    {
        // 1. Static URL configured?
        var staticUrl = _config.ResolvedServerUrl();
        if (!string.IsNullOrEmpty(staticUrl))
        {
            if (await ProbeAsync(staticUrl, ct))
                return staticUrl;
        }

        // 2. UDP broadcast discovery
        if (_config.UseMdnsDiscovery)
        {
            var discovered = await DiscoverViaBroadcastAsync(ct);
            if (discovered != null)
                return discovered;
        }

        return null;
    }

    private static async Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = await http.GetAsync($"{url.TrimEnd('/')}/api/agents/ping", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> DiscoverViaBroadcastAsync(CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient(0);
            udp.EnableBroadcast = true;

            var msg = Encoding.UTF8.GetBytes("NOHDDX_DISCOVER");
            await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, _config.DiscoveryPort));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_config.DiscoveryTimeoutSeconds));

            var receiveTask = udp.ReceiveAsync();
            var delayTask = Task.Delay(TimeSpan.FromSeconds(_config.DiscoveryTimeoutSeconds), cts.Token);
            var completed = await Task.WhenAny(receiveTask, delayTask);

            if (completed == receiveTask && receiveTask.IsCompletedSuccessfully)
            {
                var result = receiveTask.Result;
                var response = Encoding.UTF8.GetString(result.Buffer);

                // Expected: "NOHDDX_SERVER:<ip>:<port>"
                if (response.StartsWith("NOHDDX_SERVER:", StringComparison.Ordinal))
                {
                    var payload = response.Substring("NOHDDX_SERVER:".Length).Trim();
                    var parts = payload.Split(':');
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var port))
                    {
                        return $"http://{parts[0]}:{port}";
                    }
                    if (parts.Length == 1)
                    {
                        return $"http://{parts[0]}:{_config.ServerPort}";
                    }
                }
            }
        }
        catch
        {
            // Ignored - return null below.
        }

        return null;
    }
}
