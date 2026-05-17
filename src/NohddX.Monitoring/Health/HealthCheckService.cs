using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;

namespace NohddX.Monitoring.Health;

/// <summary>
/// Checks the health of all NohddX subsystems and produces a consolidated report.
/// </summary>
public class HealthCheckService
{
    private readonly NohddxOptions _options;
    private readonly ILogger<HealthCheckService> _logger;

    public HealthCheckService(
        IOptions<NohddxOptions> options,
        ILogger<HealthCheckService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Runs all health checks and returns an aggregated report.
    /// </summary>
    public async Task<HealthReport> GetOverallHealthAsync(CancellationToken ct = default)
    {
        var report = new HealthReport
        {
            CheckedAt = DateTime.UtcNow
        };

        var checks = new[]
        {
            CheckStorageHealthAsync(ct),
            CheckIscsiHealthAsync(ct),
            CheckDhcpProxyHealthAsync(ct),
            CheckTftpHealthAsync(ct)
        };

        var results = await Task.WhenAll(checks);
        foreach (var component in results)
        {
            report.Components[component.Name] = component;
        }

        report.IsHealthy = report.Components.Values.All(c => c.IsHealthy);
        return report;
    }

    /// <summary>
    /// Verifies that configured storage paths exist and are writable.
    /// </summary>
    public Task<ComponentHealth> CheckStorageHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "Storage" };

        try
        {
            var paths = new[]
            {
                _options.StorageBasePath,
                _options.BaseImagesPath,
                _options.OverlaysPath,
                _options.SnapshotsPath
            };

            var missing = paths.Where(p => !Directory.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                health.IsHealthy = false;
                health.Message = $"Missing directories: {string.Join(", ", missing)}";
            }
            else
            {
                // Verify writable by touching a temp file
                var testFile = Path.Combine(_options.StorageBasePath, ".health_check");
                File.WriteAllText(testFile, DateTime.UtcNow.ToString("O"));
                File.Delete(testFile);

                health.IsHealthy = true;
                health.Message = "All storage paths accessible and writable";
            }
        }
        catch (Exception ex)
        {
            health.IsHealthy = false;
            health.Message = $"Storage check failed: {ex.Message}";
        }

        sw.Stop();
        health.ResponseTime = sw.Elapsed;
        return Task.FromResult(health);
    }

    /// <summary>
    /// Verifies that the iSCSI port is open and listening.
    /// </summary>
    public async Task<ComponentHealth> CheckIscsiHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "iSCSI" };

        try
        {
            using var tcp = new TcpClient();
            var connectTask = tcp.ConnectAsync("127.0.0.1", _options.Iscsi.Port, ct);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(3), ct);

            if (await Task.WhenAny(connectTask.AsTask(), timeoutTask) == timeoutTask)
            {
                health.IsHealthy = false;
                health.Message = $"iSCSI port {_options.Iscsi.Port} connection timed out";
            }
            else
            {
                await connectTask;
                health.IsHealthy = true;
                health.Message = $"iSCSI listening on port {_options.Iscsi.Port}";
            }
        }
        catch (SocketException)
        {
            health.IsHealthy = false;
            health.Message = $"iSCSI port {_options.Iscsi.Port} not listening";
        }
        catch (Exception ex)
        {
            health.IsHealthy = false;
            health.Message = $"iSCSI check failed: {ex.Message}";
        }

        sw.Stop();
        health.ResponseTime = sw.Elapsed;
        return health;
    }

    /// <summary>
    /// Verifies that the DHCP proxy service port is open and listening.
    /// </summary>
    public async Task<ComponentHealth> CheckDhcpProxyHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "DHCP Proxy" };

        if (!_options.DhcpProxy.Enabled)
        {
            health.IsHealthy = true;
            health.Message = "DHCP Proxy is disabled";
            health.ResponseTime = sw.Elapsed;
            return health;
        }

        try
        {
            // DHCP proxy uses UDP, so we check if the port is bound
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,
                    _options.DhcpProxy.Port));
                // If we can bind, it means nothing else is listening
                health.IsHealthy = false;
                health.Message = $"DHCP Proxy port {_options.DhcpProxy.Port} not bound";
            }
            catch (SocketException)
            {
                // Port already in use means the service is running
                health.IsHealthy = true;
                health.Message = $"DHCP Proxy running on port {_options.DhcpProxy.Port}";
            }
        }
        catch (Exception ex)
        {
            health.IsHealthy = false;
            health.Message = $"DHCP Proxy check failed: {ex.Message}";
        }

        sw.Stop();
        health.ResponseTime = sw.Elapsed;
        return health;
    }

    /// <summary>
    /// Verifies that the TFTP service port is open and listening.
    /// </summary>
    public async Task<ComponentHealth> CheckTftpHealthAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var health = new ComponentHealth { Name = "TFTP" };

        if (!_options.Tftp.Enabled)
        {
            health.IsHealthy = true;
            health.Message = "TFTP is disabled";
            health.ResponseTime = sw.Elapsed;
            return health;
        }

        try
        {
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,
                    _options.Tftp.Port));
                health.IsHealthy = false;
                health.Message = $"TFTP port {_options.Tftp.Port} not bound";
            }
            catch (SocketException)
            {
                health.IsHealthy = true;
                health.Message = $"TFTP running on port {_options.Tftp.Port}";
            }
        }
        catch (Exception ex)
        {
            health.IsHealthy = false;
            health.Message = $"TFTP check failed: {ex.Message}";
        }

        sw.Stop();
        health.ResponseTime = sw.Elapsed;
        return health;
    }
}

/// <summary>
/// Aggregated health status of all system components.
/// </summary>
public class HealthReport
{
    public bool IsHealthy { get; set; }
    public DateTime CheckedAt { get; set; }
    public Dictionary<string, ComponentHealth> Components { get; set; } = new();
}

/// <summary>
/// Health status of a single system component.
/// </summary>
public class ComponentHealth
{
    public string Name { get; set; } = "";
    public bool IsHealthy { get; set; }
    public string? Message { get; set; }
    public TimeSpan? ResponseTime { get; set; }
}
