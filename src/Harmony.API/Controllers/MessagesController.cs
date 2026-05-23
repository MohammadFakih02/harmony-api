using System.Security.Claims;
using Harmony.Core.DTOs.Requests;
using Harmony.Core.DTOs.Responses;
using Harmony.Core.Interfaces.Repositories;
using Harmony.Core.Interfaces.Services;
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
    private readonly IMessageService _messageService;
    private readonly IMessageRepository _messageRepository;
    private readonly HarmonyDbContext _db;

    public MessagesController(
        IMessageService messageService,
        IMessageRepository messageRepository,
        HarmonyDbContext db
    )
    {
        _messageService = messageService;
        _messageRepository = messageRepository;
        _db = db;
    }

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

        try
        {
            var response = await _messageService.SendMessageAsync(
                userId.Value,
                guildId,
                channelId,
                request
            );
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

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

        var channel = await _db.Channels.FirstOrDefaultAsync(c =>
            c.Id == channelId && c.GuildId == guildId
        );
        if (channel is null)
            return NotFound(new { error = "Channel not found." });

        var isMember = await _db.GuildMembers.AnyAsync(m =>
            m.GuildId == guildId && m.UserId == userId.Value
        );
        if (!isMember)
            return Forbid();

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

    [HttpDelete("{messageId}")]
    public async Task<IActionResult> DeleteMessage(long guildId, long channelId, long messageId)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        try
        {
            await _messageService.DeleteMessageAsync(userId.Value, guildId, channelId, messageId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

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

        try
        {
            await _messageService.EditMessageAsync(
                userId.Value,
                guildId,
                channelId,
                messageId,
                request
            );
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
