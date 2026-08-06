using System.Globalization;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
[EnableRateLimiting("api")]
public class UsersController : HarmonyControllerBase
{
    private readonly IUserRepository _users;
    private readonly IGuildRepository _guilds;
    private readonly IPresenceService _presence;
    private readonly IUserBlockRepository _blocks;
    private readonly IFriendRepository _friends;
    private readonly IUserNicknameRepository _nicknames;
    private readonly IDirectMessageRepository _dms;
    private readonly IHubBroadcaster _broadcaster;

    public UsersController(
        IUserRepository users,
        IGuildRepository guilds,
        IPresenceService presence,
        IUserBlockRepository blocks,
        IFriendRepository friends,
        IUserNicknameRepository nicknames,
        IDirectMessageRepository dms,
        IHubBroadcaster broadcaster
    )
    {
        _users = users;
        _guilds = guilds;
        _presence = presence;
        _blocks = blocks;
        _friends = friends;
        _nicknames = nicknames;
        _dms = dms;
        _broadcaster = broadcaster;
    }

    // GET /api/users/me
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me
    [HttpPatch("me")]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserRequest request)
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        if (request.Bio is not null)
            user.Bio = request.Bio;
        if (request.StatusMessage is not null)
            user.StatusMessage = request.StatusMessage;

        if (request.BannerColor is not null)
        {
            if (request.BannerColor.Length == 0)
            {
                user.BannerColor = null; // empty string clears it
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(request.BannerColor, "^#[0-9a-fA-F]{6}$"))
            {
                user.BannerColor = request.BannerColor.ToLowerInvariant();
            }
            else
            {
                return BadRequest(new { error = "Banner colour must be a #rrggbb hex value." });
            }
        }

        if (request.DateOfBirth is not null)
        {
            if (request.DateOfBirth.Length == 0)
            {
                user.DateOfBirth = null; // empty string clears it
            }
            else if (
                DateOnly.TryParseExact(
                    request.DateOfBirth,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var dob
                )
                && dob <= DateOnly.FromDateTime(DateTime.UtcNow)
            )
            {
                user.DateOfBirth = dob;
            }
            else
            {
                return BadRequest(new { error = "Invalid date of birth." });
            }
        }

        await _users.SaveChangesAsync();

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me/status — durable preferred status (online|away|dnd|invisible)
    [HttpPatch("me/status")]
    public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
    {
        // Shape (the four allowed values) is enforced by UpdateStatusRequestValidator.
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        user.PreferredStatus = request.Status;
        // Expiry only applies to a non-default status — "online" is already the revert
        // target, so an expiry on it is meaningless and is cleared.
        user.PreferredStatusExpiresAt =
            request.Status != PresenceStatus.Online && request.ExpiresInMinutes is > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + (long)request.ExpiresInMinutes.Value * 60_000
                : null;
        await _users.SaveChangesAsync(); // Postgres is the source of truth

        // Update the Redis cache + recompute the effective status + broadcast StatusChanged.
        await _presence.SetPreferredStatusAsync(user.Id, request.Status);

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me/custom-status — the free-text custom status message + clear-after.
    [HttpPatch("me/custom-status")]
    public async Task<IActionResult> UpdateCustomStatus(
        [FromBody] UpdateCustomStatusRequest request
    )
    {
        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        var message = string.IsNullOrWhiteSpace(request.Message) ? null : request.Message.Trim();
        user.StatusMessage = message;
        // No message ⇒ no expiry; otherwise honour the optional clear-after.
        user.StatusMessageExpiresAt =
            message is not null && request.ExpiresInMinutes is > 0
                ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + (long)request.ExpiresInMinutes.Value * 60_000
                : null;
        await _users.SaveChangesAsync();

        // Live half: cache the message + broadcast StatusChanged to friends/own tabs.
        await _presence.SetCustomStatusAsync(user.Id, message);

        return Ok(ToProfileResponse(user));
    }

    // PATCH /api/users/me/dm-privacy — the checklist of who may open a new DM with me.
    [HttpPatch("me/dm-privacy")]
    public async Task<IActionResult> UpdateDmPrivacy([FromBody] UpdateDmPrivacyRequest request)
    {
        if (!(request.Audiences ?? []).All(DmPrivacy.AllowedTokens.Contains))
            return BadRequest(new { error = "Invalid DM privacy selection." });

        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        user.DmPrivacy = DmPrivacy.Normalize(request.Audiences ?? []);
        await _users.SaveChangesAsync();

        // My privacy gates who may message me in EXISTING 1:1 DMs too (the send-gate re-checks it),
        // so nudge each 1:1 peer to re-evaluate their composer live instead of only on refresh.
        await NotifyDmPeersGateChangedAsync(user.Id);

        return Ok(ToProfileResponse(user));
    }

    /// <summary>
    /// Best-effort: broadcast <c>DmChannelUpdated</c> for each of the user's 1:1 DM channels so both
    /// participants re-fetch the send-gate after a DM-privacy change. Bounded by the user's DM count
    /// and gated to 1:1s (group DMs aren't privacy-gated). Never throws.
    /// </summary>
    private async Task NotifyDmPeersGateChangedAsync(long me)
    {
        try
        {
            var oneToOne = (await _dms.GetVisibleForUserAsync(me))
                .Where(d => d.Type == "dm")
                .Select(d => d.ChannelId)
                .ToList();
            if (oneToOne.Count == 0)
                return;

            var byChannel = await _dms.GetParticipantsForChannelsAsync(oneToOne);
            foreach (var (channelId, participants) in byChannel)
                await _broadcaster.BroadcastDmChannelUpdatedAsync(
                    participants,
                    new DmChannelUpdatedPayload(channelId)
                );
        }
        catch
        {
            // swallow — the privacy change already saved; peers re-check on next refresh.
        }
    }

    // PATCH /api/users/me/guild-order — the caller's personal guild-rail order.
    // Lenient by design (a personal preference, like mutes): ids are stored as sent; stale ids
    // (left guilds) are ignored at read time and guilds missing from the list append after it.
    [HttpPatch("me/guild-order")]
    public async Task<IActionResult> UpdateGuildOrder([FromBody] UpdateGuildOrderRequest request)
    {
        if (request.GuildOrder is null || request.GuildOrder.Count > 500)
            return BadRequest(new { error = "Invalid guild order." });

        var user = await _users.GetByIdAsync(GetUserId());
        if (user is null)
            return NotFound();

        user.GuildOrder = request.GuildOrder.Distinct().ToArray();
        await _users.SaveChangesAsync();

        return NoContent();
    }

    // GET /api/users/presence?ids=1,2,3 — effective status + custom message for the member list
    [HttpGet("presence")]
    public async Task<IActionResult> GetPresence([FromQuery] string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return Ok(new Dictionary<string, UserPresenceResponse>());

        var userIds = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => long.TryParse(s, out var id) ? id : (long?)null)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var statuses = await _presence.GetStatusesAsync(userIds);
        var messages = await _presence.GetStatusMessagesAsync(userIds);

        // Serialize ids as strings to match the Snowflake-as-string convention everywhere else.
        // A user who appears offline never exposes their custom message.
        return Ok(
            statuses.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new UserPresenceResponse(
                    kv.Value,
                    kv.Value == "offline" ? null : messages.GetValueOrDefault(kv.Key)
                )
            )
        );
    }

    // GET /api/users/{id}
    [HttpGet("{id:long}")]
    public async Task<IActionResult> GetById(long id)
    {
        var user = await _users.GetByIdAsync(id);
        if (user is null)
            return NotFound();

        var me = GetUserId();
        var canMessage =
            me == id
            || DmPrivacy.Parse(user.DmPrivacy).Contains(DmPrivacy.Everyone)
            || DmPrivacy.CanReceiveFrom(
                user.DmPrivacy,
                isFriend: (await _friends.GetBetweenAsync(me, id)) is { Status: "accepted" },
                sharesGuild: await _guilds.ShareAnyGuildAsync(me, id)
            );

        return Ok(ToPublicResponse(user, canMessage));
    }

    // GET /api/users/me/guilds — sorted by the caller's personal rail order.
    [HttpGet("me/guilds")]
    public async Task<IActionResult> GetMyGuilds()
    {
        var me = GetUserId();
        var user = await _users.GetByIdAsync(me);
        var guilds = ApplyGuildOrder(await _guilds.GetByUserIdAsync(me), user?.GuildOrder);
        return Ok(
            guilds.Select(g => new GuildResponse(
                g.Id,
                g.Name,
                g.Description,
                g.OwnerId,
                g.IconKey,
                g.BannerKey,
                g.IsPublic,
                g.MemberCount,
                g.CreatedAt,
                g.WelcomeChannelId,
                g.WelcomeMessage,
                g.SystemMessagesEnabled,
                g.RequireVerifiedEmail
            ))
        );
    }

    // -------------------------------------------------------------------------
    // Blocking — block CRUD + a bidirectional query seam. The effects
    // (DM/mention/presence suppression) belong to Phase 4 features that will
    // consume IUserBlockRepository.AreBlockedAsync; nothing consumes it yet.
    // -------------------------------------------------------------------------

    // POST /api/users/{id}/block — idempotent
    [HttpPost("{id:long}/block")]
    public async Task<IActionResult> Block(long id)
    {
        var me = GetUserId();
        if (id == me)
            return BadRequest(new { error = "You cannot block yourself." });

        var target = await _users.GetByIdAsync(id);
        if (target is null)
            return NotFound();

        var existing = await _blocks.GetAsync(me, id);
        if (existing is null)
        {
            await _blocks.AddAsync(
                new UserBlock
                {
                    BlockerId = me,
                    BlockedId = id,
                    CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                }
            );
            await _blocks.SaveChangesAsync();
        }

        // Blocking severs any friendship or pending request between the two users
        // (the deferred §5.14 cleanup, now that friends-system exists).
        var friendship = await _friends.GetBetweenAsync(me, id);
        if (friendship is not null)
        {
            _friends.Remove(friendship);
            await _friends.SaveChangesAsync();

            // Notify the blocked user to prune; keep my own other tabs in sync too.
            await _broadcaster.BroadcastFriendRemovedAsync(id, new FriendRemovedPayload(me));
            await _broadcaster.BroadcastFriendRemovedAsync(me, new FriendRemovedPayload(id));
        }

        // If a 1:1 DM exists with the blocked user, nudge both sides to re-check their DM send-gate
        // so the composer locks live (the newly-blocked user's client can't send anymore) instead
        // of only after a refresh.
        await NotifyDmGateChangedAsync(me, id);

        return NoContent();
    }

    // DELETE /api/users/{id}/block — idempotent
    [HttpDelete("{id:long}/block")]
    public async Task<IActionResult> Unblock(long id)
    {
        var me = GetUserId();
        var existing = await _blocks.GetAsync(me, id);
        if (existing is not null)
        {
            _blocks.Remove(existing);
            await _blocks.SaveChangesAsync();
        }

        // Unblocking re-opens the DM — nudge both sides to re-check the send-gate so the composer
        // unlocks live rather than staying disabled until a refresh.
        await NotifyDmGateChangedAsync(me, id);

        return NoContent();
    }

    /// <summary>
    /// Best-effort: if the two users share a 1:1 DM channel, broadcast <c>DmChannelUpdated</c> to
    /// both so their open composers re-fetch the send-gate (block/privacy changes take effect live).
    /// Never throws — a signalling hiccup must not fail the block/unblock itself.
    /// </summary>
    private async Task NotifyDmGateChangedAsync(long me, long other)
    {
        try
        {
            var channelId = await _dms.GetSharedChannelIdAsync(me, other);
            if (channelId is { } id)
                await _broadcaster.BroadcastDmChannelUpdatedAsync(
                    new[] { me, other },
                    new DmChannelUpdatedPayload(id)
                );
        }
        catch
        {
            // swallow — the block/unblock already succeeded; the peer re-checks on next refresh.
        }
    }

    // GET /api/users/me/blocks
    [HttpGet("me/blocks")]
    public async Task<IActionResult> GetMyBlocks()
    {
        var blocks = await _blocks.GetByBlockerAsync(GetUserId());
        if (blocks.Count == 0)
            return Ok(Array.Empty<BlockResponse>());

        var users = await _users.GetByIdsAsync(blocks.Select(b => b.BlockedId));
        var result = blocks
            .Where(b => users.ContainsKey(b.BlockedId))
            .Select(b =>
            {
                var u = users[b.BlockedId];
                return new BlockResponse(
                    u.Id,
                    u.UserName!,
                    u.AvatarKey,
                    u.BannerKey,
                    b.CreatedAt
                );
            });

        return Ok(result);
    }

    // -------------------------------------------------------------------------
    // Friend nicknames — a private, one-directional alias only the caller sees. Independent of
    // friendship/guild membership; used as the friend/DM display name. (Server nicknames are a
    // separate, guild-scoped thing on GuildMembersController.)
    // -------------------------------------------------------------------------

    // GET /api/users/me/nicknames — the caller's whole personal nickname map (targetId → nickname).
    [HttpGet("me/nicknames")]
    public async Task<IActionResult> GetMyNicknames()
    {
        var rows = await _nicknames.GetByOwnerAsync(GetUserId());
        return Ok(rows.ToDictionary(n => n.TargetId.ToString(CultureInfo.InvariantCulture), n => n.Nickname));
    }

    // PUT /api/users/{userId}/nickname — set (or, when blank, clear) my private alias for {userId}.
    [HttpPut("{userId:long}/nickname")]
    public async Task<IActionResult> SetNickname(long userId, [FromBody] SetNicknameRequest request)
    {
        var me = GetUserId();
        if (userId == me)
            return BadRequest(new { error = "You cannot set a nickname for yourself." });

        var nickname = string.IsNullOrWhiteSpace(request.Nickname) ? null : request.Nickname.Trim();
        var existing = await _nicknames.GetAsync(me, userId);

        if (nickname is null)
        {
            // Blank PUT clears, mirroring DELETE — keeps the client's set/clear path uniform.
            if (existing is not null)
            {
                _nicknames.Remove(existing);
                await _nicknames.SaveChangesAsync();
            }
            return NoContent();
        }

        if (await _users.GetByIdAsync(userId) is null)
            return NotFound(new { error = "User not found." });

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (existing is null)
        {
            await _nicknames.AddAsync(
                new UserNickname
                {
                    OwnerId = me,
                    TargetId = userId,
                    Nickname = nickname,
                    CreatedAt = now,
                    UpdatedAt = now,
                }
            );
        }
        else
        {
            existing.Nickname = nickname;
            existing.UpdatedAt = now;
        }
        await _nicknames.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/users/{userId}/nickname — idempotent clear.
    [HttpDelete("{userId:long}/nickname")]
    public async Task<IActionResult> ClearNickname(long userId)
    {
        var existing = await _nicknames.GetAsync(GetUserId(), userId);
        if (existing is not null)
        {
            _nicknames.Remove(existing);
            await _nicknames.SaveChangesAsync();
        }
        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------


    /// <summary>
    /// Sorts guilds by the user's saved rail order; guilds not in the list (new joins) keep
    /// their current (join) order after the ranked ones — OrderBy is stable. Internal so
    /// BootstrapController applies the identical ordering.
    /// </summary>
    internal static List<Guild> ApplyGuildOrder(List<Guild> guilds, long[]? order)
    {
        if (order is null || order.Length == 0)
            return guilds;
        var rank = new Dictionary<long, int>(order.Length);
        for (var i = 0; i < order.Length; i++)
            rank.TryAdd(order[i], i);
        return guilds
            .OrderBy(g => rank.TryGetValue(g.Id, out var r) ? r : int.MaxValue)
            .ToList();
    }

    // Internal so BootstrapController can reuse the exact same mapping (single source of truth).
    internal static UserProfileResponse ToProfileResponse(User u) =>
        new(
            u.Id,
            u.UserName!,
            u.Email!,
            u.AvatarKey,
            u.BannerKey,
            u.BannerColor,
            u.Bio,
            u.StatusMessage,
            u.StatusMessageExpiresAt,
            u.PreferredStatus,
            u.PreferredStatusExpiresAt,
            u.AccountStatus,
            u.CreatedAt,
            u.DateOfBirth?.ToString("yyyy-MM-dd"),
            u.DmPrivacy
        );

    private static PublicUserResponse ToPublicResponse(User u, bool canMessage) =>
        new(u.Id, u.UserName!, u.AvatarKey, u.BannerKey, u.BannerColor, u.Bio, u.StatusMessage,
            AgeFrom(u.DateOfBirth), u.DmPrivacy, canMessage);

    /// <summary>Whole years between a DOB and today (UTC), or null when DOB is unset.</summary>
    private static int? AgeFrom(DateOnly? dob)
    {
        if (dob is not { } d)
            return null;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var age = today.Year - d.Year;
        if (d > today.AddYears(-age))
            age--; // birthday hasn't occurred yet this year
        return age < 0 ? null : age;
    }
}
