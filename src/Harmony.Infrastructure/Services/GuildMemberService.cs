using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Application.Validation;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.Extensions.Logging;

namespace Harmony.Infrastructure.Services;

/// <summary>
/// Implements the guild member moderation actions. The required permission bit is enforced at the
/// endpoint ([RequirePermission]); this service owns the hierarchy guard, the state change, the
/// best-effort audit write, and the (best-effort) SignalR broadcasts. State mutations are committed
/// before any audit/broadcast side effect, so a failed side effect never rolls back the action.
///
/// Hierarchy (v1, locked): a moderator cannot act on themselves or on the guild owner. The
/// "can't act on someone with a higher role" comparison is deferred to role-management-ui, where
/// role positions become first-class.
/// </summary>
public class GuildMemberService : IGuildMemberService
{
    private readonly IGuildRepository _guilds;
    private readonly IGuildBanRepository _bans;
    private readonly IUserRepository _users;
    private readonly IPermissionService _permissions;
    private readonly IAuditLogService _audit;
    private readonly IHubBroadcaster _broadcaster;
    private readonly ILogger<GuildMemberService> _logger;

    public GuildMemberService(
        IGuildRepository guilds,
        IGuildBanRepository bans,
        IUserRepository users,
        IPermissionService permissions,
        IAuditLogService audit,
        IHubBroadcaster broadcaster,
        ILogger<GuildMemberService> logger
    )
    {
        _guilds = guilds;
        _bans = bans;
        _users = users;
        _permissions = permissions;
        _audit = audit;
        _broadcaster = broadcaster;
        _logger = logger;
    }

    public async Task KickAsync(
        long guildId,
        long actorId,
        long targetId,
        CancellationToken ct = default
    )
    {
        var target = await GuardTargetAsync(guildId, actorId, targetId);

        await _guilds.RemoveMemberAsync(target);
        await DecrementMemberCountAndSaveAsync(guildId);

        await _permissions.InvalidateUserAsync(targetId, guildId, ct);
        await _audit.LogAsync(guildId, actorId, AuditLogAction.MemberKick, targetId: targetId, ct: ct);
        await BroadcastRemovalAsync(guildId, targetId, reason: null, banned: false);
    }

    public async Task BanAsync(
        long guildId,
        long actorId,
        long targetId,
        string? reason,
        CancellationToken ct = default
    )
    {
        var target = await GuardTargetAsync(guildId, actorId, targetId);

        // Ban row + member removal + count decrement all commit in one SaveChanges — the ban and
        // guild repos share the request-scoped DbContext.
        await _bans.AddAsync(
            new GuildBan
            {
                GuildId = guildId,
                UserId = targetId,
                BannedBy = actorId,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }
        );
        await _guilds.RemoveMemberAsync(target);
        await DecrementMemberCountAndSaveAsync(guildId);

        await _permissions.InvalidateUserAsync(targetId, guildId, ct);
        await _audit.LogAsync(
            guildId,
            actorId,
            AuditLogAction.MemberBan,
            targetId: targetId,
            changes: new { reason },
            reason: reason,
            ct: ct
        );
        await BroadcastRemovalAsync(guildId, targetId, reason, banned: true);
    }

    public async Task UnbanAsync(
        long guildId,
        long actorId,
        long targetId,
        CancellationToken ct = default
    )
    {
        var ban = await _bans.GetAsync(guildId, targetId);
        if (ban is null)
            throw new KeyNotFoundException("This user is not banned.");

        _bans.Remove(ban);
        await _bans.SaveChangesAsync();

        await _audit.LogAsync(guildId, actorId, AuditLogAction.MemberUnban, targetId: targetId, ct: ct);
    }

    public async Task TimeoutAsync(
        long guildId,
        long actorId,
        long targetId,
        long durationSeconds,
        CancellationToken ct = default
    )
    {
        if (durationSeconds < 1 || durationSeconds > TimeoutMemberRequestValidator.MaxTimeoutSeconds)
            throw new ArgumentException(
                $"Timeout duration must be between 1 second and {TimeoutMemberRequestValidator.MaxTimeoutSeconds} seconds (28 days)."
            );

        var target = await GuardTargetAsync(guildId, actorId, targetId);

        var until = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + durationSeconds * 1000;
        target.CommunicationDisabledUntil = until;
        await _guilds.SaveChangesAsync();

        await _audit.LogAsync(
            guildId,
            actorId,
            AuditLogAction.MemberTimeout,
            targetId: targetId,
            changes: new { until },
            ct: ct
        );
        await BroadcastMemberUpdatedAsync(guildId, targetId, until);
    }

    public async Task ClearTimeoutAsync(
        long guildId,
        long actorId,
        long targetId,
        CancellationToken ct = default
    )
    {
        var target = await GuardTargetAsync(guildId, actorId, targetId);

        target.CommunicationDisabledUntil = null;
        await _guilds.SaveChangesAsync();

        await _audit.LogAsync(
            guildId,
            actorId,
            AuditLogAction.MemberTimeout,
            targetId: targetId,
            changes: new { until = (long?)null },
            ct: ct
        );
        await BroadcastMemberUpdatedAsync(guildId, targetId, null);
    }

    public async Task<IReadOnlyList<GuildBanResponse>> GetBansAsync(
        long guildId,
        CancellationToken ct = default
    )
    {
        var bans = await _bans.GetByGuildAsync(guildId);
        if (bans.Count == 0)
            return [];

        // Resolve both the banned user and the banning moderator in one round trip (no N+1).
        var ids = bans.Select(b => b.UserId).Concat(bans.Select(b => b.BannedBy)).Distinct();
        var users = await _users.GetByIdsAsync(ids);

        return bans.Select(b =>
            {
                users.TryGetValue(b.UserId, out var banned);
                users.TryGetValue(b.BannedBy, out var by);
                return new GuildBanResponse(
                    b.UserId,
                    banned?.UserName,
                    banned?.AvatarKey,
                    b.BannedBy,
                    by?.UserName,
                    b.Reason,
                    b.CreatedAt
                );
            })
            .ToList();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Hierarchy guard shared by kick/ban/timeout. Returns the tracked target member.</summary>
    private async Task<GuildMember> GuardTargetAsync(long guildId, long actorId, long targetId)
    {
        if (targetId == actorId)
            throw new ArgumentException("You cannot moderate yourself.");

        var target = await _guilds.GetMemberAsync(guildId, targetId);
        if (target is null)
            throw new KeyNotFoundException("Member not found in this guild.");

        if (target.IsOwner)
            throw new UnauthorizedAccessException("You cannot moderate the guild owner.");

        return target;
    }

    private async Task DecrementMemberCountAndSaveAsync(long guildId)
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is not null && guild.MemberCount > 0)
            guild.MemberCount--;
        await _guilds.SaveChangesAsync();
    }

    private async Task BroadcastRemovalAsync(long guildId, long targetId, string? reason, bool banned)
    {
        try
        {
            await _broadcaster.BroadcastMemberRemovedAsync(
                guildId,
                new MemberRemovedPayload(guildId, targetId)
            );
            await _broadcaster.BroadcastKickedAsync(
                targetId,
                new KickedPayload(guildId, reason, banned)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to broadcast member removal: guild={GuildId} target={TargetId}",
                guildId,
                targetId
            );
        }
    }

    private async Task BroadcastMemberUpdatedAsync(long guildId, long targetId, long? until)
    {
        try
        {
            await _broadcaster.BroadcastMemberUpdatedAsync(
                guildId,
                new MemberUpdatedPayload(guildId, targetId, until)
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to broadcast member update: guild={GuildId} target={TargetId}",
                guildId,
                targetId
            );
        }
    }
}
