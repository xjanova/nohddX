using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class StorageController : ControllerBase
{
    private readonly IStoragePoolRepository _poolRepo;

    public StorageController(IStoragePoolRepository poolRepo)
    {
        _poolRepo = poolRepo;
    }

    [HttpGet("pools")]
    [ProducesResponseType(typeof(IEnumerable<StoragePoolResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetPools(CancellationToken ct = default)
    {
        var pools = await _poolRepo.GetAllAsync(ct);
        return Ok(pools.Select(MapToResponse));
    }

    [HttpGet("pools/{id:guid}")]
    [ProducesResponseType(typeof(StoragePoolResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPoolById(Guid id, CancellationToken ct = default)
    {
        var pool = await _poolRepo.GetByIdAsync(id, ct);
        if (pool is null)
            return NotFound();

        return Ok(MapToResponse(pool));
    }

    [HttpGet("health")]
    [ProducesResponseType(typeof(StorageHealthResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHealth(CancellationToken ct = default)
    {
        var pools = await _poolRepo.GetAllAsync(ct);

        var totalBytes = pools.Sum(p => p.TotalBytes);
        var usedBytes = pools.Sum(p => p.UsedBytes);
        var freeBytes = pools.Sum(p => p.FreeBytes);
        var usagePercent = totalBytes > 0 ? (double)usedBytes / totalBytes * 100.0 : 0.0;
        var isHealthy = pools.All(p => p.RaidStatus == RaidStatus.Healthy || p.RaidStatus == RaidStatus.Unknown);

        var response = new StorageHealthResponse(
            IsHealthy: isHealthy,
            TotalBytes: totalBytes,
            UsedBytes: usedBytes,
            FreeBytes: freeBytes,
            UsagePercent: Math.Round(usagePercent, 2),
            Pools: pools.Select(MapToResponse).ToList());

        return Ok(response);
    }

    /// <summary>
    /// Lists the physical drives the server sees, with free-space and a
    /// derived health bucket. Uses <see cref="DriveInfo"/> so the implementation
    /// is portable across Windows and Linux without WMI / sysfs shims. SMART
    /// temperature/error-counter data is intentionally out of scope here —
    /// that's a future per-platform enhancement.
    /// </summary>
    [HttpGet("disks")]
    [ProducesResponseType(typeof(IEnumerable<DiskInfoResponse>), StatusCodes.Status200OK)]
    public IActionResult GetDisks()
    {
        var rows = new List<DiskInfoResponse>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                bool ready = d.IsReady;
                long total = ready ? d.TotalSize : 0;
                long free = ready ? d.AvailableFreeSpace : 0;
                long used = total - free;
                double pct = total > 0 ? used * 100.0 / total : 0.0;

                // Bucket: Good < 75%, Warning 75-90%, Failing > 90%. The
                // names match what the UI's status-LED triggers look for.
                string health = !ready
                    ? "Offline"
                    : pct >= 90 ? "Failing"
                    : pct >= 75 ? "Warning"
                    : "Good";

                rows.Add(new DiskInfoResponse(
                    Device: d.Name,
                    VolumeLabel: ready ? d.VolumeLabel : null,
                    DriveFormat: ready ? d.DriveFormat : "—",
                    DriveType: d.DriveType.ToString(),
                    TotalBytes: total,
                    UsedBytes: used,
                    FreeBytes: free,
                    UsagePercent: Math.Round(pct, 1),
                    IsReady: ready,
                    Health: health));
            }
            catch
            {
                // DriveInfo can throw for inaccessible volumes (CD-ROM with
                // no media, network shares that timeout). Skip rather than
                // 500 the entire request.
            }
        }

        // Show fixed drives first so the operator's actual storage isn't
        // buried below CD-ROM and removable USB entries.
        rows.Sort((a, b) =>
        {
            int rank(DiskInfoResponse r) => r.DriveType == "Fixed" ? 0 : r.DriveType == "Network" ? 1 : 2;
            int cmp = rank(a).CompareTo(rank(b));
            return cmp != 0 ? cmp : string.Compare(a.Device, b.Device, StringComparison.Ordinal);
        });

        return Ok(rows);
    }

    private static StoragePoolResponse MapToResponse(StoragePool p)
    {
        return new StoragePoolResponse(
            Id: p.Id,
            Name: p.Name,
            Path: p.Path,
            TotalBytes: p.TotalBytes,
            UsedBytes: p.UsedBytes,
            FreeBytes: p.FreeBytes,
            RaidLevel: p.RaidLevel,
            RaidStatus: p.RaidStatus,
            DiskCount: p.DiskCount,
            IsDefault: p.IsDefault,
            CreatedAt: p.CreatedAt,
            UpdatedAt: p.UpdatedAt);
    }
}
