using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Harmony.Application.DTOs.Requests;
using Harmony.Application.DTOs.Responses;
using Harmony.API.Filters;
using Harmony.Application.Interfaces.Services; // For IHubBroadcaster
using Harmony.Application.Services;
using Harmony.Domain.Domain.Entities;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces; // For IMessagePublisher
using Harmony.Domain.Interfaces.Repositories;
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
    private readonly IMessagePublisher _publisher;
    private readonly IHubBroadcaster _broadcaster;
    private readonly IPermissionService _permissions;

    public ChannelsController(
        IChannelRepository channels,
        IGuildRepository guilds,
        ISnowflakeIdGenerator snowflake,
        IMessagePublisher publisher,
        IHubBroadcaster broadcaster,
        IPermissionService permissions
    )
    {
        _channels = channels;
        _guilds = guilds;
        _snowflake = snowflake;
        _publisher = publisher;
        _broadcaster = broadcaster;
        _permissions = permissions;
    }

    // POST /api/guilds/{guildId}/channels
    [HttpPost]
    [RequirePermission(Permission.ManageChannels)]
    public async Task<IActionResult> Create(long guildId, [FromBody] CreateChannelRequest request)
    {
        var guild = await _guilds.GetByIdAsync(guildId);
        if (guild is null)
            return NotFound();

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

        var response = ToResponse(channel);

        // REAL-TIME BROADCAST: Notify connected clients in the Guild Group
        await _broadcaster.BroadcastChannelUpdatedAsync(response, guildId);

        return CreatedAtAction(nameof(GetById), new { guildId, channelId = channel.Id }, response);
    }

    // PATCH /api/guilds/{guildId}/channels/{channelId}
    [HttpPatch("{channelId:long}")]
    [RequirePermission(Permission.ManageChannels)]
    public async Task<IActionResult> Update(
        long guildId,
        long channelId,
        [FromBody] UpdateChannelRequest request
    )
    {
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

        var response = ToResponse(channel);

        // REAL-TIME BROADCAST: Notify connected clients of channel metadata updates
        await _broadcaster.BroadcastChannelUpdatedAsync(response, guildId);

        return Ok(response);
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}
    [HttpDelete("{channelId:long}")]
    [RequirePermission(Permission.ManageChannels)]
    public async Task<IActionResult> Delete(long guildId, long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        await _channels.DeleteAsync(channel);
        await _channels.SaveChangesAsync();

        // 1. ASYNC DECOUPLED CLEANUP: Publish the deletion event to RabbitMQ.
        //    Consumers purge ScyllaDB partitions and the Postgres search index.
        await _publisher.PublishChannelDeletedAsync(
            new ChannelDeletedEvent(channelId, guildId, DateTimeOffset.UtcNow)
        );

        // 2. REAL-TIME BROADCAST: Tell clients to REMOVE this channel from the sidebar.
        //    Distinct from ChannelUpdated — clients must navigate away if viewing it.
        await _broadcaster.BroadcastChannelDeletedAsync(channelId, guildId);

        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/channels/{channelId}/category
    // Moves a channel into a category, or clears it back to top-level when CategoryId is null.
    // Separate from Update above because that endpoint's "null = don't change" convention can't
    // express "clear the category" — this one always applies exactly what's provided. Places the
    // moved channel at the bottom of its new group (Discord's drop behavior).
    [HttpPatch("{channelId:long}/category")]
    [RequirePermission(Permission.ManageChannels)]
    public async Task<IActionResult> MoveToCategory(
        long guildId,
        long channelId,
        [FromBody] MoveChannelCategoryRequest request
    )
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        if (request.CategoryId is { } categoryId)
        {
            var category = await _channels.GetByIdAsync(categoryId);
            if (category is null || category.GuildId != guildId || category.Type != "category")
                return BadRequest(new { error = "Target category does not exist in this guild." });
        }

        var siblings = await _channels.GetByGuildIdAsync(guildId);
        var maxPosition = siblings
            .Where(c => c.Id != channelId && c.CategoryId == request.CategoryId && c.Type != "category")
            .Select(c => (int?)c.Position)
            .DefaultIfEmpty(-1)
            .Max()!.Value;

        channel.CategoryId = request.CategoryId;
        channel.Position = maxPosition + 1;
        await _channels.SaveChangesAsync();

        var response = ToResponse(channel);
        await _broadcaster.BroadcastChannelUpdatedAsync(response, guildId);

        return Ok(response);
    }

    // PATCH /api/guilds/{guildId}/channels/reorder
    [HttpPatch("reorder")]
    [RequirePermission(Permission.ManageChannels)]
    public async Task<IActionResult> Reorder(
        long guildId,
        [FromBody] List<ReorderChannelRequest> request
    )
    {
        // DECOUPLED TRANSACTION: Map requests to basic C# Value Tuples
        var updates = request.Select(r => (r.ChannelId, r.Position)).ToList();
        await _channels.ReorderAsync(updates);

        var channels = await _channels.GetByGuildIdAsync(guildId);

        // REAL-TIME BROADCAST: push each moved channel so other clients' stores re-sort.
        // (A single-channel "invalidation" broadcast isn't enough — the client's
        // ChannelUpdated handler patches that one channel, it doesn't refetch the list.)
        var movedIds = updates.Select(u => u.ChannelId).ToHashSet();
        foreach (var channel in channels.Where(c => movedIds.Contains(c.Id)))
        {
            await _broadcaster.BroadcastChannelUpdatedAsync(ToResponse(channel), guildId);
        }

        return Ok(channels.OrderBy(c => c.Position).Select(ToResponse));
    }

    // GET /api/guilds/{guildId}/channels
    // The guild-level [RequirePermission] is a coarse gate (non-members → 403); each channel is
    // then filtered by its OWN resolved ViewChannel so override-hidden channels (e.g. #staff)
    // never appear in the list — not just blocked on entry.
    [HttpGet]
    [RequirePermission(Permission.ViewChannel)]
    public async Task<IActionResult> GetAll(long guildId)
    {
        var userId = GetUserId();
        var channels = await _channels.GetByGuildIdAsync(guildId);

        var visible = new List<ChannelResponse>(channels.Count);
        foreach (var channel in channels)
        {
            if (await _permissions.HasAsync(userId, guildId, Permission.ViewChannel, channel.Id))
                visible.Add(ToResponse(channel));
        }

        return Ok(visible);
    }

    // GET /api/guilds/{guildId}/channels/{channelId}
    [HttpGet("{channelId:long}")]
    [RequirePermission(Permission.ViewChannel)]
    public async Task<IActionResult> GetById(long guildId, long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        return Ok(ToResponse(channel));
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/permissions
    // The caller's effective capabilities in this channel, computed server-side so the client
    // never reasons about permission bits. canSend factors in the live timeout (which the
    // cached resolver omits); the others are pure resolved bits.
    [HttpGet("{channelId:long}/permissions")]
    public async Task<IActionResult> GetMyCapabilities(long guildId, long channelId)
    {
        var userId = GetUserId();
        var bits = await _permissions.ResolveAsync(userId, guildId, channelId);

        bool Has(Permission p) => (bits & (long)p) == (long)p;

        var member = await _guilds.GetMemberAsync(guildId, userId);
        var timedOut =
            member?.CommunicationDisabledUntil is { } until
            && until > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var canView = Has(Permission.ViewChannel);
        return Ok(new ChannelCapabilitiesResponse(
            CanView: canView,
            CanSend: canView && Has(Permission.SendMessage) && !timedOut,
            CanAttach: canView && Has(Permission.AttachFiles),
            CanManageMessages: Has(Permission.ManageMessages),
            CanManageChannels: Has(Permission.ManageChannels),
            CanPin: canView && Has(Permission.PinMessages),
            CanUseVideo: canView && Has(Permission.UseVideo),
            CanStream: canView && Has(Permission.Stream),
            TimedOut: timedOut
        ));
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/viewers
    // The member ids that can actually ViewChannel this channel — so the member sidebar can
    // hide members an override (e.g. a #staff deny) excludes. Same per-member resolution the
    // unread fan-out uses, cached per (user, channel), so repeated opens are cheap.
    [HttpGet("{channelId:long}/viewers")]
    [RequirePermission(Permission.ViewChannel)]
    public async Task<IActionResult> GetViewers(long guildId, long channelId)
    {
        var channel = await _channels.GetByIdAsync(channelId);
        if (channel is null || channel.GuildId != guildId)
            return NotFound();

        var memberIds = await _guilds.GetMemberIdsAsync(guildId);
        var viewers = new List<long>(memberIds.Count);
        foreach (var id in memberIds)
        {
            if (await _permissions.HasAsync(id, guildId, Permission.ViewChannel, channelId))
                viewers.Add(id);
        }

        return Ok(viewers);
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
