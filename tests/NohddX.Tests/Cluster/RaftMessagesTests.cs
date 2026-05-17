using System.Text.Json;
using FluentAssertions;
using NohddX.Cluster.Consensus;
using NohddX.Cluster.Heartbeat;
using NohddX.Core.Models;
using Xunit;

namespace NohddX.Tests.Cluster;

public class RaftMessagesTests
{
    [Fact]
    public void RequestVote_serializes_to_json_round_trip()
    {
        var rpc = new RequestVoteRpc(
            CandidateId: Guid.NewGuid(),
            Term: 7,
            LastLogIndex: 0,
            LastLogTerm: 0,
            Timestamp: DateTime.UtcNow);

        var json = JsonSerializer.SerializeToUtf8Bytes(rpc);
        var parsed = JsonSerializer.Deserialize<RequestVoteRpc>(json);

        parsed.Should().NotBeNull();
        parsed!.CandidateId.Should().Be(rpc.CandidateId);
        parsed.Term.Should().Be(rpc.Term);
    }

    [Fact]
    public void Heartbeat_message_envelope_format_is_documented()
    {
        // Wire format: [type byte][JSON payload bytes]
        var heartbeat = new HeartbeatService.HeartbeatMessage(
            NodeId: Guid.NewGuid(),
            Term: 1,
            Role: ClusterRole.Leader,
            ClientCount: 0,
            CpuUsage: 0,
            MemoryUsage: 0,
            DiskIops: 0,
            Timestamp: DateTime.UtcNow);

        var json = JsonSerializer.SerializeToUtf8Bytes(heartbeat);
        var envelope = new byte[json.Length + 1];
        envelope[0] = (byte)ClusterMessageType.Heartbeat;
        Buffer.BlockCopy(json, 0, envelope, 1, json.Length);

        envelope[0].Should().Be((byte)ClusterMessageType.Heartbeat);

        // Receiver decodes:
        var typeByte = envelope[0];
        Enum.IsDefined(typeof(ClusterMessageType), typeByte).Should().BeTrue();
        var payload = new byte[envelope.Length - 1];
        Buffer.BlockCopy(envelope, 1, payload, 0, payload.Length);
        var decoded = JsonSerializer.Deserialize<HeartbeatService.HeartbeatMessage>(payload);

        decoded.Should().NotBeNull();
        decoded!.NodeId.Should().Be(heartbeat.NodeId);
    }
}
