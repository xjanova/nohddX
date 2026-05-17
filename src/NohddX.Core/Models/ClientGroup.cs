namespace NohddX.Core.Models;

public class ClientGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? DefaultImageId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<ClientMachine> Clients { get; set; } = new List<ClientMachine>();
    public BootImage? DefaultImage { get; set; }
}
