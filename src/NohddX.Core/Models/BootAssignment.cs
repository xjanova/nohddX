namespace NohddX.Core.Models;

public class BootAssignment
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ImageId { get; set; }
    public Guid? HardwareProfileId { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ClientMachine Client { get; set; } = null!;
    public BootImage Image { get; set; } = null!;
    public HardwareProfile? Profile { get; set; }
}
