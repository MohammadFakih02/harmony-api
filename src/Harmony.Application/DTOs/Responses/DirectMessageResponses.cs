namespace Harmony.Application.DTOs.Responses;

// One DM channel from the caller's perspective: the channel id, the peer's public
// identity, and the caller's last-read marker. GuildId is intentionally absent — DMs
// have none; the client keys off the channel id.
public record DirectMessageChannelResponse(
    long ChannelId,
    long PeerId,
    string PeerUsername,
    string? PeerAvatarKey,
    long LastReadId
);
