using System.Text.Json;

namespace NohddX.Agent.Discovery;

/// <summary>
/// Persisted agent configuration. Loaded from one of several
/// well-known locations so the same ISO can be customised
/// per-deployment by dropping a single JSON file on the USB stick.
/// </summary>
public class AgentConfig
{
    public string? ServerUrl { get; set; }
    public string? ServerIp { get; set; }
    public int ServerPort { get; set; } = 8080;
    public bool UseMdnsDiscovery { get; set; } = true;
    public string? PreferredMode { get; set; } // "Persistent" | "Diskless" | "NetworkBoot"
    public int AgentPort { get; set; } = 7000;
    public int DiscoveryPort { get; set; } = 4012;
    public int DiscoveryTimeoutSeconds { get; set; } = 5;

    private static readonly string[] StandardPaths =
    {
        "/boot/nohddx-agent.json",
        "/etc/nohddx-agent.json",
        "./nohddx-agent.json",
        "nohddx-agent.json"
    };

    public static AgentConfig LoadFromStandardPaths()
    {
        foreach (var path in StandardPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = Load(path);
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // Skip and try the next path.
            }
        }
        return new AgentConfig();
    }

    public static AgentConfig? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AgentConfig();

            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            };

            return JsonSerializer.Deserialize<AgentConfig>(json, opts) ?? new AgentConfig();
        }
        catch
        {
            return new AgentConfig();
        }
    }

    public string ResolvedServerUrl()
    {
        if (!string.IsNullOrWhiteSpace(ServerUrl))
            return ServerUrl.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(ServerIp))
            return $"http://{ServerIp}:{ServerPort}";
        return string.Empty;
    }
}
