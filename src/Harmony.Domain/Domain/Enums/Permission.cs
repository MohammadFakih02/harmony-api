namespace Harmony.Domain.Domain.Enums;

/// <summary>
/// Guild/channel permission flags, stored as a <c>long</c> bitmask on roles and
/// channel overrides (<c>permission_bits</c>, <c>allow_bits</c>, <c>deny_bits</c>).
///
/// <see cref="Administrator"/> bypasses every check during resolution.
/// </summary>
[Flags]
public enum Permission : long
{
    None = 0,

    // General
    ViewChannel = 1L << 0,
    ManageChannels = 1L << 1,
    ManageRoles = 1L << 2,
    ManageGuild = 1L << 3,
    CreateInvite = 1L << 4,
    KickMembers = 1L << 5,
    BanMembers = 1L << 6,
    Administrator = 1L << 7, // bypasses all checks

    // Text
    SendMessage = 1L << 8,
    SendReply = 1L << 9,
    EmbedLinks = 1L << 10,
    AttachFiles = 1L << 11,
    ReadHistory = 1L << 12,
    MentionEveryone = 1L << 13,
    ManageMessages = 1L << 14,
    PinMessages = 1L << 15,
    AddReactions = 1L << 16,

    // Voice
    ConnectVoice = 1L << 17,
    Speak = 1L << 18,
    MuteMembers = 1L << 19,
    DeafenMembers = 1L << 20,
    MoveMembers = 1L << 21,
    Stream = 1L << 22,
    UseVideo = 1L << 23,

    // Moderation
    ViewAuditLog = 1L << 24,
    TimeoutMembers = 1L << 25,
    ManageInvites = 1L << 26,
    ManageNicknames = 1L << 27, // change OTHER members' guild nicknames (changing your own is always allowed)

    /// <summary>
    /// Default permission set granted to the <c>@everyone</c> role on guild creation:
    /// members can view, chat, react, attach, invite, and use voice — but cannot moderate.
    /// The bits are snapshotted onto the role row at creation, so widening this set only
    /// affects NEW guilds — existing guilds grant the new bit via the Roles UI.
    /// </summary>
    DefaultEveryone =
        ViewChannel
        | SendMessage
        | SendReply
        | EmbedLinks
        | AttachFiles
        | ReadHistory
        | AddReactions
        | CreateInvite
        | ConnectVoice
        | Speak
        | UseVideo
        | Stream,
}
