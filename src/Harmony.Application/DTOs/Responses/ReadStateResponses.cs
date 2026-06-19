namespace Harmony.Application.DTOs.Responses;

public record UnreadCountResponse(long ChannelId, long GuildId, int UnreadCount);
