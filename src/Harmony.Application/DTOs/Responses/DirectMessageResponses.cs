namespace Harmony.Application.DTOs.Responses;

// One participant of a DM channel (everyone except the caller). Public identity only.
public record DmParticipantResponse(long UserId, string Username, string? AvatarKey);

// One DM channel from the caller's perspective. Unified across 1:1 and group:
//   - IsGroup=false → Participants holds the single peer; Name and IconKey are null.
//   - IsGroup=true  → Participants holds every other member; Name is the group name
//     (empty string when unnamed — the client derives a label from the members);
//     IconKey is the group icon's storage key (null = glyph fallback).
// GuildId is intentionally absent — DMs have none; the client keys off the channel id.
public record DirectMessageChannelResponse(
    long ChannelId,
    bool IsGroup,
    string? Name,
    string? IconKey,
    long LastReadId,
    IReadOnlyList<DmParticipantResponse> Participants
);

// Whether the caller may currently send in a 1:1 DM (block / the peer's DM-privacy checklist), with
// a human reason to show when they can't. Lets the composer disable the input up front instead of
// letting a doomed send fail. Non-throwing mirror of MessageService.AuthorizeDmSendAsync.
public record DmSendGateResponse(bool CanSend, string? Reason);
