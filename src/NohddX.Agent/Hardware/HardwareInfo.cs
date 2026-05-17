namespace NohddX.Agent.Hardware;

/// <summary>
/// Top-level snapshot of detected client hardware. Used to register
/// the agent with the NoHddX server and to drive boot/install decisions.
/// </summary>
public record HardwareInfo(
    string Hostname,
    SystemInfo System,
    CpuInfo Cpu,
    MemoryInfo Memory,
    List<DiskInfo> Disks,
    List<NetworkInfo> Networks,
    List<GpuInfo> Gpus,
    BootInfo Boot,
    DateTime DetectedAt
);

public record SystemInfo(
    string Manufacturer,
    string Model,
    string SerialNumber,
    string BiosVersion
);

public record CpuInfo(
    string Model,
    int PhysicalCores,
    int LogicalCores,
    double SpeedGhz,
    string Architecture
);

public record MemoryInfo(
    long TotalBytes,
    long AvailableBytes,
    int SlotCount
);

public record DiskInfo(
    string Device,
    string Model,
    string Serial,
    long SizeBytes,
    string Type,
    bool IsRotational,
    string? SmartHealth
);

public record NetworkInfo(
    string Interface,
    string MacAddress,
    string? IpAddress,
    long SpeedMbps,
    bool IsConnected
);

public record GpuInfo(
    string Vendor,
    string Model,
    long? VramBytes
);

public record BootInfo(
    string Mode,
    string Loader,
    bool SecureBoot
);
