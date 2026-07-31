using Harmony.API.Filters;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Hubs;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Harmony.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// CRUD for a channel's permission overrides — the (allow/deny) layer the permission
/// resolver applies on top of resolved guild-level bits. Edits are gated on ManageRoles;
/// every write invalidates the permission cache so the next resolve recomputes:
/// role-targeted changes drop the whole guild's cache, member-targeted changes drop just
/// that member's. The resolver only reads overrides — this controller manages them.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/channels/{channelId:long}/overrides")]
[Authorize]
[EnableRateLimiting("api")]
public class ChannelOverridesController : HarmonyControllerBase
{
    private readonly IChannelPermissionOverrideRepository _overrides;
    private readonly IChannelRepository _channels;
    private readonly IGuildRepository _guilds;
    private readonly IRoleRepository _roles;
    private readonly IPermissionService _permissions;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IHubBroadcaster _broadcaster;

    public ChannelOverridesController(
        IChannelPermissionOverrideRepository overrides,
        IChannelRepository channels,
        IGuildRepository guilds,
        IRoleRepository roles,
        IPermissionService permissions,
        ISnowflakeIdGenerator snowflake,
        IHubBroadcaster broadcaster
    )
    {
        _overrides = overrides;
        _channels = channels;
        _guilds = guilds;
        _roles = roles;
        _permissions = permissions;
        _snowflake = snowflake;
        _broadcaster = broadcaster;
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/overrides
    [HttpGet]
    public async Task<IActionResult> List(long guildId, long channelId)
    {
        if (!await _guilds.IsMemberAsync(guildId, GetUserId()))
            return Forbid();

        if (await ValidateChannel(guildId, channelId) is { } error)
            return error;

        var overrides = await _overrides.GetByChannelAsync(channelId);
        return Ok(overrides.Select(ToResponse));
    }

    // PUT /api/guilds/{guildId}/channels/{channelId}/overrides/{targetId}
    [HttpPut("{targetId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Upsert(
        long guildId,
        long channelId,
        long targetId,
        [FromBody] UpsertChannelOverrideRequest request
    )
    {
        if (await ValidateChannel(guildId, channelId) is { } channelError)
            return channelError;

        if (request.TargetType is not ("role" or "user"))
            return BadRequest(new { error = "TargetType must be 'role' or 'user'." });

        // A bit in both allow and deny is contradictory — reject rather than silently
        // letting deny win (the resolver applies (perms & ~deny) | allow).
        if ((request.AllowBits & request.DenyBits) != 0)
            return BadRequest(new { error = "AllowBits and DenyBits must not overlap." });

        // The target must actually exist in this guild, or we'd persist a dangling override.
        if (request.TargetType == "role")
        {
            var role = await _roles.GetByIdAsync(targetId);
            if (role is null || role.GuildId != guildId)
                return BadRequest(new { error = "Target role does not belong to this guild." });
        }
        else if (!await _guilds.IsMemberAsync(guildId, targetId))
        {
            return BadRequest(new { error = "Target user is not a member of this guild." });
        }

        var existing = await _overrides.GetByChannelAndTargetAsync(channelId, targetId);
        ChannelPermissionOverride entity;
        if (existing is not null)
        {
            existing.TargetType = request.TargetType;
            existing.AllowBits = request.AllowBits;
            existing.DenyBits = request.DenyBits;
            entity = existing;
        }
        else
        {
            entity = new ChannelPermissionOverride
            {
                Id = _snowflake.NextId(),
                ChannelId = channelId,
                TargetId = targetId,
                TargetType = request.TargetType,
                AllowBits = request.AllowBits,
                DenyBits = request.DenyBits,
            };
            await _overrides.AddAsync(entity);
        }

        await _overrides.SaveChangesAsync();
        await InvalidateForTarget(request.TargetType, targetId, guildId);
        await BroadcastChanged(guildId, channelId);

        return Ok(ToResponse(entity));
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}/overrides/{targetId}
    [HttpDelete("{targetId:long}")]
    [RequirePermission(Permission.ManageRoles)]
    public async Task<IActionResult> Delete(long guildId, long channelId, long targetId)
    {
        if (await ValidateChannel(guildId, channelId) is { } channelError)
            return channelError;

        var existing = await _overrides.GetByChannelAndTargetAsync(channelId, targetId);
        if (existing is null)
            return NotFound();

        _overrides.Remove(existing);
        await _overrides.SaveChangesAsync();
        await InvalidateForTarget(existing.TargetType, targetId, guildId);
        await BroadcastChanged(guildId, channelId);

        return NoContent();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>404 if the channel is missing or belongs to a different guild; null if valid.</summary>
    private async Task<IActionResult?> ValidateChannel(long guildId, long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        return channel is null || channel.GuildId != guildId ? NotFound() : null;
    }

    /// <summary>
    /// Role override changes can affect every member holding that role, so drop the whole
    /// guild's cache; member overrides affect only the one member.
    /// </summary>
    private Task InvalidateForTarget(string targetType, long targetId, long guildId) =>
        targetType == "role"
            ? _permissions.InvalidateGuildAsync(guildId)
            : _permissions.InvalidateUserAsync(targetId, guildId);

    /// <summary>
    /// Best-effort live resync signal — the write already persisted and the cache is
    /// invalidated, so a failed broadcast must not fail the request.
    /// </summary>
    private async Task BroadcastChanged(long guildId, long channelId)
    {
        try
        {
            await _broadcaster.BroadcastChannelOverridesChangedAsync(
                guildId,
                new ChannelOverridesChangedPayload(guildId, channelId)
            );
        }
        catch
        {
            // Clients that miss the event still resync on next navigation/load.
        }
    }


    private static ChannelOverrideResponse ToResponse(ChannelPermissionOverride o) =>
        new(o.Id, o.ChannelId, o.TargetId, o.TargetType, o.AllowBits, o.DenyBits);
}
