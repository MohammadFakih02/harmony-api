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

/// <summary>
/// Personal mute preferences: silence a guild, channel, or user. The suppression
/// effects (notifications, badges, typing/presence) are Phase 4 consumers of
/// IUserMuteRepository.IsMutedAsync; this controller is pure CRUD plus the
/// MuteExpired broadcast on manual unmute.
/// </summary>
[ApiController]
[Route("api/mutes")]
[Authorize]
[EnableRateLimiting("api")]
public class MutesController : HarmonyControllerBase
{
    private readonly IUserMuteRepository _mutes;
    private readonly IHubBroadcaster _broadcaster;

    public MutesController(IUserMuteRepository mutes, IHubBroadcaster broadcaster)
    {
        _mutes = mutes;
        _broadcaster = broadcaster;
    }

    // GET /api/mutes — the caller's currently-active mutes
    [HttpGet]
    public async Task<IActionResult> GetMyMutes()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var mutes = await _mutes.GetActiveMutesAsync(GetUserId(), now);

        return Ok(
            mutes.Select(m => new MuteResponse(m.TargetType, m.TargetId, m.MutedUntil, m.CreatedAt))
        );
    }

    // POST /api/mutes — upsert (re-muting updates the expiry)
    [HttpPost]
    public async Task<IActionResult> Mute([FromBody] CreateMuteRequest request)
    {
        // Shape (target type, future expiry) is enforced by CreateMuteRequestValidator.
        var me = GetUserId();

        var existing = await _mutes.GetAsync(me, request.TargetId, request.TargetType);
        if (existing is null)
        {
            existing = new UserMute
            {
                UserId = me,
                TargetId = request.TargetId,
                TargetType = request.TargetType,
                MutedUntil = request.MutedUntil,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            await _mutes.AddAsync(existing);
        }
        else
        {
            existing.MutedUntil = request.MutedUntil;
        }

        await _mutes.SaveChangesAsync();

        return Ok(
            new MuteResponse(
                existing.TargetType,
                existing.TargetId,
                existing.MutedUntil,
                existing.CreatedAt
            )
        );
    }

    // DELETE /api/mutes/{targetType}/{targetId} — manual unmute (idempotent)
    [HttpDelete("{targetType}/{targetId:long}")]
    public async Task<IActionResult> Unmute(string targetType, long targetId)
    {
        var me = GetUserId();
        var existing = await _mutes.GetAsync(me, targetId, targetType);
        if (existing is not null)
        {
            _mutes.Remove(existing);
            await _mutes.SaveChangesAsync();

            // Same event as auto-expiry so the client treats both identically.
            await _broadcaster.BroadcastMuteExpiredAsync(
                me,
                new MuteExpiredPayload(targetId, targetType)
            );
        }

        return NoContent();
    }

}
