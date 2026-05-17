namespace NohddX.Core.Configuration;

public class NohddxOptions
{
    public const string SectionName = "NohddX";

    public string StorageBasePath { get; set; } = @"C:\NohddX\Storage";
    public string BaseImagesPath { get; set; } = @"C:\NohddX\Storage\bases";
    public string OverlaysPath { get; set; } = @"C:\NohddX\Storage\overlays";
    public string SnapshotsPath { get; set; } = @"C:\NohddX\Storage\snapshots";
    public int CowBlockSizeBytes { get; set; } = 4096;

    public DhcpProxyOptions DhcpProxy { get; set; } = new();
    public IscsiOptions Iscsi { get; set; } = new();
    public ClusterOptions Cluster { get; set; } = new();
    public TftpOptions Tftp { get; set; } = new();
    public DiscoveryOptions Discovery { get; set; } = new();
}

public class DiscoveryOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4012;
    public int AnnouncedPort { get; set; } = 8080;
    public string? AnnouncedIp { get; set; }
}

public class DhcpProxyOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 4011;
    public string? NextServerIp { get; set; }
    public string BiosBootFile { get; set; } = "undionly.kpxe";
    public string UefiBootFile { get; set; } = "snponly.efi";
}

public class IscsiOptions
{
    public int Port { get; set; } = 3260;
    public string IqnPrefix { get; set; } = "iqn.2024.com.nohddx";
    public bool ChapEnabled { get; set; } = false;
    public string? ChapUsername { get; set; }
    public string? ChapPassword { get; set; }
    public int MaxConnections { get; set; } = 2000;
}

public class ClusterOptions
{
    public bool Enabled { get; set; } = false;
    public string? NodeName { get; set; }
    public string? BindAddress { get; set; }
    public int ClusterPort { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 2000;
    public int SuspectThreshold { get; set; } = 3;
    public int FailureThreshold { get; set; } = 5;
    public double RebalanceThresholdPercent { get; set; } = 20.0;
    public List<string> SeedNodes { get; set; } = new();
}

public class TftpOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 69;
    public string IpxeBinaryPath { get; set; } = @"tools\ipxe";
}
