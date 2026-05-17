using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using NohddX.Api.Auth;
using NohddX.Api.DTOs;
using NohddX.Core.Configuration;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = NohddxAuthSchemes.AdminPolicy)]
[EnableRateLimiting(ServiceCollectionExtensions.AdminRateLimitPolicy)]
public class ImagesController : ControllerBase
{
    private readonly IImageRepository _imageRepo;
    private readonly ICowStorageEngine _cowStorage;
    private readonly AuditLogger _audit;
    private readonly NohddxOptions _options;
    private readonly ILogger<ImagesController> _logger;

    public ImagesController(
        IImageRepository imageRepo,
        ICowStorageEngine cowStorage,
        AuditLogger audit,
        IOptions<NohddxOptions> options,
        ILogger<ImagesController> logger)
    {
        _imageRepo = imageRepo;
        _cowStorage = cowStorage;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ImageResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken ct = default)
    {
        var images = await _imageRepo.GetAllAsync(ct);
        return Ok(images.Select(MapToResponse));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ImageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null)
            return NotFound();

        return Ok(MapToResponse(image));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ImageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(
        [FromBody] CreateImageRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Image name is required.");
        if (string.IsNullOrWhiteSpace(request.FilePath))
            return BadRequest("File path is required.");

        var image = new BootImage
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            OsType = request.OsType,
            Version = request.Version,
            FilePath = request.FilePath,
            SizeBytes = request.SizeBytes,
            Checksum = request.Checksum,
            Status = ImageStatus.Active,
            IsDefault = request.IsDefault,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var created = await _imageRepo.AddAsync(image, ct);
        await _audit.RecordAsync("image.create", true, "image", created.Id.ToString(),
            $"name={created.Name} path={created.FilePath} size={created.SizeBytes}", ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToResponse(created));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ImageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateImageRequest request,
        CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null)
            return NotFound();

        if (request.Name is not null)
            image.Name = request.Name;
        if (request.Version is not null)
            image.Version = request.Version;
        if (request.FilePath is not null)
            image.FilePath = request.FilePath;
        if (request.SizeBytes.HasValue)
            image.SizeBytes = request.SizeBytes.Value;
        if (request.Checksum is not null)
            image.Checksum = request.Checksum;
        if (request.Status.HasValue)
            image.Status = request.Status.Value;
        if (request.IsDefault.HasValue)
            image.IsDefault = request.IsDefault.Value;

        image.UpdatedAt = DateTime.UtcNow;
        await _imageRepo.UpdateAsync(image, ct);
        await _audit.RecordAsync("image.update", true, "image", image.Id.ToString(),
            $"name={image.Name} path={image.FilePath}", ct);

        return Ok(MapToResponse(image));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null)
            return NotFound();

        // Remove the DB row first; if the disk delete fails afterward the
        // operator only loses some space, not data integrity.
        await _imageRepo.DeleteAsync(id, ct);

        // Only unlink files that live under our configured storage roots —
        // refuses to touch anything an operator pasted in from outside the
        // sandbox. Best-effort: log + audit even on failure so the operator
        // sees the orphaned file.
        bool fileDeleted = false;
        string? deleteError = null;
        if (!string.IsNullOrWhiteSpace(image.FilePath) && IsUnderStorageRoot(image.FilePath))
        {
            try
            {
                if (System.IO.File.Exists(image.FilePath))
                {
                    System.IO.File.Delete(image.FilePath);
                    fileDeleted = true;
                }
            }
            catch (Exception ex)
            {
                deleteError = ex.Message;
                _logger.LogWarning(ex, "Failed to delete image file {Path}", image.FilePath);
            }
        }

        await _audit.RecordAsync("image.delete", true, "image", id.ToString(),
            $"name={image.Name} path={image.FilePath} fileDeleted={fileDeleted}" +
            (deleteError is null ? "" : $" error={deleteError}"), ct);
        return NoContent();
    }

    /// <summary>
    /// Streaming upload of a raw or VHD image. Operator picks a local file in
    /// the WPF console and POSTs its bytes; this writes them to a generated
    /// path under <see cref="NohddxOptions.BaseImagesPath"/> while computing
    /// a SHA-256 checksum, then inserts a <see cref="BootImage"/> row. The
    /// upload path is what lets the operator add a new boot target without
    /// shell access to the server.
    /// </summary>
    [HttpPost("upload")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue)]
    [ProducesResponseType(typeof(ImageResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(
        [FromQuery] string name,
        [FromQuery] OsType osType = OsType.Custom,
        [FromQuery] string version = "1.0",
        [FromQuery] bool isDefault = false,
        [FromQuery] string? extension = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Image name is required.");

        Directory.CreateDirectory(_options.BaseImagesPath);

        // Sanitise extension — never let the caller drop a path separator
        // into our storage root. Default to .vhd which is what we serve.
        var ext = string.IsNullOrWhiteSpace(extension) ? "vhd" : extension.Trim('.', '/', '\\', ' ');
        if (ext.Length > 8 || ext.Any(c => !char.IsLetterOrDigit(c)))
            ext = "vhd";

        var safeName = string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'));
        if (string.IsNullOrEmpty(safeName)) safeName = "image";
        var fileName = $"{safeName}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.{ext}";
        var filePath = Path.Combine(_options.BaseImagesPath, fileName);

        long bytesWritten;
        string checksumHex;
        try
        {
            await using var dest = new FileStream(
                filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1 << 20, useAsync: true);
            using var sha = SHA256.Create();

            var buffer = new byte[1 << 20]; // 1 MiB
            int read;
            long total = 0;
            while ((read = await Request.Body.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);
                total += read;
            }
            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            bytesWritten = total;
            checksumHex = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        }
        catch
        {
            // Clean up the partial file so the storage root doesn't fill up
            // with zombie uploads after disconnects.
            try { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); } catch { }
            throw;
        }

        if (bytesWritten == 0)
        {
            try { System.IO.File.Delete(filePath); } catch { }
            return BadRequest("Upload body was empty.");
        }

        var image = new BootImage
        {
            Id = Guid.NewGuid(),
            Name = name,
            OsType = osType,
            Version = version,
            FilePath = filePath,
            SizeBytes = bytesWritten,
            Checksum = "sha256:" + checksumHex,
            Status = ImageStatus.Active,
            IsDefault = isDefault,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var created = await _imageRepo.AddAsync(image, ct);

        await _audit.RecordAsync("image.upload", true, "image", created.Id.ToString(),
            $"name={name} bytes={bytesWritten} sha256={checksumHex[..16]}…", ct);

        return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToResponse(created));
    }

    /// <summary>
    /// Refuses paths that aren't inside the configured storage roots — keeps
    /// DELETE from removing arbitrary files an admin happened to register.
    /// </summary>
    private bool IsUnderStorageRoot(string path)
    {
        try
        {
            var resolved = Path.GetFullPath(path);
            string[] roots =
            {
                Path.GetFullPath(_options.BaseImagesPath),
                Path.GetFullPath(_options.StorageBasePath),
            };
            return roots.Any(r => resolved.StartsWith(r, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    [HttpPost("{id:guid}/snapshot")]
    [ProducesResponseType(typeof(SnapshotResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateSnapshot(
        Guid id,
        [FromBody] CreateSnapshotRequest request,
        CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Snapshot name is required.");

        var snapshot = new ImageSnapshot
        {
            Id = Guid.NewGuid(),
            ImageId = id,
            Name = request.Name,
            Description = request.Description,
            SnapshotPath = Path.Combine("snapshots", $"{id}_{request.Name}_{DateTime.UtcNow:yyyyMMddHHmmss}"),
            SizeBytes = 0,
            CreatedAt = DateTime.UtcNow
        };

        image.Snapshots.Add(snapshot);
        await _imageRepo.UpdateAsync(image, ct);
        await _audit.RecordAsync("image.snapshot", true, "image", id.ToString(),
            $"snapshotId={snapshot.Id} name={snapshot.Name}", ct);

        var response = new SnapshotResponse(
            Id: snapshot.Id,
            ImageId: snapshot.ImageId,
            Name: snapshot.Name,
            Description: snapshot.Description,
            SnapshotPath: snapshot.SnapshotPath,
            SizeBytes: snapshot.SizeBytes,
            CreatedAt: snapshot.CreatedAt);

        return CreatedAtAction(nameof(GetSnapshots), new { id }, response);
    }

    [HttpGet("{id:guid}/snapshots")]
    [ProducesResponseType(typeof(IEnumerable<SnapshotResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSnapshots(Guid id, CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null)
            return NotFound();

        var snapshots = image.Snapshots.Select(s => new SnapshotResponse(
            Id: s.Id,
            ImageId: s.ImageId,
            Name: s.Name,
            Description: s.Description,
            SnapshotPath: s.SnapshotPath,
            SizeBytes: s.SizeBytes,
            CreatedAt: s.CreatedAt));

        return Ok(snapshots);
    }

    /// <summary>
    /// Streams the underlying image file (VHD / raw) to the caller. The
    /// bootstrap agent uses this for "Persistent" install mode where the
    /// image is written byte-for-byte to a local disk.
    /// </summary>
    [HttpGet("{id:guid}/download")]
    [Authorize(Policy = NohddxAuthSchemes.AgentPolicy)] // both agents and admins
    [EnableRateLimiting(ServiceCollectionExtensions.AgentRateLimitPolicy)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct = default)
    {
        var image = await _imageRepo.GetByIdAsync(id, ct);
        if (image is null) return NotFound();

        if (string.IsNullOrWhiteSpace(image.FilePath) || !System.IO.File.Exists(image.FilePath))
            return NotFound($"Image file '{image.FilePath}' not found on disk.");

        var fileName = Path.GetFileName(image.FilePath);
        var stream = new FileStream(
            image.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1 << 20,
            useAsync: true);

        Response.Headers["Accept-Ranges"] = "bytes";
        return File(stream, "application/octet-stream", fileName, enableRangeProcessing: true);
    }

    private static ImageResponse MapToResponse(BootImage i)
    {
        return new ImageResponse(
            Id: i.Id,
            Name: i.Name,
            OsType: i.OsType,
            Version: i.Version,
            FilePath: i.FilePath,
            SizeBytes: i.SizeBytes,
            Checksum: i.Checksum,
            Status: i.Status,
            IsDefault: i.IsDefault,
            AssignmentCount: i.Assignments.Count,
            SnapshotCount: i.Snapshots.Count,
            CreatedAt: i.CreatedAt,
            UpdatedAt: i.UpdatedAt);
    }
}
