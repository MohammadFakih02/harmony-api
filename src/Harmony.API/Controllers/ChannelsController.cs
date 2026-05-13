using System.Security.Claims;
using Harmony.API.DTOs.Requests;
using Harmony.API.DTOs.Responses;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/guilds/{guildId:long}/channels")]
[Authorize]
[EnableRateLimiting("api")]
public class ChannelsController : ControllerBase
{
    private readonly IChannelRepository _channels;
    private readonly IGuildRepository _guilds;
    private readonly ISnowflakeIdGenerator _snowflake;

    public ChannelsController(
        IChannelRepository channels,
        IGuildRepository guilds,
        ISnowflakeIdGenerator snowflake
    )
    {
        _channels = channels;
        _guilds = guilds;
        _snowflake = snowflake;
    }

    // POST /api/guilds/{guildId}/channels
    [HttpPost]
    public async Task<IActionResult> Create(long guildId, [FromBody] CreateChannelRequest request)
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is null)
            return NotFound();

        // For now: only guild owner can create channels.
        // Will be replaced by permission resolution in feature/permission-resolution-service.
        if (guild.OwnerId != GetUserId())
            return Forbid();

        var validTypes = new[] { "text", "voice", "category" };
        if (!validTypes.Contains(request.Type))
            return BadRequest(
                new { error = "Invalid channel type. Must be text, voice, or category." }
            );

        var channel = new Channel
        {
            Id = _snowflake.NextId(),
            GuildId = guildId,
            Name = request.Name,
            Type = request.Type,
            Topic = request.Topic,
            Position = request.Position,
            CategoryId = request.CategoryId,
            IsNsfw = request.IsNsfw,
            SlowmodeSeconds = request.SlowmodeSeconds,
            Bitrate = request.Type == "voice" ? (request.Bitrate ?? 64000) : null,
            UserLimit = request.Type == "voice" ? request.UserLimit : null,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        await _channels.AddAsync(channel);
        await _channels.SaveChangesAsync();

        return CreatedAtAction(
            nameof(GetById),
            new { guildId, channelId = channel.Id },
            ToResponse(channel)
        );
    }

    // GET /api/guilds/{guildId}/channels
    [HttpGet]
    public async Task<IActionResult> GetAll(long guildId)
    {
        if (!await _guilds.IsMemberAsync(guildId, GetUserId()))
            return Forbid();

        var channels = await _channels.GetByGuildIdAsync(guildId);
        return Ok(channels.Select(ToResponse));
    }

    // GET /api/guilds/{guildId}/channels/{channelId}
    [HttpGet("{channelId:long}")]
    public async Task<IActionResult> GetById(long guildId, long channelId)
    {
        if (!await _guilds.IsMemberAsync(guildId, GetUserId()))
            return Forbid();

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        return Ok(ToResponse(channel));
    }

    // PATCH /api/guilds/{guildId}/channels/{channelId}
    [HttpPatch("{channelId:long}")]
    public async Task<IActionResult> Update(
        long guildId,
        long channelId,
        [FromBody] UpdateChannelRequest request
    )
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is null)
            return NotFound();

        if (guild.OwnerId != GetUserId())
            return Forbid();

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        if (request.Name is not null)
            channel.Name = request.Name;
        if (request.Topic is not null)
            channel.Topic = request.Topic;
        if (request.IsNsfw is not null)
            channel.IsNsfw = request.IsNsfw.Value;
        if (request.SlowmodeSeconds is not null)
            channel.SlowmodeSeconds = request.SlowmodeSeconds.Value;
        if (request.Bitrate is not null && channel.Type == "voice")
            channel.Bitrate = request.Bitrate;
        if (request.UserLimit is not null && channel.Type == "voice")
            channel.UserLimit = request.UserLimit;
        if (request.CategoryId is not null)
            channel.CategoryId = request.CategoryId;

        await _channels.SaveChangesAsync();

        return Ok(ToResponse(channel));
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}
    [HttpDelete("{channelId:long}")]
    public async Task<IActionResult> Delete(long guildId, long channelId)
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is null)
            return NotFound();

        if (guild.OwnerId != GetUserId())
            return Forbid();

        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        await _channels.DeleteAsync(channel);
        await _channels.SaveChangesAsync();

        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/channels/reorder
    [HttpPatch("reorder")]
    public async Task<IActionResult> Reorder(
        long guildId,
        [FromBody] List<ReorderChannelRequest> request
    )
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is null)
            return NotFound();

        if (guild.OwnerId != GetUserId())
            return Forbid();

        var channels = await _channels.GetByGuildIdAsync(guildId);
        var channelMap = channels.ToDictionary(c => c.Id);

        foreach (var item in request)
        {
            if (channelMap.TryGetValue(item.ChannelId, out var channel))
                channel.Position = item.Position;
        }

        await _channels.SaveChangesAsync();

        return Ok(channels.OrderBy(c => c.Position).Select(ToResponse));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static ChannelResponse ToResponse(Channel c) =>
        new(
            c.Id,
            c.GuildId,
            c.Name,
            c.Type,
            c.Topic,
            c.Position,
            c.CategoryId,
            c.IsNsfw,
            c.SlowmodeSeconds,
            c.Bitrate,
            c.UserLimit,
            c.CreatedAt
        );
}
