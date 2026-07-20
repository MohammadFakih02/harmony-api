using Harmony.Domain.Domain.Entities;

namespace Harmony.Domain.Interfaces.Repositories;

public interface IFileAttachmentRepository
{
    Task<FileAttachment?> GetByIdAsync(long id);

    /// <summary>All rows matching the given ids — missing ids are silently absent.</summary>
    Task<List<FileAttachment>> GetByIdsAsync(IReadOnlyCollection<long> ids);

    Task AddAsync(FileAttachment attachment);

    /// <summary>
    /// Unconfirmed (pending) rows created before <paramref name="cutoffUnixMs"/> — the orphans
    /// a presign created but a confirm never finalized. Capped by <paramref name="limit"/> so one
    /// sweep can't load an unbounded backlog.
    /// </summary>
    Task<List<FileAttachment>> GetUnconfirmedOlderThanAsync(long cutoffUnixMs, int limit = 500);

    void RemoveRange(IEnumerable<FileAttachment> attachments);

    Task SaveChangesAsync();
}
