using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IFileAttachmentRepository
{
    Task<FileAttachment?> GetByIdAsync(long id);
    Task AddAsync(FileAttachment attachment);
    Task SaveChangesAsync();
}
