using System.Text.Json.Serialization;

namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Creates a managed guild invite. <see cref="ChannelId"/> is an optional landing channel
/// (null = guild-level invite). <see cref="MaxUses"/> null = unlimited;
/// <see cref="ExpiresInSeconds"/> null = never expires.
/// </summary>
public record CreateInviteRequest(long? ChannelId, int? MaxUses, long? ExpiresInSeconds);

/// <summary>
/// Invite-a-friend: mint an invite and DM its link to <see cref="FriendId"/> (a current friend),
/// server-side. <see cref="MaxUses"/>/<see cref="ExpiresInSeconds"/> follow the same null semantics
/// as <see cref="CreateInviteRequest"/> (defaulting is the client's job — the Discord confirm step
/// suggests 1 use / 7 days). The client sends the snowflake id as a string (full precision), so read
/// it back from a string (MVC has no global LongStringConverter, unlike the hub).
/// </summary>
public record InviteFriendRequest(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long FriendId,
    int? MaxUses,
    long? ExpiresInSeconds
);
