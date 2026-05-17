namespace NohddX.Core.Models;

public class ImageSnapshot
{
    public Guid Id { get; set; }
    public Guid ImageId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string SnapshotPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation properties
    public BootImage Image { get; set; } = null!;
}
