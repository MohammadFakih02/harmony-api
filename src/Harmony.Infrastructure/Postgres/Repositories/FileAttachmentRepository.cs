using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;

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

    public async Task SaveChangesAsync() => await _db.SaveChangesAsync();
}
