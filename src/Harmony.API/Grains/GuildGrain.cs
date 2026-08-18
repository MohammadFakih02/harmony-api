using Harmony.Application.Authorization;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

namespace Harmony.API.Grains;

/// <summary>
/// Grain-memory implementation of <see cref="IGuildGrain"/> — the D4 replacement for the Redis
/// <c>perms:{userId}:{guildId}</c> permission cache. On first use it hydrates a snapshot of the
/// guild's authorization inputs from Postgres (roles + bits, membership + owner set, per-member role
/// assignments) and resolves effective bits <em>in memory</em> thereafter; per-channel overrides are
/// loaded lazily and cached per channel. Because the grain is single-threaded, the snapshot never
/// races a mutation, so the resolve needs no locks.
///
/// <para><b>Hot path:</b> <see cref="FilterByPermission"/> loads the channel's overrides <em>once</em>
/// and then filters the whole member list in memory — replacing the unread fan-out's per-user Redis
/// round-trip with a pure in-process pass.</para>
///
/// <para><b>Layering:</b> a grain outlives any request scope, so the scoped EF repositories are
/// resolved per hydration through an <see cref="IServiceScopeFactory"/> (the same pattern as
/// <see cref="UserGrain"/>). The scope is opened only on a cache miss/invalidation, never on a hit.</para>
///
/// <para><b>Failure posture:</b> unlike <see cref="UserGrain"/> (whose presence defaults are benign),
/// a hydration failure here is NOT swallowed — it propagates (and <c>_loaded</c> stays false so the
/// next call retries), exactly as the Test-environment <c>PermissionService.ComputeAsync</c> throws on
/// a DB outage. The <c>GrainPermissionService</c> adapter turns that into a fail-<em>closed</em> deny
/// so an authorization decision is never wrongly granted on a transient error.</para>
///
/// <para>The resolution math is the shared <see cref="PermissionResolver"/>, so this grain and the
/// Test-environment <c>PermissionService</c> resolve byte-identically.</para>
/// </summary>
public sealed class GuildGrain : Grain, IGuildGrain
{
    /// <summary>Shared empty set for a member with no explicitly-assigned roles (@everyone is implicit).</summary>
    private static readonly HashSet<long> NoRoles = [];

    private readonly IServiceScopeFactory _scopeFactory;

    // --- snapshot (valid only while _loaded) ---------------------------------
    private bool _loaded;
    private long _everyoneBits;
    private long? _everyoneRoleId;
    private Dictionary<long, long> _roleBits = []; // roleId → permission bits
    private HashSet<long> _members = []; // every member's userId
    private HashSet<long> _owners = []; // owner userId(s)
    private Dictionary<long, HashSet<long>> _memberRoleIds = []; // userId → assigned role ids (no @everyone)

    // Per-channel overrides, filled lazily and kept until the snapshot is invalidated.
    private readonly Dictionary<long, IReadOnlyList<ChannelPermissionOverride>> _overridesByChannel = [];

    private long GuildId => this.GetPrimaryKeyLong();

    public GuildGrain(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    // -------------------------------------------------------------------------
    // Reads
    // -------------------------------------------------------------------------

    public async Task<long> ResolveBits(long userId, long? channelId)
    {
        await EnsureLoadedAsync();
        var overrides = channelId is { } cid ? await GetOverridesAsync(cid) : null;
        return ComputeBits(userId, overrides);
    }

    public async Task<List<long>> FilterByPermission(List<long> userIds, long permissionBit, long? channelId)
    {
        if (userIds.Count == 0)
            return [];

        await EnsureLoadedAsync();
        // Resolve the channel's overrides ONCE for the whole group — the batched win over the old
        // per-user cache read. Then every user is a pure in-memory compute.
        var overrides = channelId is { } cid ? await GetOverridesAsync(cid) : null;

        var granted = new List<long>(userIds.Count);
        foreach (var uid in userIds) // preserve the caller's ordering
        {
            var bits = ComputeBits(uid, overrides);
            if ((bits & permissionBit) == permissionBit)
                granted.Add(uid);
        }
        return granted;
    }

    /// <summary>
    /// Pure, in-memory resolve from the loaded snapshot. Owner/non-member short-circuits mirror the
    /// resolver's so an owner never triggers an override load; everything else defers to
    /// <see cref="PermissionResolver.Resolve"/> for byte-identical behaviour.
    /// </summary>
    private long ComputeBits(long userId, IReadOnlyList<ChannelPermissionOverride>? overrides)
    {
        if (!_members.Contains(userId))
            return 0;
        if (_owners.Contains(userId))
            return PermissionResolver.AllPermissions;

        var roleIds = _memberRoleIds.TryGetValue(userId, out var ids) ? ids : NoRoles;
        var memberRoleBits = new List<long>(roleIds.Count);
        foreach (var rid in roleIds)
            if (_roleBits.TryGetValue(rid, out var bits))
                memberRoleBits.Add(bits);

        return PermissionResolver.Resolve(
            isMember: true,
            isOwner: false,
            everyoneBits: _everyoneBits,
            memberRoleBits: memberRoleBits,
            overrides: overrides,
            everyoneRoleId: _everyoneRoleId,
            memberRoleIds: roleIds,
            userId: userId
        );
    }

    // -------------------------------------------------------------------------
    // Invalidation — both drop the whole snapshot; the next resolve re-hydrates.
    // -------------------------------------------------------------------------

    // At this scale a full re-hydrate is three cheap queries on a rare admin action, and a
    // member-targeted override change (routed here via InvalidateUser) must also drop that channel's
    // cached overrides — dropping the whole snapshot is the simplest thing that is unarguably correct.
    // A targeted per-user reload is a possible future optimization, unnecessary now.
    public Task InvalidateUser(long userId)
    {
        Reset();
        return Task.CompletedTask;
    }

    public Task InvalidateGuild()
    {
        Reset();
        return Task.CompletedTask;
    }

    private void Reset()
    {
        _loaded = false;
        _overridesByChannel.Clear(); // lazily filled → must be cleared explicitly (the model dicts are reassigned on reload)
    }

    // -------------------------------------------------------------------------
    // Hydration
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads the guild's roles, membership/owners and per-member role assignments into memory once per
    /// activation (or after an invalidation). Builds into locals and commits at the end, so a repo
    /// failure leaves the previous snapshot untouched and <c>_loaded</c> false (→ retry next call) —
    /// deliberately NOT swallowed, mirroring PermissionService throwing on a DB outage.
    /// </summary>
    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var roleRepo = scope.ServiceProvider.GetRequiredService<IRoleRepository>();
        var guildRepo = scope.ServiceProvider.GetRequiredService<IGuildRepository>();

        var roles = await roleRepo.GetByGuildAsync(GuildId);
        var members = await guildRepo.GetMembersAsync(GuildId);
        var roleIdsByMember = await roleRepo.GetRoleIdsByMemberAsync(GuildId);

        var everyone = roles.FirstOrDefault(r => r.IsDefault);
        _everyoneRoleId = everyone?.Id;
        _everyoneBits = everyone?.PermissionBits ?? 0;
        _roleBits = roles.ToDictionary(r => r.Id, r => r.PermissionBits);
        _members = members.Select(m => m.UserId).ToHashSet();
        _owners = members.Where(m => m.IsOwner).Select(m => m.UserId).ToHashSet();
        _memberRoleIds = roleIdsByMember.ToDictionary(kv => kv.Key, kv => kv.Value.ToHashSet());

        _loaded = true;
    }

    /// <summary>Lazily loads and caches one channel's overrides; a hit is a plain dictionary lookup.</summary>
    private async Task<IReadOnlyList<ChannelPermissionOverride>> GetOverridesAsync(long channelId)
    {
        if (_overridesByChannel.TryGetValue(channelId, out var cached))
            return cached;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IChannelPermissionOverrideRepository>();
        var overrides = await repo.GetByChannelAsync(channelId);
        _overridesByChannel[channelId] = overrides;
        return overrides;
    }
}
