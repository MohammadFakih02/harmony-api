using Harmony.Domain.Interfaces;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Hard-deletes soft-deleted guilds and channels once they've sat in Trash past the retention window
/// (§5.71 #5 — 30 days). Runs daily. A permanent delete from the UI is the same purge on demand; this
/// is the automatic backstop so nothing lingers forever.
///
/// Channels: publish <see cref="ChannelDeletedEvent"/> (consumers purge the Scylla partition + search
/// index), then EF-remove the row. Guilds: publish the event for each of the guild's text channels
/// first, then remove the guild (the Postgres cascade drops its channels/members/roles). A guild whose
/// window has elapsed is purged as a unit — its channels are NOT double-processed by the channel pass
/// (GetPurgeableAsync excludes channels whose guild is itself trashed).
///
/// The sweep body is <see cref="RunOnceAsync"/> for direct invocation in tests. Scope-per-cycle like
/// the other sweeps (the repositories are scoped; this service is a singleton). Registered only when
/// not in the test environment.
/// </summary>
public class TrashPurgeService : BackgroundService
{
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrashPurgeService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    // Retention window: a trashed guild/channel is recoverable for this long before the sweep hard-
    // deletes it. Kept generous so an accidental delete is always recoverable for weeks.
    private readonly TimeSpan _retention = TimeSpan.FromDays(30);

    public TrashPurgeService(
        IServiceScopeFactory scopeFactory,
        ILogger<TrashPurgeService> logger
    )
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TrashPurgeService background worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "An error occurred during the trash-purge sweep.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("TrashPurgeService background worker stopped.");
    }

    /// <summary>Runs a single sweep — purges due guilds then due channels. Returns the total count
    /// of entities hard-deleted. Safe to call directly (tests).</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(_retention).ToUnixTimeMilliseconds();

        using var scope = _scopeFactory.CreateScope();
        var guilds = scope.ServiceProvider.GetRequiredService<IGuildRepository>();
        var channels = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var publisher = scope.ServiceProvider.GetRequiredService<IMessagePublisher>();

        var purged = 0;

        // Guilds first: purging a guild also cleans up its channels, so doing this before the channel
        // pass avoids the channel pass touching a guild that's about to vanish wholesale.
        var dueGuilds = await guilds.GetPurgeableAsync(cutoff, BatchSize);
        foreach (var guild in dueGuilds)
        {
            var channelIds = await channels.GetTextChannelIdsByGuildIncludingDeletedAsync(guild.Id);
            await guilds.DeleteAsync(guild);
            await guilds.SaveChangesAsync();

            foreach (var channelId in channelIds)
                await publisher.PublishChannelDeletedAsync(
                    new ChannelDeletedEvent(channelId, guild.Id, DateTimeOffset.UtcNow),
                    ct
                );
            purged++;
        }

        // Individually-trashed channels whose guild is still live.
        var dueChannels = await channels.GetPurgeableAsync(cutoff, BatchSize);
        foreach (var channel in dueChannels)
        {
            await channels.DeleteAsync(channel);
            await channels.SaveChangesAsync();

            if (channel.GuildId is { } guildId)
                await publisher.PublishChannelDeletedAsync(
                    new ChannelDeletedEvent(channel.Id, guildId, DateTimeOffset.UtcNow),
                    ct
                );
            purged++;
        }

        if (purged > 0)
            _logger.LogInformation(
                "TrashPurgeService purged {GuildCount} guild(s) and {ChannelCount} channel(s).",
                dueGuilds.Count,
                dueChannels.Count
            );

        return purged;
    }
}
