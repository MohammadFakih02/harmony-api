namespace Harmony.Application.DTOs.Responses;

/// <summary>
/// A minted LiveKit join token plus everything the client needs to connect: the Cloud websocket
/// <see cref="Url"/> and the <see cref="RoomName"/> (always the channelId as a string). The client
/// hands the token to <c>livekit-client</c>'s <c>Room.connect(url, token)</c>.
/// </summary>
public record VoiceTokenResponse(string Token, string Url, string RoomName);
