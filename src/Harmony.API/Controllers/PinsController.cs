using System.Security.Claims;
using Harmony.API.Filters;
using Harmony.Domain.Domain.Enums;
using Harmony.Domain.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Harmony.API.Controllers;

/// <summary>
/// Pinned-message endpoints for guild channels. Nested under the channel so the route-based
/// <see cref="RequirePermissionAttribute"/> applies channel-scoped bits (overrides included): listing
/// needs <see cref="Permission.ViewChannel"/>; pinning/unpinning need <see cref="Permission.PinMessages"/>.
/// DM pins live on <c>DirectMessagesController</c> (participant-gated). PUT-to-pin is an idempotent
/// upsert (the pin's Scylla clustering key is the message id).
/// </summary>
[ApiController]
[Route("api/guilds/{guildId:long}/channels/{channelId:long}/pins")]
[Authorize]
[EnableRateLimiting("api")]
public class PinsController : ControllerBase
{
    private readonly IMessageService _messages;

    public PinsController(IMessageService messages)
    {
        _messages = messages;
    }

    // GET /api/guilds/{guildId}/channels/{channelId}/pins
    [HttpGet]
    [RequirePermission(Permission.ViewChannel)]
    public async Task<IActionResult> GetPins(long guildId, long channelId, CancellationToken ct)
    {
        var pins = await _messages.GetPinsAsync(GetUserId(), guildId, channelId, ct);
        return Ok(pins);
    }

    // PUT /api/guilds/{guildId}/channels/{channelId}/pins/{messageId}
    [HttpPut("{messageId:long}")]
    [RequirePermission(Permission.PinMessages)]
    public async Task<IActionResult> Pin(
        long guildId,
        long channelId,
        long messageId,
        CancellationToken ct
    )
    {
        await _messages.PinMessageAsync(GetUserId(), guildId, channelId, messageId, ct);
        return NoContent();
    }

    // DELETE /api/guilds/{guildId}/channels/{channelId}/pins/{messageId}
    [HttpDelete("{messageId:long}")]
    [RequirePermission(Permission.PinMessages)]
    public async Task<IActionResult> Unpin(
        long guildId,
        long channelId,
        long messageId,
        CancellationToken ct
    )
    {
        await _messages.UnpinMessageAsync(GetUserId(), guildId, channelId, messageId, ct);
        return NoContent();
    }

    private long GetUserId() => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
