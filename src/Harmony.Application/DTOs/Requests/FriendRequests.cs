namespace Harmony.Application.DTOs.Requests;

// POST /api/friends/request — send a friend request to a user by their (globally
// unique) username. Shape is validated by SendFriendRequestRequestValidator.
public record SendFriendRequestRequest(string Username);
