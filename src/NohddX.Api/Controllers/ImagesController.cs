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
public class ImagesController : ControllerBase
{
    private readonly IImageRepository _imageRepo;
    private readonly ICowStorageEngine _cowStorage;

    public ImagesController(
        IImageRepository imageRepo,
        ICowStorageEngine cowStorage)
    {
        _imageRepo = imageRepo;
        _cowStorage = cowStorage;
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

        await _imageRepo.DeleteAsync(id, ct);
        return NoContent();
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
