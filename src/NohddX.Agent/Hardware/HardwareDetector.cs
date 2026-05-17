using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace NohddX.Agent.Hardware;

/// <summary>
/// Hardware detection facade. On Linux uses /proc, /sys and a few
/// optional userspace tools (dmidecode, lspci, lsblk). On other
/// platforms returns best-effort values from .NET so the project
/// can be developed and unit-built on Windows.
/// </summary>
public class HardwareDetector
{
    public async Task<HardwareInfo> DetectAsync()
    {
        var system = await SafeAsync(DetectSystemAsync, new SystemInfo("Unknown", "Unknown", "Unknown", "Unknown"));
        var cpu = await SafeAsync(DetectCpuAsync, new CpuInfo("Unknown", 1, 1, 0, RuntimeInformation.OSArchitecture.ToString()));
        var memory = await SafeAsync(DetectMemoryAsync, new MemoryInfo(0, 0, 0));
        var disks = await SafeListAsync(DetectDisksAsync);
        var networks = await SafeListAsync(DetectNetworksAsync);
        var gpus = await SafeListAsync(DetectGpusAsync);
        var boot = SafeSync(DetectBootInfo, new BootInfo("Unknown", "Unknown", false));

        return new HardwareInfo(
            Hostname: SafeHostname(),
            System: system,
            Cpu: cpu,
            Memory: memory,
            Disks: disks,
            Networks: networks,
            Gpus: gpus,
            Boot: boot,
            DetectedAt: DateTime.UtcNow
        );
    }

    // ---------- System ----------

    private async Task<SystemInfo> DetectSystemAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            string Read(string name) =>
                (CommandRunner.ReadFile($"/sys/class/dmi/id/{name}") ?? "Unknown").Trim();

            var manufacturer = Read("sys_vendor");
            var model = Read("product_name");
            var serial = Read("product_serial");
            var bios = Read("bios_version");

            // dmidecode is more reliable but requires root; ignore failures.
            if (manufacturer == "Unknown")
            {
                var output = await CommandRunner.RunAsync("dmidecode", "-s system-manufacturer", 2000);
                if (!string.IsNullOrWhiteSpace(output)) manufacturer = output.Trim();
            }

            return new SystemInfo(manufacturer, model, serial, bios);
        }

        return new SystemInfo(
            Manufacturer: "Generic",
            Model: RuntimeInformation.OSDescription,
            SerialNumber: "N/A",
            BiosVersion: "N/A"
        );
    }

    // ---------- CPU ----------

    private async Task<CpuInfo> DetectCpuAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            var content = await CommandRunner.ReadFileAsync("/proc/cpuinfo");
            if (string.IsNullOrEmpty(content))
            {
                return new CpuInfo("Unknown", Environment.ProcessorCount, Environment.ProcessorCount, 0, RuntimeInformation.OSArchitecture.ToString());
            }

            var model = "Unknown";
            double speedGhz = 0;
            int logical = 0;
            var physicalIds = new HashSet<string>();
            var coreIds = new HashSet<string>();

            string? currentPhysical = null;
            foreach (var rawLine in content.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                {
                    currentPhysical = null;
                    continue;
                }

                var idx = line.IndexOf(':');
                if (idx < 0) continue;
                var key = line.Substring(0, idx).Trim();
                var value = line.Substring(idx + 1).Trim();

                switch (key)
                {
                    case "model name":
                        if (model == "Unknown") model = value;
                        break;
                    case "cpu MHz":
                        if (speedGhz == 0 &&
                            double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
                        {
                            speedGhz = Math.Round(mhz / 1000.0, 2);
                        }
                        break;
                    case "processor":
                        logical++;
                        break;
                    case "physical id":
                        currentPhysical = value;
                        physicalIds.Add(value);
                        break;
                    case "core id":
                        if (currentPhysical != null)
                            coreIds.Add($"{currentPhysical}:{value}");
                        else
                            coreIds.Add(value);
                        break;
                }
            }

            if (logical == 0) logical = Environment.ProcessorCount;
            var physical = coreIds.Count > 0 ? coreIds.Count : Math.Max(1, logical);

            return new CpuInfo(
                Model: model,
                PhysicalCores: physical,
                LogicalCores: logical,
                SpeedGhz: speedGhz,
                Architecture: RuntimeInformation.OSArchitecture.ToString()
            );
        }

        return new CpuInfo(
            Model: $"Generic CPU ({RuntimeInformation.OSArchitecture})",
            PhysicalCores: Environment.ProcessorCount,
            LogicalCores: Environment.ProcessorCount,
            SpeedGhz: 0,
            Architecture: RuntimeInformation.OSArchitecture.ToString()
        );
    }

    // ---------- Memory ----------

    private async Task<MemoryInfo> DetectMemoryAsync()
    {
        if (OperatingSystem.IsLinux())
        {
            var content = await CommandRunner.ReadFileAsync("/proc/meminfo");
            if (string.IsNullOrEmpty(content))
                return new MemoryInfo(0, 0, 0);

            long total = 0;
            long available = 0;

            foreach (var rawLine in content.Split('\n'))
            {
                if (rawLine.StartsWith("MemTotal:", StringComparison.Ordinal))
                    total = ParseMemKb(rawLine);
                else if (rawLine.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    available = ParseMemKb(rawLine);
            }

            int slots = 0;
            // dmidecode --type memory provides physical slot count
            var dmi = await CommandRunner.RunAsync("dmidecode", "-t memory", 2000);
            if (!string.IsNullOrWhiteSpace(dmi))
            {
                foreach (var line in dmi.Split('\n'))
                {
                    if (line.TrimStart().StartsWith("Locator:", StringComparison.Ordinal) &&
                        !line.Contains("Bank", StringComparison.OrdinalIgnoreCase))
                    {
                        slots++;
                    }
                }
            }

            return new MemoryInfo(total, available, slots);
        }

        // Windows fallback - GC info gives a rough cap
        var gcInfo = GC.GetGCMemoryInfo();
        return new MemoryInfo(
            TotalBytes: gcInfo.TotalAvailableMemoryBytes,
            AvailableBytes: gcInfo.TotalAvailableMemoryBytes - GC.GetTotalMemory(false),
            SlotCount: 0
        );
    }

    private static long ParseMemKb(string line)
    {
        // e.g. "MemTotal:       16384012 kB"
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
            return kb * 1024L;
        return 0;
    }

    // ---------- Disks ----------

    private async Task<List<DiskInfo>> DetectDisksAsync()
    {
        var result = new List<DiskInfo>();

        if (OperatingSystem.IsLinux())
        {
            const string sysBlock = "/sys/block";
            if (!CommandRunner.DirectoryExists(sysBlock)) return result;

            string[] entries;
            try { entries = Directory.GetDirectories(sysBlock); }
            catch { return result; }

            foreach (var entry in entries)
            {
                var device = Path.GetFileName(entry);

                // Skip loop, ram, dm, sr (cdrom) devices
                if (device.StartsWith("loop") || device.StartsWith("ram") ||
                    device.StartsWith("dm-") || device.StartsWith("sr"))
                    continue;

                long sizeBytes = 0;
                var sizeStr = CommandRunner.ReadFile(Path.Combine(entry, "size"))?.Trim();
                if (long.TryParse(sizeStr, out var sectors))
                    sizeBytes = sectors * 512L;

                var model = (CommandRunner.ReadFile(Path.Combine(entry, "device", "model")) ?? "Unknown").Trim();
                var serial = (CommandRunner.ReadFile(Path.Combine(entry, "device", "serial")) ?? "").Trim();
                var rotational = (CommandRunner.ReadFile(Path.Combine(entry, "queue", "rotational")) ?? "1").Trim() == "1";

                var type = device switch
                {
                    var d when d.StartsWith("nvme") => "NVMe",
                    var d when d.StartsWith("mmcblk") => "eMMC",
                    _ => rotational ? "HDD" : "SSD"
                };

                // Best-effort SMART query (smartctl is optional)
                string? smart = null;
                var smartOut = await CommandRunner.RunAsync("smartctl", $"-H /dev/{device}", 1500);
                if (!string.IsNullOrWhiteSpace(smartOut))
                {
                    foreach (var line in smartOut.Split('\n'))
                    {
                        if (line.Contains("overall-health", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("SMART Health Status", StringComparison.OrdinalIgnoreCase))
                        {
                            var idx = line.IndexOf(':');
                            if (idx >= 0)
                            {
                                smart = line.Substring(idx + 1).Trim();
                                break;
                            }
                        }
                    }
                }

                result.Add(new DiskInfo(
                    Device: $"/dev/{device}",
                    Model: model,
                    Serial: serial,
                    SizeBytes: sizeBytes,
                    Type: type,
                    IsRotational: rotational,
                    SmartHealth: smart
                ));
            }
        }
        else
        {
            // Windows fallback - use DriveInfo for ready drives
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!d.IsReady) continue;
                    result.Add(new DiskInfo(
                        Device: d.Name,
                        Model: d.DriveType.ToString(),
                        Serial: "",
                        SizeBytes: d.TotalSize,
                        Type: d.DriveType.ToString(),
                        IsRotational: false,
                        SmartHealth: null
                    ));
                }
            }
            catch { }
        }

        return result;
    }

    // ---------- Networks ----------

    private async Task<List<NetworkInfo>> DetectNetworksAsync()
    {
        var result = new List<NetworkInfo>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var name = nic.Name;
                var mac = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes()
                    .Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

                string? ip = null;
                try
                {
                    var props = nic.GetIPProperties();
                    var ipv4 = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                    ip = ipv4?.Address.ToString();
                }
                catch { }

                var speedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000L : 0;
                var connected = nic.OperationalStatus == OperationalStatus.Up;

                result.Add(new NetworkInfo(
                    Interface: name,
                    MacAddress: string.IsNullOrEmpty(mac) ? "00:00:00:00:00:00" : mac,
                    IpAddress: ip,
                    SpeedMbps: speedMbps,
                    IsConnected: connected
                ));
            }
        }
        catch { }

        // On Linux supplement with /sys/class/net for the speed/operstate when .NET reports 0
        if (OperatingSystem.IsLinux())
        {
            const string sysNet = "/sys/class/net";
            if (CommandRunner.DirectoryExists(sysNet))
            {
                try
                {
                    foreach (var entry in Directory.GetDirectories(sysNet))
                    {
                        var name = Path.GetFileName(entry);
                        if (name == "lo") continue;

                        var existing = result.FirstOrDefault(n => n.Interface == name);
                        if (existing == null) continue;

                        long speed = existing.SpeedMbps;
                        if (speed == 0)
                        {
                            var s = CommandRunner.ReadFile(Path.Combine(entry, "speed"))?.Trim();
                            if (long.TryParse(s, out var sp) && sp > 0) speed = sp;
                        }

                        var operState = (CommandRunner.ReadFile(Path.Combine(entry, "operstate")) ?? "").Trim();
                        var connected = existing.IsConnected || operState == "up";

                        result.Remove(existing);
                        result.Add(existing with { SpeedMbps = speed, IsConnected = connected });
                    }
                }
                catch { }
            }
        }

        await Task.CompletedTask;
        return result;
    }

    // ---------- GPUs ----------

    private async Task<List<GpuInfo>> DetectGpusAsync()
    {
        var result = new List<GpuInfo>();

        if (OperatingSystem.IsLinux())
        {
            var output = await CommandRunner.RunAsync("lspci", "-mm -nn", 2000);
            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("VGA", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("3D controller", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Display controller", StringComparison.OrdinalIgnoreCase))
                    {
                        // Format: 00:02.0 "VGA compatible controller" "Intel Corporation" "UHD Graphics 630"
                        var parts = line.Split('"');
                        var vendor = parts.Length > 3 ? parts[3] : "Unknown";
                        var model = parts.Length > 5 ? parts[5] : "Unknown";
                        result.Add(new GpuInfo(vendor, model, null));
                    }
                }
            }
        }

        return result;
    }

    // ---------- Boot ----------

    private BootInfo DetectBootInfo()
    {
        if (OperatingSystem.IsLinux())
        {
            var isUefi = CommandRunner.DirectoryExists("/sys/firmware/efi");
            var mode = isUefi ? "UEFI" : "BIOS";
            var loader = "Unknown";

            if (isUefi)
            {
                // efibootmgr would tell us the active loader; cheap fallback
                loader = "EFI";
            }

            // Secure boot byte from EFI vars; ignore failures.
            var secureBoot = false;
            try
            {
                var efiVar = Directory.GetFiles("/sys/firmware/efi/efivars", "SecureBoot-*", SearchOption.TopDirectoryOnly);
                if (efiVar.Length > 0)
                {
                    var bytes = File.ReadAllBytes(efiVar[0]);
                    if (bytes.Length >= 5) secureBoot = bytes[4] == 1;
                }
            }
            catch { }

            return new BootInfo(mode, loader, secureBoot);
        }

        return new BootInfo("Unknown", "Unknown", false);
    }

    // ---------- helpers ----------

    private static string SafeHostname()
    {
        try { return Environment.MachineName; }
        catch { return "unknown-host"; }
    }

    private static async Task<T> SafeAsync<T>(Func<Task<T>> fn, T fallback)
    {
        try { return await fn(); }
        catch { return fallback; }
    }

    private static async Task<List<T>> SafeListAsync<T>(Func<Task<List<T>>> fn)
    {
        try { return await fn(); }
        catch { return new List<T>(); }
    }

    private static T SafeSync<T>(Func<T> fn, T fallback)
    {
        try { return fn(); }
        catch { return fallback; }
    }
}

