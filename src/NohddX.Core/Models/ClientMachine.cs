namespace NohddX.Core.Models;

public class ClientMachine
{
    public Guid Id { get; set; }
    public string MacAddress { get; set; } = string.Empty;
    public string? Hostname { get; set; }
    public string? IpAddress { get; set; }
    public Guid? GroupId { get; set; }
    public Guid? HardwareProfileId { get; set; }
    public ClientStatus Status { get; set; }
    public Guid? AssignedNodeId { get; set; }
    public DateTime? LastSeen { get; set; }
    public DateTime? LastBootTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ClientGroup? Group { get; set; }
    public HardwareProfile? HardwareProfile { get; set; }
    public BootAssignment? BootAssignment { get; set; }
    public ClusterNode? AssignedNode { get; set; }
}
