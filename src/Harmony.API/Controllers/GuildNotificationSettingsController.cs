using System.Security.Claims;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// The caller's own per-guild / per-channel notification levels (§5.31, roadmap E#16). These are a
/// personal preference, not a moderation surface, so every endpoint is gated only on guild membership
/// (no permission bit). Resolution at notify-time is channel-scope → guild-scope → default "mentions";
/// resetting a scope is a DELETE (removing the row), which lets a channel fall back to the guild level
/// and the guild fall back to the global default.
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}")]
[Authorize]
[EnableRateLimiting("api")]
public class GuildNotificationSettingsController : ControllerBase
{
    private readonly IGuildRepository _guilds;
    private readonly IChannelRepository _channels;
    private readonly INotificationSettingRepository _settings;

    public GuildNotificationSettingsController(
        IGuildRepository guilds,
        IChannelRepository channels,
        INotificationSettingRepository settings
    )
    {
        _guilds = guilds;
        _channels = channels;
        _settings = settings;
    }

    // GET /api/guilds/{guildId}/notification-settings
    [HttpGet("notification-settings")]
    public async Task<IActionResult> Get(long guildId)
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        var guildRow = await _settings.GetAsync(userId, NotificationScope.Guild, guildId);
        var guildLevel = guildRow?.Level ?? NotificationLevel.Default;

        // Only this guild's channels — scope ids are global snowflakes, so we filter by the guild's
        // channel set rather than trust whatever channel rows the user might have elsewhere.
        var channelIds = (await _channels.GetByGuildIdAsync(guildId)).Select(c => c.Id).ToList();
        var channelRows = await _settings.GetManyAsync(userId, NotificationScope.Channel, channelIds);

        return Ok(
            new GuildNotificationSettingsResponse(
                guildLevel,
                guildRow?.SuppressEveryone ?? false,
                channelRows.Select(r => new ChannelNotificationSettingResponse(
                    r.ScopeId,
                    r.Level,
                    r.SuppressEveryone
                ))
            )
        );
    }

    // PUT /api/guilds/{guildId}/notification-settings
    [HttpPut("notification-settings")]
    public async Task<IActionResult> SetGuildLevel(
        long guildId,
        [FromBody] SetNotificationLevelRequest request
    )
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();
        if (!NotificationLevel.IsValid(request.Level))
            return BadRequest(new { error = "Invalid notification level." });

        await _settings.UpsertAsync(userId, NotificationScope.Guild, guildId, request.Level);
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    // PUT /api/guilds/{guildId}/notification-settings/suppress-everyone — toggle @everyone suppression
    // at the guild scope (creates the row at the default level if none exists).
    [HttpPut("notification-settings/suppress-everyone")]
    public async Task<IActionResult> SetGuildSuppressEveryone(
        long guildId,
        [FromBody] SetSuppressEveryoneRequest request
    )
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        await _settings.UpsertSuppressEveryoneAsync(
            userId,
            NotificationScope.Guild,
            guildId,
            request.Value
        );
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/notification-settings — reset guild scope to the global default.
    [HttpDelete("notification-settings")]
    public async Task<IActionResult> ResetGuildLevel(long guildId)
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        await _settings.DeleteAsync(userId, NotificationScope.Guild, guildId);
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    // PUT /api/guilds/{guildId}/channels/{channelId}/notification-settings
    [HttpPut("channels/{channelId:long}/notification-settings")]
    public async Task<IActionResult> SetChannelLevel(
        long guildId,
        long channelId,
        [FromBody] SetNotificationLevelRequest request
    )
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();
        if (!NotificationLevel.IsValid(request.Level))
            return BadRequest(new { error = "Invalid notification level." });

        var channel = await _channels.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            return NotFound(new { error = "Channel not found in this guild." });

        await _settings.UpsertAsync(userId, NotificationScope.Channel, channelId, request.Level);
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    // PUT /api/guilds/{guildId}/channels/{channelId}/notification-settings/suppress-everyone
    [HttpPut("channels/{channelId:long}/notification-settings/suppress-everyone")]
    public async Task<IActionResult> SetChannelSuppressEveryone(
        long guildId,
        long channelId,
        [FromBody] SetSuppressEveryoneRequest request
    )
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        var channel = await _channels.GetByIdAndGuildIdAsync(channelId, guildId);
        if (channel is null)
            return NotFound(new { error = "Channel not found in this guild." });

        await _settings.UpsertSuppressEveryoneAsync(
            userId,
            NotificationScope.Channel,
            channelId,
            request.Value
        );
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}/notification-settings — fall back to guild level.
    [HttpDelete("channels/{channelId:long}/notification-settings")]
    public async Task<IActionResult> ResetChannelLevel(long guildId, long channelId)
    {
        var userId = GetUserId();
        if (!await _guilds.IsMemberAsync(guildId, userId))
            return Forbid();

        await _settings.DeleteAsync(userId, NotificationScope.Channel, channelId);
        await _settings.SaveChangesAsync();
        return NoContent();
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
