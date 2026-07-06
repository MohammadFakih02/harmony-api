namespace Harmony.Application.DTOs.Requests;

/// <summary>
/// Set the caller's notification level for a guild or channel scope. <see cref="Level"/> must be one
/// of the canonical <c>NotificationLevel</c> values (all | mentions | nothing). To reset a scope back
/// to the default, DELETE the setting instead of sending a level.
/// </summary>
public record SetNotificationLevelRequest(string Level);

/// <summary>
/// Register (or refresh) the caller's browser push subscription. Endpoint identifies the
/// device+origin at the push service; p256dh/auth are the client-generated encryption keys.
/// PUT is an upsert keyed by Endpoint — resubscribing or logging in as a different user in
/// the same browser updates/reassigns the existing row instead of duplicating it.
/// </summary>
public record SavePushSubscriptionRequest(string Endpoint, string P256dh, string AuthKey);
