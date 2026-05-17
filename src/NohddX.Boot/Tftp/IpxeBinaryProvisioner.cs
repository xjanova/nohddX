using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;

namespace NohddX.Boot.Tftp;

/// <summary>
/// Ensures that the TFTP root directory (NohddX:Tftp:IpxeBinaryPath) contains
/// the iPXE binaries that PXE clients request. Out of the box the
/// <c>tools/ipxe</c> directory ships empty, so without this service every
/// TFTP read request would return "file not found" and the boot would stall.
///
/// Strategy: at server start, check the configured path for the standard
/// iPXE binary set. Any missing file is downloaded from boot.ipxe.org with
/// a short timeout. If the network is unavailable we log loudly and let the
/// operator stage them manually (or by running tools/iso-builder).
/// </summary>
public sealed class IpxeBinaryProvisioner : IHostedService
{
    private const string IpxeBaseUrl = "https://boot.ipxe.org";

    private static readonly string[] RequiredBinaries =
    {
        "ipxe.efi",
        "ipxe.lkrn",
        "undionly.kpxe",
        "snponly.efi"
    };

    private readonly NohddxOptions _options;
    private readonly ILogger<IpxeBinaryProvisioner> _logger;

    public IpxeBinaryProvisioner(
        IOptions<NohddxOptions> options,
        ILogger<IpxeBinaryProvisioner> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Tftp.Enabled)
            return;

        var path = Path.GetFullPath(_options.Tftp.IpxeBinaryPath);
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot create TFTP root {Path}; PXE boot will fail", path);
            return;
        }

        var missing = RequiredBinaries
            .Where(f => !FileExistsAndNonEmpty(Path.Combine(path, f)))
            .ToList();

        if (missing.Count == 0)
        {
            _logger.LogInformation("iPXE binaries present in {Path}: {Files}",
                path, string.Join(", ", RequiredBinaries));
            return;
        }

        _logger.LogInformation(
            "Downloading {Count} missing iPXE binaries into {Path}: {Files}",
            missing.Count, path, string.Join(", ", missing));

        using var http = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version20,
            Timeout = TimeSpan.FromSeconds(30)
        };

        foreach (var name in missing)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var url = $"{IpxeBaseUrl}/{name}";
            var dest = Path.Combine(path, name);
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                resp.EnsureSuccessStatusCode();

                var tmp = dest + ".part";
                await using (var fs = File.Create(tmp))
                {
                    await resp.Content.CopyToAsync(fs, cancellationToken);
                }
                File.Move(tmp, dest, overwrite: true);

                _logger.LogInformation("Fetched {Name} ({Kb} KB)", name, new FileInfo(dest).Length / 1024);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not fetch iPXE binary {Name} from {Url}. PXE clients that need this file " +
                    "will fail until you copy it manually to {Dest} (e.g. by running tools/iso-builder).",
                    name, url, dest);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static bool FileExistsAndNonEmpty(string path)
    {
        var fi = new FileInfo(path);
        return fi.Exists && fi.Length > 1024;
    }
}
