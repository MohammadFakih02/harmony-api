namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Set the caller's notification level for a guild or channel scope. <see cref="Level"/> must be one
/// of the canonical <c>NotificationLevel</c> values (all | mentions | nothing). To reset a scope back
/// to the default, DELETE the setting instead of sending a level.
/// </summary>
public record SetNotificationLevelRequest(string Level);
