namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Creates a managed guild invite. <see cref="ChannelId"/> is an optional landing channel
/// (null = guild-level invite). <see cref="MaxUses"/> null = unlimited;
/// <see cref="ExpiresInSeconds"/> null = never expires.
/// </summary>
public record CreateInviteRequest(long? ChannelId, int? MaxUses, long? ExpiresInSeconds);
