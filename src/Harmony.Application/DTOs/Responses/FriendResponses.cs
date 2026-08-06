namespace Harmony.Application.DTOs.Responses;

// One accepted friend — the other user's public identity plus when the friendship
// was established (the row's UpdatedAt at accept time).
public record FriendResponse(
    long Id,
    string Username,
    string? AvatarKey,
    string? BannerKey,
    long Since
);

// One pending friend request the caller is party to. Direction tells the client
// whether to render an Accept/Decline ("incoming") or a Cancel ("outgoing") action.
public record PendingFriendResponse(
    long Id,
    string Username,
    string? AvatarKey,
    string? BannerKey,
    string Direction, // "incoming" | "outgoing"
    long CreatedAt
);
