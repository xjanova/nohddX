namespace NohddX.Core.Models;

public class BootImage
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public OsType OsType { get; set; }
    public string Version { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Checksum { get; set; }
    public ImageStatus Status { get; set; }
    public bool IsDefault { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<BootAssignment> Assignments { get; set; } = new List<BootAssignment>();
    public ICollection<ImageSnapshot> Snapshots { get; set; } = new List<ImageSnapshot>();
}
