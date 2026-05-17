using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Cluster.Discovery;
using NohddX.Cluster.Heartbeat;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Cluster.Consensus;

public class RaftClusterService : BackgroundService, IClusterService
{
    private readonly ConcurrentDictionary<Guid, ClusterNodeState> _nodes = new();
    private readonly NohddxOptions _options;
    private readonly NodeDiscoveryService _discovery;
    private readonly HeartbeatService _heartbeatService;
    private readonly ILogger<RaftClusterService> _logger;

    private Guid _localNodeId;
    private ClusterRole _currentRole = ClusterRole.Follower;
    private Guid? _leaderId;
    private int _currentTerm;
    private Guid? _votedForInTerm;
    private int _votedForTerm = -1;
    private readonly Random _electionRandom = new();
    private readonly SemaphoreSlim _electionLock = new(1, 1);
    private readonly ConcurrentDictionary<int, HashSet<Guid>> _votesByTerm = new();

    public bool IsLeader => _currentRole == ClusterRole.Leader;

    public event EventHandler<ClusterNode>? NodeJoined;
    public event EventHandler<ClusterNode>? NodeLeft;
    public event EventHandler<ClusterNode>? LeaderChanged;

    public RaftClusterService(
        IOptions<NohddxOptions> options,
        NodeDiscoveryService discovery,
        HeartbeatService heartbeatService,
        ILogger<RaftClusterService> logger)
    {
        _options = options.Value;
        _discovery = discovery;
        _heartbeatService = heartbeatService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Cluster.Enabled)
        {
            _logger.LogInformation("Cluster is disabled, Raft consensus will not start");
            return;
        }

        _localNodeId = Guid.NewGuid();
        _logger.LogInformation("Cluster node {NodeId} starting as {Role}",
            _localNodeId, _currentRole);

        RegisterLocalNode();

        _discovery.NodeDiscovered += OnNodeDiscovered;
        _heartbeatService.HeartbeatReceived += OnHeartbeatReceived;
        _heartbeatService.VoteRequested += OnVoteRequested;
        _heartbeatService.VoteReceived += OnVoteReceived;

        // Start the unified UDP listener for heartbeat / RequestVote / VoteResponse
        var listenTask = _heartbeatService.StartListeningAsync(_options.Cluster.ClusterPort, stoppingToken);

        var heartbeatTask = RunHeartbeatLoopAsync(stoppingToken);
        var electionTask = RunElectionCheckLoopAsync(stoppingToken);

        await Task.WhenAll(listenTask, heartbeatTask, electionTask);
    }

    private void RegisterLocalNode()
    {
        var nodeName = _options.Cluster.NodeName ?? Environment.MachineName;
        var bindAddress = _options.Cluster.BindAddress ?? "127.0.0.1";

        var localNode = new ClusterNode
        {
            Id = _localNodeId,
            Hostname = nodeName,
            IpAddress = bindAddress,
            Port = _options.Cluster.ClusterPort,
            Role = _currentRole,
            Status = NodeStatus.Online,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow
        };

        var state = new ClusterNodeState
        {
            Node = localNode,
            MissedHeartbeats = 0,
            LastHeartbeat = DateTime.UtcNow
        };

        _nodes[_localNodeId] = state;
        _logger.LogInformation("Local node registered: {NodeId} ({Hostname})",
            _localNodeId, nodeName);
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_currentRole == ClusterRole.Leader)
                {
                    await SendLeaderHeartbeatsAsync(ct);
                }

                // Update local node's heartbeat
                if (_nodes.TryGetValue(_localNodeId, out var localState))
                {
                    localState.LastHeartbeat = DateTime.UtcNow;
                    localState.Node.LastHeartbeat = DateTime.UtcNow;
                    localState.Node.UpdatedAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error in heartbeat loop");
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

    private async Task SendLeaderHeartbeatsAsync(CancellationToken ct)
    {
        var localState = _nodes.GetValueOrDefault(_localNodeId);
        if (localState is null) return;

        var message = new HeartbeatService.HeartbeatMessage(
            NodeId: _localNodeId,
            Term: _currentTerm,
            Role: ClusterRole.Leader,
            ClientCount: localState.Node.CurrentClientCount,
            CpuUsage: localState.Node.CpuUsagePercent,
            MemoryUsage: localState.Node.MemoryUsagePercent,
            DiskIops: localState.Node.DiskIops,
            Timestamp: DateTime.UtcNow);

        foreach (var (nodeId, state) in _nodes)
        {
            if (nodeId == _localNodeId) continue;
            if (state.Node.Status is NodeStatus.Offline or NodeStatus.Leaving) continue;

            try
            {
                var endpoint = new System.Net.IPEndPoint(
                    System.Net.IPAddress.Parse(state.Node.IpAddress),
                    state.Node.Port);

                await _heartbeatService.SendHeartbeatAsync(message, endpoint, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to send heartbeat to node {NodeId}", nodeId);
            }
        }
    }

    private async Task RunElectionCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_currentRole != ClusterRole.Leader)
                {
                    CheckForMissedHeartbeats();
                }

                DetectFailedNodes();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Error in election check loop");
            }

            try
            {
                // Add random jitter to avoid split-brain during simultaneous elections
                var jitter = _electionRandom.Next(0, _options.Cluster.HeartbeatIntervalMs / 2);
                await Task.Delay(_options.Cluster.HeartbeatIntervalMs + jitter, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void CheckForMissedHeartbeats()
    {
        if (_leaderId is null)
        {
            // No leader known, try to elect
            StartElection();
            return;
        }

        if (!_nodes.TryGetValue(_leaderId.Value, out var leaderState))
        {
            StartElection();
            return;
        }

        var elapsed = DateTime.UtcNow - leaderState.LastHeartbeat;
        var missedCount = (int)(elapsed.TotalMilliseconds / _options.Cluster.HeartbeatIntervalMs);

        if (missedCount >= _options.Cluster.FailureThreshold)
        {
            _logger.LogWarning(
                "Leader {LeaderId} missed {Count} heartbeats (threshold: {Threshold}), starting election",
                _leaderId, missedCount, _options.Cluster.FailureThreshold);

            leaderState.Node.Status = NodeStatus.Suspect;
            StartElection();
        }
        else if (missedCount >= _options.Cluster.SuspectThreshold)
        {
            _logger.LogWarning("Leader {LeaderId} suspected, missed {Count} heartbeats",
                _leaderId, missedCount);

            leaderState.Node.Status = NodeStatus.Suspect;
        }
    }

    private void DetectFailedNodes()
    {
        foreach (var (nodeId, state) in _nodes)
        {
            if (nodeId == _localNodeId) continue;
            if (state.Node.Status is NodeStatus.Offline or NodeStatus.Leaving) continue;

            var elapsed = DateTime.UtcNow - state.LastHeartbeat;
            var missedCount = (int)(elapsed.TotalMilliseconds / _options.Cluster.HeartbeatIntervalMs);

            if (missedCount >= _options.Cluster.FailureThreshold)
            {
                _logger.LogWarning("Node {NodeId} ({Hostname}) declared failed after {Count} missed heartbeats",
                    nodeId, state.Node.Hostname, missedCount);

                state.Node.Status = NodeStatus.Offline;
                NodeLeft?.Invoke(this, state.Node);
            }
            else if (missedCount >= _options.Cluster.SuspectThreshold
                     && state.Node.Status != NodeStatus.Suspect)
            {
                state.Node.Status = NodeStatus.Suspect;
                _logger.LogWarning("Node {NodeId} ({Hostname}) suspected, missed {Count} heartbeats",
                    nodeId, state.Node.Hostname, missedCount);
            }
        }
    }

    private void StartElection()
    {
        if (!_electionLock.Wait(0)) return; // Another election already running

        try
        {
            _currentTerm++;
            _currentRole = ClusterRole.Candidate;
            _votedForInTerm = _localNodeId; // self-vote
            _votedForTerm = _currentTerm;

            // Track votes for THIS term in a fresh set, including our own.
            var votes = _votesByTerm.GetOrAdd(_currentTerm, _ => new HashSet<Guid>());
            lock (votes) { votes.Add(_localNodeId); }

            _logger.LogInformation("Starting election for term {Term}", _currentTerm);

            var onlineNodes = _nodes.Values
                .Where(s => s.Node.Status == NodeStatus.Online && s.Node.Id != _localNodeId)
                .ToList();

            if (onlineNodes.Count == 0)
            {
                // Single-node cluster: we win immediately.
                BecomeLeader();
                return;
            }

            var rpc = new RequestVoteRpc(
                CandidateId: _localNodeId,
                Term: _currentTerm,
                LastLogIndex: 0,
                LastLogTerm: 0,
                Timestamp: DateTime.UtcNow);

            // Fire RequestVote to every online peer. Replies arrive on the
            // VoteReceived event and are tallied in OnVoteReceived.
            foreach (var peer in onlineNodes)
            {
                if (!IPAddress.TryParse(peer.Node.IpAddress, out var ip))
                    continue;
                var endpoint = new IPEndPoint(ip, peer.Node.Port);

                _ = _heartbeatService.SendRequestVoteAsync(rpc, endpoint, CancellationToken.None);
            }
        }
        finally
        {
            _electionLock.Release();
        }
    }

    private void OnVoteRequested(object? sender, (IPEndPoint From, RequestVoteRpc Rpc) args)
    {
        var rpc = args.Rpc;

        // §5.1: if RPC term > currentTerm, update and become follower.
        if (rpc.Term > _currentTerm)
        {
            _currentTerm = rpc.Term;
            _currentRole = ClusterRole.Follower;
            _votedForInTerm = null;
            _votedForTerm = -1;
            _leaderId = null;
        }

        bool grant = false;
        if (rpc.Term >= _currentTerm)
        {
            // Grant if we haven't already voted in this term, OR we already
            // voted for the same candidate (idempotent retransmissions).
            if (_votedForTerm != rpc.Term || _votedForInTerm == rpc.CandidateId)
            {
                grant = true;
                _votedForInTerm = rpc.CandidateId;
                _votedForTerm = rpc.Term;
            }
        }

        var response = new VoteResponseRpc(
            VoterId: _localNodeId,
            Term: _currentTerm,
            VoteGranted: grant,
            Timestamp: DateTime.UtcNow);

        _ = _heartbeatService.SendVoteResponseAsync(response, args.From, CancellationToken.None);

        _logger.LogInformation(
            "RequestVote: candidate={Candidate} term={Term} -> grant={Grant} (current_term={Current})",
            rpc.CandidateId, rpc.Term, grant, _currentTerm);
    }

    private void OnVoteReceived(object? sender, VoteResponseRpc rpc)
    {
        // If a peer reports a higher term, step down.
        if (rpc.Term > _currentTerm)
        {
            _currentTerm = rpc.Term;
            _currentRole = ClusterRole.Follower;
            _votedForInTerm = null;
            _votedForTerm = -1;
            _leaderId = null;
            return;
        }

        // Stale vote response (from a previous election we no longer care about)
        if (rpc.Term != _currentTerm || _currentRole != ClusterRole.Candidate)
            return;

        if (!rpc.VoteGranted) return;

        var votes = _votesByTerm.GetOrAdd(_currentTerm, _ => new HashSet<Guid>());
        int count;
        lock (votes)
        {
            votes.Add(rpc.VoterId);
            count = votes.Count;
        }

        var totalNodes = _nodes.Values.Count(s => s.Node.Status == NodeStatus.Online);
        var majority = (totalNodes / 2) + 1;

        _logger.LogDebug("Vote tally: term={Term} votes={Votes} majority={Majority}/{Total}",
            _currentTerm, count, majority, totalNodes);

        if (count >= majority)
        {
            BecomeLeader();
        }
    }

    private void BecomeLeader()
    {
        _currentRole = ClusterRole.Leader;
        _leaderId = _localNodeId;

        if (_nodes.TryGetValue(_localNodeId, out var localState))
        {
            localState.Node.Role = ClusterRole.Leader;
        }

        _logger.LogInformation("Node {NodeId} became leader for term {Term}", _localNodeId, _currentTerm);

        LeaderChanged?.Invoke(this, _nodes[_localNodeId].Node);
    }

    private void OnNodeDiscovered(object? sender, ClusterNode discoveredNode)
    {
        if (discoveredNode.Id == _localNodeId) return;

        var isNew = !_nodes.ContainsKey(discoveredNode.Id);

        var state = _nodes.GetOrAdd(discoveredNode.Id, _ => new ClusterNodeState
        {
            Node = discoveredNode,
            MissedHeartbeats = 0,
            LastHeartbeat = DateTime.UtcNow
        });

        state.LastHeartbeat = DateTime.UtcNow;
        state.Node.Status = NodeStatus.Online;
        state.Node.LastHeartbeat = DateTime.UtcNow;

        if (isNew)
        {
            _logger.LogInformation("Node {NodeId} ({Hostname}) joined the cluster",
                discoveredNode.Id, discoveredNode.Hostname);
            NodeJoined?.Invoke(this, discoveredNode);
        }
    }

    private void OnHeartbeatReceived(object? sender, HeartbeatService.HeartbeatMessage msg)
    {
        if (msg.NodeId == _localNodeId) return;

        if (_nodes.TryGetValue(msg.NodeId, out var state))
        {
            state.LastHeartbeat = DateTime.UtcNow;
            state.MissedHeartbeats = 0;
            state.Node.LastHeartbeat = DateTime.UtcNow;
            state.Node.CpuUsagePercent = msg.CpuUsage;
            state.Node.MemoryUsagePercent = msg.MemoryUsage;
            state.Node.DiskIops = msg.DiskIops;
            state.Node.CurrentClientCount = msg.ClientCount;
            state.Node.Role = msg.Role;
            state.Node.Status = NodeStatus.Online;

            // If received heartbeat from a leader with higher term, step down
            if (msg.Role == ClusterRole.Leader && msg.Term >= _currentTerm)
            {
                if (_currentRole is ClusterRole.Leader or ClusterRole.Candidate)
                {
                    _logger.LogInformation(
                        "Stepping down: received heartbeat from leader {LeaderId} with term {Term}",
                        msg.NodeId, msg.Term);
                    _currentRole = ClusterRole.Follower;

                    if (_nodes.TryGetValue(_localNodeId, out var localState))
                    {
                        localState.Node.Role = ClusterRole.Follower;
                    }
                }

                _leaderId = msg.NodeId;
                _currentTerm = msg.Term;
            }
        }
    }

    public Task JoinClusterAsync(string nodeAddress, CancellationToken ct = default)
    {
        _logger.LogInformation("Joining cluster via seed node {Address}", nodeAddress);

        // Parse address (format: host:port)
        var parts = nodeAddress.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[1], out var port))
        {
            _logger.LogError("Invalid node address format: {Address}. Expected host:port", nodeAddress);
            return Task.CompletedTask;
        }

        var seedNode = new ClusterNode
        {
            Id = Guid.NewGuid(),
            Hostname = parts[0],
            IpAddress = parts[0],
            Port = port,
            Role = ClusterRole.Follower,
            Status = NodeStatus.Online,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            LastHeartbeat = DateTime.UtcNow
        };

        var state = new ClusterNodeState
        {
            Node = seedNode,
            MissedHeartbeats = 0,
            LastHeartbeat = DateTime.UtcNow
        };

        _nodes[seedNode.Id] = state;
        NodeJoined?.Invoke(this, seedNode);

        return Task.CompletedTask;
    }

    public Task LeaveClusterAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Node {NodeId} leaving cluster", _localNodeId);

        if (_nodes.TryGetValue(_localNodeId, out var localState))
        {
            localState.Node.Status = NodeStatus.Leaving;
            localState.Node.Role = ClusterRole.Follower;
        }

        // If this node is the leader, force an election on the remaining nodes
        if (_currentRole == ClusterRole.Leader)
        {
            _logger.LogInformation("Leader is leaving, other nodes will elect a new leader");
            _currentRole = ClusterRole.Follower;
            _leaderId = null;
        }

        NodeLeft?.Invoke(this, localState?.Node ?? new ClusterNode { Id = _localNodeId });

        return Task.CompletedTask;
    }

    public ClusterNode? GetLeaderNode()
    {
        if (_leaderId.HasValue && _nodes.TryGetValue(_leaderId.Value, out var state))
        {
            return state.Node;
        }

        return null;
    }

    public IReadOnlyList<ClusterNode> GetClusterNodes()
    {
        return _nodes.Values
            .Select(s => s.Node)
            .ToList()
            .AsReadOnly();
    }

    public override void Dispose()
    {
        _electionLock.Dispose();
        base.Dispose();
    }

    private class ClusterNodeState
    {
        public ClusterNode Node { get; set; } = null!;
        public int MissedHeartbeats { get; set; }
        public DateTime LastHeartbeat { get; set; }
    }
}
