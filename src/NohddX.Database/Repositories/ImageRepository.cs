using Microsoft.EntityFrameworkCore;
using NohddX.Core.Interfaces;
using NohddX.Core.Models;

namespace NohddX.Database.Repositories;

public class ImageRepository : IImageRepository
{
    private readonly NohddxDbContext _db;

    public ImageRepository(NohddxDbContext db)
    {
        _db = db;
    }

    public async Task<BootImage?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.Images
            .Include(i => i.Assignments)
            .Include(i => i.Snapshots)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<IReadOnlyList<BootImage>> GetAllAsync(CancellationToken ct = default)
    {
        return await _db.Images
            .Include(i => i.Assignments)
            .Include(i => i.Snapshots)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
    }

    public async Task<BootImage?> GetDefaultAsync(CancellationToken ct = default)
    {
        return await _db.Images
            .Include(i => i.Assignments)
            .Include(i => i.Snapshots)
            .FirstOrDefaultAsync(i => i.IsDefault, ct);
    }

    public async Task<BootImage> AddAsync(BootImage image, CancellationToken ct = default)
    {
        _db.Images.Add(image);
        await _db.SaveChangesAsync(ct);
        return image;
    }

    public async Task UpdateAsync(BootImage image, CancellationToken ct = default)
    {
        _db.Images.Update(image);
        await _db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var image = await _db.Images.FindAsync(new object[] { id }, ct);
        if (image is not null)
        {
            _db.Images.Remove(image);
            await _db.SaveChangesAsync(ct);
        }
    }
}
