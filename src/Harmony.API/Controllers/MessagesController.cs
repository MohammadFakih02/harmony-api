using System.Security.Claims;
using Harmony.Core.DTOs.Requests;
using Harmony.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

[ApiController]
[Route("api/guilds/{guildId}/channels/{channelId}/messages")]
[Authorize]
[EnableRateLimiting("api")]
public class MessagesController : ControllerBase
{
    private readonly IMessageService _messageService;

    public MessagesController(IMessageService messageService)
    {
        _messageService = messageService;
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

        var response = await _messageService.SendMessageAsync(
            userId.Value,
            guildId,
            channelId,
            request
        );
        return Ok(response);
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

        var response = await _messageService.GetChannelMessagesAsync(
            userId.Value,
            guildId,
            channelId,
            limit,
            before
        );
        return Ok(response);
    }

    [HttpDelete("{messageId}")]
    public async Task<IActionResult> DeleteMessage(long guildId, long channelId, long messageId)
    {
        var userId = GetUserId();
        if (userId is null)
            return Unauthorized();

        await _messageService.DeleteMessageAsync(userId.Value, guildId, channelId, messageId);
        return NoContent();
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

        await _messageService.EditMessageAsync(
            userId.Value,
            guildId,
            channelId,
            messageId,
            request
        );
        return NoContent();
    }

    private long? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier) ?? User.FindFirst("sub");
        return claim is not null && long.TryParse(claim.Value, out var id) ? id : null;
    }
}
