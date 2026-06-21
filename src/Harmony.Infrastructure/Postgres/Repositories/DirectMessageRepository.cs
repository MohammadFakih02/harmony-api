using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Harmony.Infrastructure.Postgres.Repositories;

public class DirectMessageRepository : IDirectMessageRepository
{
    private const string DmChannelType = "dm";

    private readonly HarmonyDbContext _db;

    public DirectMessageRepository(HarmonyDbContext db)
    {
        _db = db;
    }

    public async Task<long?> GetSharedChannelIdAsync(long userA, long userB)
    {
        // A 1:1 DM is the channel where both users are participants. Joining the membership
        // table to itself on ChannelId finds it; restricting to "dm" channels keeps a future
        // group_dm with the same two members from masquerading as their 1:1.
        var query =
            from mine in _db.DirectMessageChannels
            where mine.UserId == userA
            join theirs in _db.DirectMessageChannels
                on mine.ChannelId equals theirs.ChannelId
            where theirs.UserId == userB
            join channel in _db.Channels on mine.ChannelId equals channel.Id
            where channel.Type == DmChannelType
            select mine.ChannelId;

        var id = await query.FirstOrDefaultAsync();
        return id == 0 ? null : id;
    }

    public async Task CreateAsync(long channelId, long userA, long userB, long createdAt)
    {
        await _db.Channels.AddAsync(
            new Channel
            {
                Id = channelId,
                GuildId = null,
                Name = string.Empty,
                Type = DmChannelType,
                Position = 0,
                CreatedAt = createdAt,
            }
        );

        await _db.DirectMessageChannels.AddRangeAsync(
            new DirectMessageChannel { ChannelId = channelId, UserId = userA, IsHidden = false, LastReadId = 0 },
            new DirectMessageChannel { ChannelId = channelId, UserId = userB, IsHidden = false, LastReadId = 0 }
        );

        await _db.SaveChangesAsync();
    }

    public async Task<bool> IsParticipantAsync(long channelId, long userId) =>
        await _db.DirectMessageChannels.AnyAsync(d =>
            d.ChannelId == channelId && d.UserId == userId
        );

    public async Task<List<long>> GetParticipantIdsAsync(long channelId) =>
        await _db
            .DirectMessageChannels.Where(d => d.ChannelId == channelId)
            .Select(d => d.UserId)
            .ToListAsync();

    public async Task<List<DmChannelView>> GetVisibleForUserAsync(long userId) =>
        await (
            from mine in _db.DirectMessageChannels
            where mine.UserId == userId && !mine.IsHidden
            join peer in _db.DirectMessageChannels on mine.ChannelId equals peer.ChannelId
            where peer.UserId != userId
            select new DmChannelView(mine.ChannelId, peer.UserId, mine.LastReadId, mine.IsHidden)
        ).ToListAsync();

    public async Task SetHiddenAsync(long channelId, long userId, bool hidden)
    {
        var row = await _db.DirectMessageChannels.FirstOrDefaultAsync(d =>
            d.ChannelId == channelId && d.UserId == userId
        );
        if (row is not null && row.IsHidden != hidden)
        {
            row.IsHidden = hidden;
            await _db.SaveChangesAsync();
        }
    }

    public async Task UnhideAllAsync(long channelId)
    {
        var rows = await _db
            .DirectMessageChannels.Where(d => d.ChannelId == channelId && d.IsHidden)
            .ToListAsync();
        if (rows.Count > 0)
        {
            foreach (var row in rows)
                row.IsHidden = false;
            await _db.SaveChangesAsync();
        }
    }
}
