using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Cluster.Consensus;
using NohddX.Core.Configuration;
using NohddX.Core.Models;

namespace NohddX.Cluster.Heartbeat;

/// <summary>
/// UDP transport for cluster control messages. Multiplexes Heartbeat,
/// RequestVote, VoteResponse, and AppendEntriesAck on a single port,
/// distinguished by a one-byte type prefix prepended to the JSON payload.
/// </summary>
public class HeartbeatService : IDisposable
{
    private readonly NohddxOptions _options;
    private readonly ILogger<HeartbeatService> _logger;
    private UdpClient? _sender;
    private UdpClient? _receiver;
    private bool _disposed;

    public event EventHandler<HeartbeatMessage>? HeartbeatReceived;
    public event EventHandler<(IPEndPoint From, RequestVoteRpc Rpc)>? VoteRequested;
    public event EventHandler<VoteResponseRpc>? VoteReceived;
    public event EventHandler<AppendEntriesAckRpc>? AppendAckReceived;

    public HeartbeatService(
        IOptions<NohddxOptions> options,
        ILogger<HeartbeatService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public Task SendHeartbeatAsync(HeartbeatMessage msg, IPEndPoint target, CancellationToken ct)
        => SendEnvelopeAsync(ClusterMessageType.Heartbeat, msg, target, ct);

    public Task SendRequestVoteAsync(RequestVoteRpc rpc, IPEndPoint target, CancellationToken ct)
        => SendEnvelopeAsync(ClusterMessageType.RequestVote, rpc, target, ct);

    public Task SendVoteResponseAsync(VoteResponseRpc rpc, IPEndPoint target, CancellationToken ct)
        => SendEnvelopeAsync(ClusterMessageType.VoteResponse, rpc, target, ct);

    public Task SendAppendAckAsync(AppendEntriesAckRpc rpc, IPEndPoint target, CancellationToken ct)
        => SendEnvelopeAsync(ClusterMessageType.AppendEntriesAck, rpc, target, ct);

    private async Task SendEnvelopeAsync<T>(ClusterMessageType type, T payload, IPEndPoint target, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sender ??= new UdpClient();

        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(payload);
            var data = new byte[json.Length + 1];
            data[0] = (byte)type;
            Buffer.BlockCopy(json, 0, data, 1, json.Length);

            await _sender.SendAsync(data, data.Length, target);
            _logger.LogDebug("Sent {Type} ({Len} bytes) to {Target}", type, data.Length, target);
        }
        catch (SocketException) when (ct.IsCancellationRequested)
        {
            _logger.LogDebug("Cluster send cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send {Type} to {Target}", type, target);
        }
    }

    public async Task StartListeningAsync(int port, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _receiver = new UdpClient(port);
        _logger.LogInformation("Cluster control listener started on port {Port}", port);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _receiver.ReceiveAsync(ct);
                if (result.Buffer.Length < 2)
                {
                    _logger.LogDebug("Ignoring short cluster packet from {Remote}", result.RemoteEndPoint);
                    continue;
                }

                var typeByte = result.Buffer[0];

                // Backwards compatibility: if the first byte isn't a known
                // type, attempt to deserialize the whole packet as a legacy
                // heartbeat (pre-envelope format).
                if (!Enum.IsDefined(typeof(ClusterMessageType), typeByte))
                {
                    TryDispatchLegacyHeartbeat(result.Buffer);
                    continue;
                }

                var payload = new byte[result.Buffer.Length - 1];
                Buffer.BlockCopy(result.Buffer, 1, payload, 0, payload.Length);
                Dispatch((ClusterMessageType)typeByte, payload, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error receiving cluster control packet");
            }
        }

        _logger.LogInformation("Cluster control listener stopped");
    }

    private void Dispatch(ClusterMessageType type, byte[] payload, IPEndPoint from)
    {
        try
        {
            switch (type)
            {
                case ClusterMessageType.Heartbeat:
                {
                    var msg = JsonSerializer.Deserialize<HeartbeatMessage>(payload);
                    if (msg is not null) HeartbeatReceived?.Invoke(this, msg);
                    break;
                }
                case ClusterMessageType.RequestVote:
                {
                    var msg = JsonSerializer.Deserialize<RequestVoteRpc>(payload);
                    if (msg is not null) VoteRequested?.Invoke(this, (from, msg));
                    break;
                }
                case ClusterMessageType.VoteResponse:
                {
                    var msg = JsonSerializer.Deserialize<VoteResponseRpc>(payload);
                    if (msg is not null) VoteReceived?.Invoke(this, msg);
                    break;
                }
                case ClusterMessageType.AppendEntriesAck:
                {
                    var msg = JsonSerializer.Deserialize<AppendEntriesAckRpc>(payload);
                    if (msg is not null) AppendAckReceived?.Invoke(this, msg);
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Bad JSON in cluster {Type} packet from {From}", type, from);
        }
    }

    private void TryDispatchLegacyHeartbeat(byte[] buffer)
    {
        try
        {
            var json = Encoding.UTF8.GetString(buffer);
            var msg = JsonSerializer.Deserialize<HeartbeatMessage>(json);
            if (msg is not null)
            {
                _logger.LogDebug("Received legacy (no-envelope) heartbeat from {NodeId}", msg.NodeId);
                HeartbeatReceived?.Invoke(this, msg);
            }
        }
        catch (JsonException) { /* swallow — packet wasn't ours */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sender?.Dispose();
        _receiver?.Dispose();
    }

    public record HeartbeatMessage(
        Guid NodeId,
        int Term,
        ClusterRole Role,
        int ClientCount,
        double CpuUsage,
        double MemoryUsage,
        double DiskIops,
        DateTime Timestamp);
}
