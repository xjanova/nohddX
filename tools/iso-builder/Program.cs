using System.Net;
using System.Text;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Streams;
using NohddX.IsoBuilder;

return await new IsoBuilderApp().RunAsync(args);

namespace NohddX.IsoBuilder
{

internal sealed class IsoBuilderApp
{
    private const string IpxeBaseUrl = "https://boot.ipxe.org";
    private static readonly string[] DefaultBinaries = { "ipxe.efi", "ipxe.lkrn", "undionly.kpxe", "snponly.efi" };

    public async Task<int> RunAsync(string[] args)
    {
        try
        {
            var opts = BuildOptions.Parse(args);
            if (opts.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            Console.WriteLine("NoHddX USB image builder");
            Console.WriteLine("------------------------");
            Console.WriteLine($"Server URL : {opts.ServerUrl}");
            Console.WriteLine($"Output     : {opts.OutputPath}");
            Console.WriteLine($"Size       : {opts.SizeMb} MB");
            Console.WriteLine($"Cache dir  : {opts.CacheDir}");
            Console.WriteLine();

            await EnsureBinariesAsync(opts);

            var initScript = BuildInitScript(opts.ServerUrl);
            var agentConfig = BuildAgentConfig(opts.ServerUrl);
            var instructions = BuildBootInstructions(opts.ServerUrl);

            BuildImage(opts, initScript, agentConfig, instructions);

            Console.WriteLine();
            Console.WriteLine("Done.");
            Console.WriteLine($"Image written to: {Path.GetFullPath(opts.OutputPath)}");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine($"  1. Flash the image to a USB stick (Rufus / BalenaEtcher / dd)");
            Console.WriteLine($"  2. Boot the target machine from the USB stick (UEFI)");
            Console.WriteLine($"  3. At the iPXE prompt, press Ctrl+B and type:");
            Console.WriteLine($"       chain http://{TryGetIpFromUrl(opts.ServerUrl)}/api/boot/$" + "{mac:hexhyp}.ipxe");
            Console.WriteLine($"     (or set DHCP option 67 to boot.ipxe so iPXE auto-chains)");
            Console.WriteLine();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private async Task EnsureBinariesAsync(BuildOptions opts)
    {
        Directory.CreateDirectory(opts.CacheDir);
        Directory.CreateDirectory(opts.IpxeBinaryDir);

        using var http = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version20,
            Timeout = TimeSpan.FromMinutes(5)
        };

        foreach (var name in DefaultBinaries)
        {
            var cachedPath = Path.Combine(opts.CacheDir, name);
            if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 1024)
            {
                Console.WriteLine($"  cache hit : {name}");
                continue;
            }

            var url = $"{IpxeBaseUrl}/{name}";
            Console.Write($"  fetching  : {name} ... ");
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using var dst = File.Create(cachedPath);
                await resp.Content.CopyToAsync(dst);
                Console.WriteLine($"{new FileInfo(cachedPath).Length / 1024} KB");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAILED ({ex.Message})");
                if (name is "ipxe.efi" or "ipxe.lkrn")
                    throw new InvalidOperationException(
                        $"Could not download required iPXE binary '{name}'. " +
                        $"Check your internet connection or place a copy at '{cachedPath}' manually.");
            }

            // Mirror the binaries into tools/ipxe/ so the running server's TFTP service
            // can serve them out of the box.
            var mirror = Path.Combine(opts.IpxeBinaryDir, name);
            try
            {
                File.Copy(cachedPath, mirror, overwrite: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (warn) could not mirror {name} to {opts.IpxeBinaryDir}: {ex.Message}");
            }
        }

        Console.WriteLine();
    }

    private static string BuildInitScript(string serverUrl)
    {
        var trimmed = serverUrl.TrimEnd('/');
        var sb = new StringBuilder();
        sb.AppendLine("#!ipxe");
        sb.AppendLine("# NoHddX boot script - chainloads the per-MAC boot script from the server.");
        sb.AppendLine();
        sb.AppendLine("echo ============================================");
        sb.AppendLine("echo   NoHddX Diskless Boot");
        sb.AppendLine("echo   MAC:    ${mac}");
        sb.AppendLine("echo   IP:     ${ip}");
        sb.AppendLine($"echo   Server: {trimmed}");
        sb.AppendLine("echo ============================================");
        sb.AppendLine();
        sb.AppendLine("dhcp || goto noip");
        sb.AppendLine($"chain {trimmed}/api/boot/$" + "{mac:hexhyp}.ipxe || goto fail");
        sb.AppendLine("exit");
        sb.AppendLine();
        sb.AppendLine(":noip");
        sb.AppendLine("echo Failed to acquire an IP address via DHCP.");
        sb.AppendLine("shell");
        sb.AppendLine();
        sb.AppendLine(":fail");
        sb.AppendLine("echo Failed to chainload boot script. Press a key for an iPXE shell...");
        sb.AppendLine("shell");
        return sb.ToString();
    }

    private static string BuildAgentConfig(string serverUrl)
    {
        var trimmed = serverUrl.TrimEnd('/');
        return $$"""
        {
          "ServerUrl": "{{trimmed}}",
          "UseMdnsDiscovery": true,
          "PreferredMode": "Persistent",
          "AgentPort": 7000,
          "DiscoveryPort": 4012
        }
        """;
    }

    private static string BuildBootInstructions(string serverUrl)
    {
        var ip = TryGetIpFromUrl(serverUrl);
        return $$"""
        NoHddX bootable USB
        ===================

        Server URL: {{serverUrl}}

        Boot mode: UEFI
          The firmware will load /EFI/BOOT/BOOTX64.EFI which is iPXE.
          iPXE will run its embedded script and try DHCP, then prompt
          for a chain command. If your DHCP server is configured with
          option 67 = "boot.ipxe", iPXE will chainload from the NoHddX
          server automatically.

        Manual fallback (at the iPXE shell):
          chain http://{{ip}}/api/boot/${mac:hexhyp}.ipxe

        BIOS / Legacy:
          Boot the host with SYSLINUX or another bootloader and execute
          /ipxe.lkrn. Or chainload undionly.kpxe via PXE / TFTP.

        Customisation:
          - /init.ipxe              the chain script used by iPXE
          - /nohddx-agent.json      the agent's runtime config
          Edit both with any text editor on the USB drive after writing.
        """;
    }

    private static string TryGetIpFromUrl(string serverUrl)
    {
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var u))
            return u.IsDefaultPort ? u.Host : $"{u.Host}:{u.Port}";
        return serverUrl;
    }

    /// <summary>
    /// Builds the raw disk image: MBR with one FAT32 partition occupying the
    /// rest of the disk, and the iPXE binaries placed under /EFI/BOOT/.
    /// </summary>
    private void BuildImage(BuildOptions opts, string initScript, string agentConfig, string instructions)
    {
        var totalBytes = (long)opts.SizeMb * 1024 * 1024;

        Console.WriteLine($"Creating raw image ({opts.SizeMb} MB)...");
        var dir = Path.GetDirectoryName(Path.GetFullPath(opts.OutputPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var imageFs = new FileStream(opts.OutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        imageFs.SetLength(totalBytes);

        // MBR layout: one FAT32 partition starting at 1 MiB to leave room for
        // the boot sector, with type 0x0C (FAT32 LBA).
        const int reservedSectors = 2048; // 1 MiB at 512 bytes/sector
        const int sectorSize = 512;
        var sectorCount = totalBytes / sectorSize;
        var partitionSectors = sectorCount - reservedSectors;
        var partitionOffset = (long)reservedSectors * sectorSize;
        var partitionLength = partitionSectors * sectorSize;

        WriteMbr(imageFs, reservedSectors, partitionSectors);

        // Format the partition as FAT (DiscUtils picks FAT16/32 based on size).
        // For sizes >= ~512 MB this is FAT32. Smaller sizes still work because
        // UEFI firmware supports FAT16 on removable media.
        using (var partitionStream = new SubStream(imageFs, partitionOffset, partitionLength))
        {
            Console.WriteLine("Formatting partition (FAT)...");
            var geometry = Geometry.FromCapacity(partitionLength);
            using var fat = FatFileSystem.FormatPartition(
                partitionStream,
                "NOHDDX",
                geometry,
                0,
                (int)partitionSectors,
                32);

            Console.WriteLine("Copying iPXE binaries and config...");
            CreateDir(fat, "EFI");
            CreateDir(fat, "EFI\\BOOT");

            CopyHostFile(fat, Path.Combine(opts.CacheDir, "ipxe.efi"), "EFI\\BOOT\\BOOTX64.EFI");
            CopyHostFile(fat, Path.Combine(opts.CacheDir, "ipxe.efi"), "EFI\\BOOT\\BOOTIA32.EFI", optional: true);
            CopyHostFile(fat, Path.Combine(opts.CacheDir, "ipxe.lkrn"), "ipxe.lkrn", optional: true);
            CopyHostFile(fat, Path.Combine(opts.CacheDir, "undionly.kpxe"), "undionly.kpxe", optional: true);
            CopyHostFile(fat, Path.Combine(opts.CacheDir, "snponly.efi"), "snponly.efi", optional: true);

            WriteText(fat, "init.ipxe", initScript);
            WriteText(fat, "autoexec.ipxe", initScript);
            WriteText(fat, "nohddx-agent.json", agentConfig);
            WriteText(fat, "BOOT-INSTRUCTIONS.txt", instructions);
        }
    }

    private static void WriteMbr(Stream image, int firstSector, long sectorCount)
    {
        // Minimal MBR: signature + one primary partition entry of type 0x0C
        // (FAT32 LBA), bootable flag set so legacy BIOS firmware will try it.
        var mbr = new byte[512];

        // Partition entry at offset 446
        const int peOffset = 446;
        mbr[peOffset + 0] = 0x80;                                  // bootable
        mbr[peOffset + 1] = 0x01;                                  // CHS start head (filler)
        mbr[peOffset + 2] = 0x01;                                  // CHS start sector
        mbr[peOffset + 3] = 0x00;                                  // CHS start cylinder
        mbr[peOffset + 4] = 0x0C;                                  // type = FAT32 LBA
        mbr[peOffset + 5] = 0xFE;                                  // CHS end head (filler)
        mbr[peOffset + 6] = 0xFF;                                  // CHS end sector
        mbr[peOffset + 7] = 0xFF;                                  // CHS end cylinder
        WriteUInt32Le(mbr, peOffset + 8, (uint)firstSector);       // LBA first sector
        WriteUInt32Le(mbr, peOffset + 12, (uint)sectorCount);      // sector count

        // Boot signature
        mbr[510] = 0x55;
        mbr[511] = 0xAA;

        image.Position = 0;
        image.Write(mbr, 0, mbr.Length);
    }

    private static void WriteUInt32Le(byte[] buf, int off, uint v)
    {
        buf[off + 0] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)((v >> 8) & 0xFF);
        buf[off + 2] = (byte)((v >> 16) & 0xFF);
        buf[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void CreateDir(IFileSystem fs, string path)
    {
        if (!fs.DirectoryExists(path)) fs.CreateDirectory(path);
    }

    private static void CopyHostFile(IFileSystem fs, string hostPath, string fsPath, bool optional = false)
    {
        if (!File.Exists(hostPath))
        {
            if (optional)
            {
                Console.WriteLine($"  skip   : {fsPath} (source missing: {Path.GetFileName(hostPath)})");
                return;
            }
            throw new FileNotFoundException($"Required iPXE binary not found: {hostPath}");
        }

        var bytes = File.ReadAllBytes(hostPath);
        using var w = fs.OpenFile(fsPath, FileMode.Create, FileAccess.Write);
        w.Write(bytes, 0, bytes.Length);
        Console.WriteLine($"  add    : {fsPath} ({bytes.Length / 1024} KB)");
    }

    private static void WriteText(IFileSystem fs, string fsPath, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var w = fs.OpenFile(fsPath, FileMode.Create, FileAccess.Write);
        w.Write(bytes, 0, bytes.Length);
        Console.WriteLine($"  add    : {fsPath} ({bytes.Length} B)");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            NoHddX USB image builder

            Usage:
              nohddx-iso-builder --server-url <url> [options]

            Required:
              --server-url <url>    Base URL of the NoHddX server, e.g. http://192.168.1.10:8080

            Options:
              --output <file>       Output image path (default: nohddx-boot.img)
              --size-mb <n>         Image size in MB (default: 64, min 16)
              --cache <dir>         iPXE binary cache (default: ./cache)
              --ipxe-dir <dir>      Where to mirror binaries for the server's TFTP
                                    (default: ../ipxe relative to this binary)
              -h, --help            Show this help

            Example:
              nohddx-iso-builder --server-url http://192.168.1.10:8080 --output usb.img
            """);
    }
}

internal sealed class BuildOptions
{
    public bool ShowHelp { get; private set; }
    public string ServerUrl { get; private set; } = "";
    public string OutputPath { get; private set; } = "nohddx-boot.img";
    public int SizeMb { get; private set; } = 64;
    public string CacheDir { get; private set; } = "cache";
    public string IpxeBinaryDir { get; private set; } = Path.Combine("..", "ipxe");

    public static BuildOptions Parse(string[] args)
    {
        var o = new BuildOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server-url" when i + 1 < args.Length: o.ServerUrl = args[++i]; break;
                case "--output" when i + 1 < args.Length: o.OutputPath = args[++i]; break;
                case "--size-mb" when i + 1 < args.Length: o.SizeMb = Math.Max(16, int.Parse(args[++i])); break;
                case "--cache" when i + 1 < args.Length: o.CacheDir = args[++i]; break;
                case "--ipxe-dir" when i + 1 < args.Length: o.IpxeBinaryDir = args[++i]; break;
                case "-h":
                case "--help":
                    o.ShowHelp = true;
                    return o;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (string.IsNullOrWhiteSpace(o.ServerUrl) && !o.ShowHelp)
            throw new ArgumentException("--server-url is required (e.g. http://192.168.1.10:8080)");

        return o;
    }
}

}
