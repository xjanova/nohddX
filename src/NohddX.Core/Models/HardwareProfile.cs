namespace NohddX.Core.Models;

public class HardwareProfile
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DriverPackPath { get; set; }
    public string? NetworkDriverName { get; set; }
    public string? StorageDriverName { get; set; }
    public BootMode BootMode { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<ClientMachine> Clients { get; set; } = new List<ClientMachine>();
}
