namespace Harmony.Application.Interfaces.Services;

/// <summary>
/// Canonical audit-log action-type strings, stored verbatim in <c>AuditLogs.action_type</c>
/// and shared by every producer (the guild-management trio — invites/members/roles — and the
/// moderator message-delete path). Centralized so producers and any future filter/UI never
/// drift on the wire value. All values fit the column's 64-char limit.
/// </summary>
public static class AuditLogAction
{
    // Members
    public const string MemberKick = "member_kick";
    public const string MemberBan = "member_ban";
    public const string MemberUnban = "member_unban";
    public const string MemberTimeout = "member_timeout";
    public const string MemberRoleUpdate = "member_role_update";

    // Invites
    public const string InviteCreate = "invite_create";
    public const string InviteDelete = "invite_delete";

    // Roles
    public const string RoleCreate = "role_create";
    public const string RoleUpdate = "role_update";
    public const string RoleDelete = "role_delete";

    // Channels
    public const string ChannelCreate = "channel_create";
    public const string ChannelUpdate = "channel_update";
    public const string ChannelDelete = "channel_delete";

    // Messages
    public const string MessageDelete = "message_delete";
}
