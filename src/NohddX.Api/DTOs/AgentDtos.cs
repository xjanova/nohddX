using System.Text.Json.Serialization;

namespace NohddX.Api.DTOs;

/// <summary>
/// Hardware snapshot the bootstrap agent posts when it registers.
/// Mirrors NohddX.Agent.Hardware.HardwareInfo so the JSON wire format
/// is identical without sharing the agent project as a reference.
/// </summary>
public record AgentRegisterRequest(
    string Hostname,
    AgentSystemInfo System,
    AgentCpuInfo Cpu,
    AgentMemoryInfo Memory,
    List<AgentDiskInfo> Disks,
    List<AgentNetworkInfo> Networks,
    List<AgentGpuInfo> Gpus,
    AgentBootInfo Boot,
    DateTime DetectedAt);

public record AgentSystemInfo(string Manufacturer, string Model, string SerialNumber, string BiosVersion);
public record AgentCpuInfo(string Model, int PhysicalCores, int LogicalCores, double SpeedGhz, string Architecture);
public record AgentMemoryInfo(long TotalBytes, long AvailableBytes, int SlotCount);
public record AgentDiskInfo(string Device, string Model, string Serial, long SizeBytes, string Type, bool IsRotational, string? SmartHealth);
public record AgentNetworkInfo(string Interface, string MacAddress, string? IpAddress, long SpeedMbps, bool IsConnected);
public record AgentGpuInfo(string Vendor, string Model, long? VramBytes);
public record AgentBootInfo(string Mode, string Loader, bool SecureBoot);

public record AgentRegisterResponse(
    string AgentId,
    string Message,
    string? Token = null,
    int? TokenExpiryHours = null);

public record AgentStatusUpdate(string Status, double Progress, DateTime Timestamp);

public record AgentInstallRequest(string Mode);

public record AgentInstallInstructions(
    string ImageUrl,
    long ImageSize,
    string TargetDisk,
    string PartitionScheme,
    Dictionary<string, string> Metadata);
