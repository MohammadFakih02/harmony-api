using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class FileAttachmentRepository : IFileAttachmentRepository
{
    private readonly HarmonyDbContext _db;

    public FileAttachmentRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<FileAttachment?> GetByIdAsync(long id) =>
        await _db.FileAttachments.FindAsync(id);

    public async Task AddAsync(FileAttachment attachment) =>
        await _db.FileAttachments.AddAsync(attachment);

    public async Task<List<FileAttachment>> GetUnconfirmedOlderThanAsync(
        long cutoffUnixMs,
        int limit = 500
    ) =>
        await _db
            .FileAttachments.Where(f => !f.IsConfirmed && f.CreatedAt < cutoffUnixMs)
            .OrderBy(f => f.CreatedAt)
            .Take(limit)
            .ToListAsync();

    public void RemoveRange(IEnumerable<FileAttachment> attachments) =>
        _db.FileAttachments.RemoveRange(attachments);

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
