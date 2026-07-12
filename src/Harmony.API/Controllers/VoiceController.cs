using System.Security.Claims;
using Harmony.Application.DTOs.Responses;
using Harmony.Application.Interfaces.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Voice/video access for a channel — flat-routed (not under <c>/guilds/{id}</c>) because a room can
/// be a guild voice channel OR a guild-less DM/group-DM call, same reasoning as DirectMessagesController.
/// The LiveKit room name is always the channelId (§5.57). Authorization mirrors
/// <c>ChatHub.CanConnectVoiceAsync</c>: guild channel → ConnectVoice (overrides applied); DM →
/// participant. The backend only mints tokens + serves the roster; media flows client ↔ LiveKit Cloud.
/// </summary>
[ApiController]
[Route("api/channels/{channelId:long}/voice")]
[Authorize]
[EnableRateLimiting("api")]
public class VoiceController : ControllerBase
{
    private readonly IChannelRepository _channels;
    private readonly IPermissionService _permissions;
    private readonly IDirectMessageRepository _dms;
    private readonly IUserRepository _users;
    private readonly ILiveKitTokenService _tokens;
    private readonly IVoiceStateService _voice;

    public VoiceController(
        IChannelRepository channels,
        IPermissionService permissions,
        IDirectMessageRepository dms,
        IUserRepository users,
        ILiveKitTokenService tokens,
        IVoiceStateService voice
    )
    {
        _channels = channels;
        _permissions = permissions;
        _dms = dms;
        _users = users;
        _tokens = tokens;
        _voice = voice;
    }

    // POST /api/channels/{channelId}/voice/token
    [HttpPost("token")]
    public async Task<IActionResult> CreateToken(long channelId)
    {
        var userId = GetUserId();
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null)
            return NotFound();

        if (!await CanConnectVoiceAsync(userId, channel))
            return Forbid();

        // UserLimit mirror of ChatHub.JoinVoice: a full guild channel refuses the token too, so a
        // client can't sidestep the hub gate by connecting media directly. Same exemptions —
        // already in the room (reconnect) or holding MoveMembers (Discord's bypass).
        if (channel.GuildId is { } limitGuildId && channel.UserLimit is int limit && limit > 0)
        {
            var participants = await _voice.GetChannelParticipantsAsync(channelId);
            if (
                participants.Count >= limit
                && participants.All(p => p.UserId != userId)
                && !await _permissions.HasAsync(userId, limitGuildId, Permission.MoveMembers, channelId)
            )
                return Conflict(new { error = "This voice channel is full." });
        }

        if (!_tokens.IsConfigured)
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Voice is not configured on this server." }
            );

        var user = await _users.GetByIdAsync(userId);
        var displayName = user?.UserName ?? userId.ToString();

        var sources = await ResolvePublishSourcesAsync(userId, channel);
        var token = _tokens.CreateToken(channelId, userId, displayName, sources);
        if (token is null)
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { error = "Voice is temporarily unavailable." }
            );

        return Ok(new VoiceTokenResponse(token, _tokens.Url, channelId.ToString()));
    }

    // GET /api/channels/{channelId}/voice/participants
    [HttpGet("participants")]
    public async Task<IActionResult> GetParticipants(long channelId)
    {
        var userId = GetUserId();
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null)
            return NotFound();

        if (!await CanConnectVoiceAsync(userId, channel))
            return Forbid();

        return Ok(await _voice.GetChannelParticipantsAsync(channelId));
    }

    private async Task<bool> CanConnectVoiceAsync(long userId, Channel channel) =>
        channel.GuildId is { } guildId
            ? await _permissions.HasAsync(userId, guildId, Permission.ConnectVoice, channel.Id)
            : await _dms.IsParticipantAsync(channel.Id, userId);

    /// <summary>
    /// The LiveKit sources this user may publish in this channel — the hard enforcement layer
    /// (LiveKit Cloud rejects a publish outside the token's grant). Guild: mic behind Speak,
    /// camera behind UseVideo, screen behind Stream — channel overrides applied. A Speak-less
    /// member joins listen-only (the client's mic-enable fails and the call stays receive-side).
    /// DM/group-DM: everything (permissions are guild-scoped).
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolvePublishSourcesAsync(long userId, Channel channel)
    {
        if (channel.GuildId is not { } guildId)
            return LiveKitTrackSources.All;

        var sources = new List<string>();
        if (await _permissions.HasAsync(userId, guildId, Permission.Speak, channel.Id))
            sources.Add(LiveKitTrackSources.Microphone);
        if (await _permissions.HasAsync(userId, guildId, Permission.UseVideo, channel.Id))
            sources.Add(LiveKitTrackSources.Camera);
        if (await _permissions.HasAsync(userId, guildId, Permission.Stream, channel.Id))
        {
            sources.Add(LiveKitTrackSources.ScreenShare);
            sources.Add(LiveKitTrackSources.ScreenShareAudio);
        }
        return sources;
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
