using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Models;

namespace NohddX.Cluster.Discovery;

public class NodeDiscoveryService : BackgroundService
{
    private readonly NohddxOptions _options;
    private readonly ILogger<NodeDiscoveryService> _logger;
    private UdpClient? _udpClient;
    private readonly IPAddress _multicastGroup = IPAddress.Parse("239.255.77.88");
    private const int DiscoveryPort = 5001;

    public event EventHandler<ClusterNode>? NodeDiscovered;

    public NodeDiscoveryService(
        IOptions<NohddxOptions> options,
        ILogger<NodeDiscoveryService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Cluster.Enabled)
        {
            _logger.LogInformation("Cluster is disabled, node discovery will not start");
            return;
        }

        _logger.LogInformation("Node discovery starting on multicast group {Group}:{Port}",
            _multicastGroup, DiscoveryPort);

        var broadcastTask = BroadcastPresenceAsync(stoppingToken);
        var listenTask = ListenForNodesAsync(stoppingToken);

        await Task.WhenAll(broadcastTask, listenTask);
    }

    private async Task BroadcastPresenceAsync(CancellationToken ct)
    {
        using var sender = new UdpClient();
        sender.JoinMulticastGroup(_multicastGroup);

        var endpoint = new IPEndPoint(_multicastGroup, DiscoveryPort);
        var nodeName = _options.Cluster.NodeName ?? Environment.MachineName;
        var bindAddress = _options.Cluster.BindAddress ?? GetLocalIpAddress();

        var message = new DiscoveryMessage(
            NodeId: Guid.NewGuid().ToString(),
            Hostname: nodeName,
            IpAddress: bindAddress,
            Port: _options.Cluster.ClusterPort,
            Role: ClusterRole.Follower.ToString());

        var json = JsonSerializer.Serialize(message);
        var data = Encoding.UTF8.GetBytes(json);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await sender.SendAsync(data, data.Length, endpoint);
                _logger.LogDebug("Broadcast discovery message for node {Hostname}", nodeName);
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Discovery broadcast stopped due to cancellation");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to broadcast discovery message");
            }

            try
            {
                await Task.Delay(_options.Cluster.HeartbeatIntervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ListenForNodesAsync(CancellationToken ct)
    {
        _udpClient = new UdpClient(DiscoveryPort);
        _udpClient.JoinMulticastGroup(_multicastGroup);

        _logger.LogInformation("Listening for node discovery on port {Port}", DiscoveryPort);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(ct);
                var json = Encoding.UTF8.GetString(result.Buffer);
                var message = JsonSerializer.Deserialize<DiscoveryMessage>(json);

                if (message is null)
                {
                    _logger.LogWarning("Received invalid discovery message");
                    continue;
                }

                if (!Enum.TryParse<ClusterRole>(message.Role, out var role))
                {
                    role = ClusterRole.Follower;
                }

                var node = new ClusterNode
                {
                    Id = Guid.TryParse(message.NodeId, out var id) ? id : Guid.NewGuid(),
                    Hostname = message.Hostname,
                    IpAddress = message.IpAddress,
                    Port = message.Port,
                    Role = role,
                    Status = NodeStatus.Online,
                    LastHeartbeat = DateTime.UtcNow
                };

                _logger.LogDebug("Discovered node {Hostname} at {IpAddress}:{Port}",
                    node.Hostname, node.IpAddress, node.Port);

                NodeDiscovered?.Invoke(this, node);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error receiving discovery message");
            }
        }
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch
        {
            // Fall through to fallback
        }

        return "127.0.0.1";
    }

    public override void Dispose()
    {
        _udpClient?.Dispose();
        base.Dispose();
    }

    private record DiscoveryMessage(
        string NodeId,
        string Hostname,
        string IpAddress,
        int Port,
        string Role);
}
