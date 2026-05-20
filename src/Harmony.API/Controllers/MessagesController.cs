using System.Security.Claims;
using Harmony.API.DTOs.Requests;
using Harmony.API.DTOs.Responses;
using Harmony.Core.Domain.Entities;
using Harmony.Core.Interfaces;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Services;
using Harmony.Infrastructure.Postgres;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/guilds/{guildId}/channels/{channelId}/messages")]
[Authorize]
[EnableRateLimiting("api")]
public class MessagesController : ControllerBase
{
    private readonly HarmonyDbContext _db;
    private readonly IMessagePublisher _publisher;
    private readonly ISnowflakeIdGenerator _snowflake;
    private readonly IMessageRepository _messageRepository;

    public MessagesController(
        HarmonyDbContext db,
        IMessagePublisher publisher,
        ISnowflakeIdGenerator snowflake,
        IMessageRepository messageRepository
    )
    {
        _db = db;
        _publisher = publisher;
        _snowflake = snowflake;
        _messageRepository = messageRepository;
    }

    // POST /api/guilds/{guildId}/channels/{channelId}/messages
    [HttpPost]
    public async Task<IActionResult> SendMessage(
        long guildId,
        long channelId,
        [FromBody] SendMessageRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        // Verify channel exists and belongs to guild
        var channel = await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId && c.GuildId == guildId
        );

        if (channel is null)
            return NotFound(new { error = "Channel not found." });

        // Verify user is a member of the guild
        var isMember = await _db.GuildMembers.AnyAsync(m =>
            m.GuildId == guildId && m.UserId == userId.Value
        );

        if (!isMember)
            return Forbid();

        // Validate content
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000)
            return BadRequest(
                new { error = "Message content must be between 1 and 2000 characters." }
            );

        // Generate Snowflake ID
        var messageId = _snowflake.NextId();
        var sentAt = DateTimeOffset.UtcNow;

        // Publish to RabbitMQ — consumer handles persistence asynchronously
        var evt = new MessageSentEvent(
            MessageId: messageId,
            ChannelId: channelId,
            GuildId: guildId,
            UserId: userId.Value,
            Content: request.Content,
            MessageType: request.MessageType ?? "text",
            AttachmentIds: request.AttachmentIds ?? [],
            MentionIds: request.MentionIds ?? [],
            ReplyToId: request.ReplyToId,
            SentAt: sentAt
        );

        await _publisher.PublishMessageSentAsync(evt);

        return Ok(
            new SendMessageResponse(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                UserId: userId.Value,
                Content: request.Content,
                MessageType: request.MessageType ?? "text",
                ReplyToId: request.ReplyToId,
                MentionIds: request.MentionIds ?? [],
                AttachmentIds: request.AttachmentIds ?? [],
                SentAt: sentAt.ToUnixTimeMilliseconds()
            )
        );
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/messages
    [HttpGet]
    public async Task<IActionResult> GetMessages(
        long guildId,
        long channelId,
        [FromQuery] int limit = 50,
        [FromQuery] long? before = null
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        // Verify channel exists and belongs to guild
        var channel = await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId && c.GuildId == guildId
        );

        if (channel is null)
            return NotFound(new { error = "Channel not found." });

        // Verify user is a member of the guild
        var isMember = await _db.GuildMembers.AnyAsync(m =>
            m.GuildId == guildId && m.UserId == userId.Value
        );

        if (!isMember)
            return Forbid();

        // Clamp limit
        limit = Math.Clamp(limit, 1, 100);

        var messages = await _messageRepository.GetChannelMessagesAsync(channelId, limit, before);

        var response = messages.Select(m => new MessageResponse(
            MessageId: m.MessageId,
            ChannelId: m.ChannelId,
            GuildId: guildId,
            UserId: m.UserId,
            Content: m.IsDeleted ? string.Empty : m.Content,
            MessageType: m.MessageType,
            IsDeleted: m.IsDeleted,
            IsEdited: m.IsEdited,
            ReplyToId: m.ReplyToId,
            MentionIds: m.IsDeleted ? [] : m.MentionIds,
            AttachmentIds: m.IsDeleted ? [] : m.AttachmentIds,
            SentAt: ((DateTimeOffset)m.CreatedAt).ToUnixTimeMilliseconds(),
            EditedAt: m.EditedAt.HasValue
                ? ((DateTimeOffset)m.EditedAt.Value).ToUnixTimeMilliseconds()
                : null
        ));

        return Ok(response);
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}
    [HttpDelete("{messageId}")]
    public async Task<IActionResult> DeleteMessage(long guildId, long channelId, long messageId)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        // Verify channel exists and belongs to guild
        var channel = await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId && c.GuildId == guildId
        );

        if (channel is null)
            return NotFound(new { error = "Channel not found." });

        // Get message to verify ownership
        var message = await _messageRepository.GetByIdAsync(messageId);

        if (message is null)
            return NotFound(new { error = "Message not found." });

        // Only message author or guild owner can delete
        var isOwner = await _db.GuildMembers.AnyAsync(m =>
            m.GuildId == guildId && m.UserId == userId.Value && m.IsOwner
        );

        if (message.UserId != userId.Value && !isOwner)
            return Forbid();

        await _publisher.PublishMessageDeletedAsync(
            new MessageDeletedEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                DeletedByUserId: userId.Value,
                DeletedAt: DateTimeOffset.UtcNow
            )
        );

        return NoContent();
    }

    // PATCH /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}
    [HttpPatch("{messageId}")]
    public async Task<IActionResult> EditMessage(
        long guildId,
        long channelId,
        long messageId,
        [FromBody] EditMessageRequest request
    )
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        // Validate content
        if (string.IsNullOrWhiteSpace(request.Content) || request.Content.Length > 2000)
            return BadRequest(
                new { error = "Message content must be between 1 and 2000 characters." }
            );

        // Get message to verify ownership
        var message = await _messageRepository.GetByIdAsync(messageId);

        if (message is null)
            return NotFound(new { error = "Message not found." });

        // Only message author can edit
        if (message.UserId != userId.Value)
            return Forbid();

        await _publisher.PublishMessageEditedAsync(
            new MessageEditedEvent(
                MessageId: messageId,
                ChannelId: channelId,
                GuildId: guildId,
                EditedByUserId: userId.Value,
                NewContent: request.Content,
                EditedAt: DateTimeOffset.UtcNow
            )
        );

        return NoContent();
    }

    // --- Helpers ---

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");

        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
