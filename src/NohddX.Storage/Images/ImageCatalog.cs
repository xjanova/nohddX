using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NohddX.Core.Configuration;
using NohddX.Core.Models;

namespace NohddX.Storage.Images;

/// <summary>
/// Manages the catalog of available boot images.
/// Scans the base images directory for VHD files and maintains an in-memory catalog.
/// </summary>
public class ImageCatalog
{
    private readonly NohddxOptions _options;
    private readonly VhdImageManager _vhdManager;
    private readonly ILogger<ImageCatalog> _logger;
    private readonly List<BootImage> _images = new();
    private readonly object _lock = new();

    public ImageCatalog(
        IOptions<NohddxOptions> options,
        VhdImageManager vhdManager,
        ILogger<ImageCatalog> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _vhdManager = vhdManager ?? throw new ArgumentNullException(nameof(vhdManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scans the configured base images path for VHD files and populates the catalog.
    /// </summary>
    public async Task ScanImagesAsync()
    {
        _logger.LogInformation("Scanning for VHD images in {BasePath}...", _options.BaseImagesPath);

        var vhdFiles = await _vhdManager.GetImageListAsync(_options.BaseImagesPath);
        var scannedImages = new List<BootImage>();

        foreach (var vhdFile in vhdFiles)
        {
            bool isValid = await _vhdManager.ValidateImageAsync(vhdFile.FilePath);
            if (!isValid)
            {
                _logger.LogWarning("Skipping invalid VHD: {FilePath}", vhdFile.FilePath);
                continue;
            }

            OsType osType = vhdFile.OsHint switch
            {
                "Windows" => OsType.Windows,
                "Linux" => OsType.Linux,
                _ => OsType.Custom
            };

            var image = new BootImage
            {
                Id = GenerateDeterministicId(vhdFile.FilePath),
                Name = Path.GetFileNameWithoutExtension(vhdFile.FileName),
                OsType = osType,
                FilePath = vhdFile.FilePath,
                SizeBytes = vhdFile.FileSizeBytes,
                Status = ImageStatus.Active,
                CreatedAt = vhdFile.LastModified,
                UpdatedAt = DateTime.UtcNow
            };

            scannedImages.Add(image);
        }

        lock (_lock)
        {
            _images.Clear();
            _images.AddRange(scannedImages);
        }

        _logger.LogInformation("Image scan complete. Found {Count} valid VHD images.", scannedImages.Count);
    }

    /// <summary>
    /// Registers a new image in the catalog from a specific file path.
    /// </summary>
    public async Task RegisterImageAsync(string filePath, string name, OsType osType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Image file not found.", filePath);
        }

        bool isValid = await _vhdManager.ValidateImageAsync(filePath);
        if (!isValid)
        {
            throw new InvalidOperationException($"VHD file at '{filePath}' is not a valid VHD image.");
        }

        var (sizeBytes, _, _) = await _vhdManager.GetImageInfoAsync(filePath);

        var image = new BootImage
        {
            Id = Guid.NewGuid(),
            Name = name,
            OsType = osType,
            FilePath = filePath,
            SizeBytes = sizeBytes,
            Status = ImageStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            // Replace if an image with the same file path already exists
            int existingIndex = _images.FindIndex(i =>
                string.Equals(i.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            if (existingIndex >= 0)
            {
                _images[existingIndex] = image;
                _logger.LogInformation("Updated existing image registration: {Name} at {FilePath}", name, filePath);
            }
            else
            {
                _images.Add(image);
                _logger.LogInformation("Registered new image: {Name} at {FilePath}", name, filePath);
            }
        }
    }

    /// <summary>
    /// Returns a read-only snapshot of the current image catalog.
    /// </summary>
    public IReadOnlyList<BootImage> GetAvailableImages()
    {
        lock (_lock)
        {
            return _images.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Generates a deterministic GUID from a file path so the same file always gets the same ID.
    /// </summary>
    private static Guid GenerateDeterministicId(string filePath)
    {
        string normalized = filePath.Replace('\\', '/').ToLowerInvariant();
        byte[] hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(normalized));
        return new Guid(hash);
    }
}
