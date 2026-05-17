namespace NohddX.Core.Models;

public class ClusterNode
{
    public Guid Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int Port { get; set; }
    public ClusterRole Role { get; set; }
    public NodeStatus Status { get; set; }
    public int MaxClients { get; set; }
    public int CurrentClientCount { get; set; }
    public double CpuUsagePercent { get; set; }
    public double MemoryUsagePercent { get; set; }
    public double DiskIops { get; set; }
    public double NetworkBandwidthMbps { get; set; }
    public int IscsiPort { get; set; }
    public int ApiPort { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public DateTime JoinedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
