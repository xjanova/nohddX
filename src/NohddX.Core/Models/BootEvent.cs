namespace NohddX.Core.Models;

public class BootEvent
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid? ImageId { get; set; }
    public Guid? NodeId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Message { get; set; }
    public bool Success { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public long? DurationMs { get; set; }
}
