using NohddX.Core.Models;

namespace NohddX.Cluster.Consensus;

/// <summary>
/// Wire-format envelope for cluster RPC messages, multiplexed onto the
/// heartbeat UDP channel. The first byte of every payload is a
/// <see cref="ClusterMessageType"/> discriminator; the rest is JSON.
/// </summary>
public enum ClusterMessageType : byte
{
    Heartbeat = 1,
    RequestVote = 2,
    VoteResponse = 3,
    AppendEntriesAck = 4,
}

/// <summary>RequestVote RPC (RFC 5.2 of the Raft paper).</summary>
public record RequestVoteRpc(
    Guid CandidateId,
    int Term,
    int LastLogIndex,
    int LastLogTerm,
    DateTime Timestamp);

/// <summary>Reply to RequestVote.</summary>
public record VoteResponseRpc(
    Guid VoterId,
    int Term,
    bool VoteGranted,
    DateTime Timestamp);

/// <summary>
/// Lightweight ack-only AppendEntries used for leader keep-alive. Full log
/// replication would extend this with an entries[] array.
/// </summary>
public record AppendEntriesAckRpc(
    Guid FollowerId,
    int Term,
    bool Success,
    DateTime Timestamp);

/// <summary>
/// Heartbeat retains its existing JSON shape but is wrapped under the
/// envelope so the receiver can dispatch by type byte.
/// </summary>
public record HeartbeatRpc(
    Guid NodeId,
    int Term,
    ClusterRole Role,
    int ClientCount,
    double CpuUsage,
    double MemoryUsage,
    double DiskIops,
    DateTime Timestamp);
