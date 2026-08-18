using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Enums;
using Microsoft.Extensions.Logging;
using Orleans;

namespace Harmony.API.Grains;

/// <summary>
/// The D4 <see cref="IPermissionService"/> implementation used outside the Test environment: a thin
/// adapter that forwards each call to the per-guild <see cref="IGuildGrain"/> (keyed by guildId),
/// which owns the in-memory permission snapshot. All five interface methods and every call site
/// (RoleService, GuildMemberService, ChannelOverridesController) are unchanged — the invalidation
/// calls simply become grain snapshot-drops. The Test environment keeps
/// <see cref="Harmony.Infrastructure.Services.PermissionService"/> (Redis-cached) because no Orleans
/// silo is co-hosted there.
///
/// <para><b>Failure posture — fail CLOSED on reads.</b> A grain-call failure (silo transient, or a
/// hydration exception the grain deliberately propagates) resolves to <em>deny</em> — 0 bits / an
/// empty granted list — never to a wrong grant. A transient blip therefore denies-and-retries rather
/// than leaking access, matching the security posture (NON-NEGOTIABLE #8: never over-trust). Writes
/// (invalidation) swallow failures like the presence adapter — a missed invalidation self-corrects on
/// the snapshot's next natural reload, and must never break the mutating request.</para>
/// </summary>
public sealed class GrainPermissionService : IPermissionService
{
    private readonly IGrainFactory _grains;
    private readonly ILogger<GrainPermissionService> _logger;

    public GrainPermissionService(IGrainFactory grains, ILogger<GrainPermissionService> logger)
    {
        _grains = grains;
        _logger = logger;
    }

    private IGuildGrain Grain(long guildId) => _grains.GetGrain<IGuildGrain>(guildId);

    public async Task<long> ResolveAsync(
        long userId,
        long guildId,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        try
        {
            return await Grain(guildId).ResolveBits(userId, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Permissions: ResolveBits failed for user {UserId} in guild {GuildId} — denying (fail-closed)",
                userId,
                guildId
            );
            return 0;
        }
    }

    public async Task<bool> HasAsync(
        long userId,
        long guildId,
        Permission permission,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        var bits = await ResolveAsync(userId, guildId, channelId, ct);
        return (bits & (long)permission) == (long)permission;
    }

    public async Task<List<long>> FilterByPermissionAsync(
        IReadOnlyList<long> userIds,
        long guildId,
        Permission permission,
        long? channelId = null,
        CancellationToken ct = default
    )
    {
        if (userIds.Count == 0)
            return [];

        try
        {
            // The grain method takes a concrete List (Orleans has a built-in serializer for it);
            // reuse the caller's list when it already is one to avoid a copy.
            var ids = userIds as List<long> ?? [.. userIds];
            return await Grain(guildId).FilterByPermission(ids, (long)permission, channelId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Permissions: FilterByPermission failed for guild {GuildId} — denying all (fail-closed)",
                guildId
            );
            return [];
        }
    }

    public async Task InvalidateUserAsync(long userId, long guildId, CancellationToken ct = default)
    {
        try
        {
            await Grain(guildId).InvalidateUser(userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Permissions: InvalidateUser failed for user {UserId} in guild {GuildId} — continuing",
                userId,
                guildId
            );
        }
    }

    public async Task InvalidateGuildAsync(long guildId, CancellationToken ct = default)
    {
        try
        {
            await Grain(guildId).InvalidateGuild();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Permissions: InvalidateGuild failed for guild {GuildId} — continuing",
                guildId
            );
        }
    }
}
