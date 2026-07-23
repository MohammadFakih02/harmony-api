## 12. REFERENCE — Redis keys

> Verified against `src/Harmony.Infrastructure/Redis/*.cs` + `Services/PermissionService.cs` +
> `API/Filters/RateLimitHubFilter.cs` (2026-07-18). The ASP.NET Core rate-limit keys
> (`login:{ip}`, `user:w:{userId}`, `user:r:{userId}`, `anon:{ip}`, `assets:{ip}`, in
> `RateLimitingExtensions.cs`) are **in-memory `RateLimitPartition` keys, NOT Redis** — don't conflate
> them with the two real Redis hub-rate-limit keys below.

```
session:{userId}                 ZSET    member=connectionId, score=unix-sec last heartbeat. No key
                                          TTL — pruned by score (90s threshold). Live SignalR
                                          connections per user (multi-device).
user:{userId}:status             string  PUBLIC EFFECTIVE status online/away/dnd/offline, TTL 60s
user:{userId}:preferred          string  durable preferred online/away/dnd/invisible (cache; Postgres is truth), no TTL
user:{userId}:statusmsg          string  custom status text (cache; Postgres is truth); ""=none, no TTL
user:{userId}:idle               string  '1' while client reports 15-min idle (dead-man's switch), TTL 60s
presence:online                  ZSET    userId → last-activity unix-sec; swept by score cutoff
voice:channel:{channelId}        HASH    userId → JSON StoredState (GuildId, IsMuted, IsDeafened,
                                          IsVideoOn, IsStreaming, JoinedAt, IsServerMuted,
                                          IsServerDeafened). No TTL. The live voice-room roster.
voice:user:{userId}              string  = current channelId (single-room enforcement). No TTL.
voice:users                       SET    userId set, for sweep-enumeration against presence. No TTL.
voice:moderation:{guildId}        HASH    userId → JSON {Muted, Deafened}. No TTL (entry deleted when
                                          both false). Sticky server-mute/deafen surviving leave/rejoin.
call:ring:{channelId}             string  = callerId. TTL 135s. NX-claimed; presence = "this DM/group-DM
                                          is currently ringing." Backstops the client's 2-min ring timer.
unread:{userId}:{channelId}       int     unread count (cache; read_states is truth), no TTL
perms:{userId}:{guildId}          HASH    field=channelId ("0"=guild-level) → resolved bits. TTL 30s,
                                          refreshed on write; DEL (single or batched) on invalidation.
2fa:challenge:{token}             HASH    {userId, code, attempts}, TTL 10m. Login-time 2FA challenge —
                                          fails CLOSED if Redis is down (the one gate in this codebase
                                          that does NOT fail open).
2fa:{purpose}:{userId}            HASH    {code, attempts}, TTL 10m. Purpose-scoped step-up/setup code
                                          (§5.68) — purpose="setup" reproduces the pre-existing
                                          "2fa:setup:{userId}" key exactly; "change-password"/
                                          "change-email" are the newer step-up-gate purposes. Fails CLOSED.
email:cooldown:{purpose}:{userId} string  SET NX EX 60. Per-(purpose,user) transactional-email send
                                          cooldown (verify/reset/2fa/2fa-setup/change-email/…). Fails OPEN.
slowmode:{channelId}:{userId}     string  '1', SET NX EX=configured slowmode seconds. Per-(channel,user)
                                          send cooldown. Fails OPEN.
ratelimit:msg:{userId}            int     fixed window, TTL 1s, limit 5 — the SendMessage hub method.
ratelimit:hub:{method}:{userId}   int     fixed window, TTL 10s, limit 20 — every OTHER hub method.
dedup:msg:{eventType}:{messageId} string  '1' (TTL 60s) — per-event-type idempotency guard, SET NX wins
                                          the race. Fails OPEN.
requeue:msg:{eventType}:{messageId} int   out-of-order edit requeue attempts (TTL 2m, set on first
                                          increment); bounds edit-before-Sent → DLQ (§5.22).
```
The SignalR Redis **backplane** (`AddStackExchangeRedis`, `ChannelPrefix = "harmony"`) uses its own
internal pub/sub channel names for cross-instance fan-out — not an app-defined key pattern, don't
document it as one.

---

## 13. REFERENCE — Permission bits

> Verified against `src/Harmony.Domain/Domain/Enums/Permission.cs` (2026-07-18) — 28 bits, `1L<<27` is
> currently the highest defined; a doc claiming more or fewer bits is stale.

```csharp
[Flags] public enum Permission : long {
  None=0,
  // General
  ViewChannel=1L<<0, ManageChannels=1L<<1, ManageRoles=1L<<2, ManageGuild=1L<<3, CreateInvite=1L<<4,
  KickMembers=1L<<5, BanMembers=1L<<6, Administrator=1L<<7,   // Administrator bypasses all checks
  // Text
  SendMessage=1L<<8, SendReply=1L<<9, EmbedLinks=1L<<10, AttachFiles=1L<<11, ReadHistory=1L<<12,
  MentionEveryone=1L<<13, ManageMessages=1L<<14, PinMessages=1L<<15, AddReactions=1L<<16,
  // Voice
  ConnectVoice=1L<<17, Speak=1L<<18, MuteMembers=1L<<19, DeafenMembers=1L<<20, MoveMembers=1L<<21,
  Stream=1L<<22, UseVideo=1L<<23,
  // Moderation
  ViewAuditLog=1L<<24, TimeoutMembers=1L<<25, ManageInvites=1L<<26, ManageNicknames=1L<<27,
  //   ManageNicknames gates changing OTHER members' nicknames; your own is always allowed regardless.
}
// DefaultEveryone = ViewChannel|SendMessage|SendReply|EmbedLinks|AttachFiles|ReadHistory|AddReactions|
//                   CreateInvite|ConnectVoice|Speak|UseVideo|Stream — snapshotted onto @everyone at
//                   guild creation (existing guilds don't retroactively gain a bit added here later —
//                   e.g. Stream/UseVideo needed manual granting via the Roles UI when they were added).
```

---

## 14. REFERENCE — SignalR events

> Verified against `src/Harmony.Application/Interfaces/Hubs/IChatClient.cs` (30 methods) +
> `IHubBroadcaster`/`HubBroadcaster` + `src/Harmony.API/Hubs/ChatHub.cs` (the only hub class) —
> 2026-07-18. `ChatHub.ChannelGroup(id) = "channel:{id}"`, `GuildGroup(id) = "guild:{id}"`.

**Server → Client** (method — fan-out target — payload):
```
MessageReceived(MessageResponse)                    — Group(channel)
MessageDeleted(MessageDeletedPayload)                — Group(channel)
  (MessageId, ChannelId, GuildId?, DeletedByUserId, DeletedAt)
MessageEdited(MessageEditedPayload)                  — Group(channel)
  (MessageId, ChannelId, GuildId?, EditedByUserId, NewContent, EditedAt)
MessagePinned(MessagePinPayload) / MessageUnpinned(MessagePinPayload) — Group(channel)
  (MessageId, ChannelId)   -- shared payload shape for both events
TypingStarted(userId, channelId) / TypingStopped(userId, channelId) — Group(channel)
  -- positional, no username — client resolves the nickname-aware display name itself
ChannelUpdated(ChannelResponse) / ChannelDeleted(ChannelDeletedPayload) — Group(guild)
  (ChannelId, GuildId, DeletedAt)
ChannelOverridesChanged(ChannelOverridesChangedPayload) — Group(guild)
  (GuildId, ChannelId)
UnreadCountUpdated(UnreadCountPayload)                — User (all connections)
  (ChannelId, GuildId?, UnreadCount)   -- absolute, not a delta
MessageFailed(MessageFailedPayload)                   — User (sender only)
  (MessageId, ChannelId, GuildId?)
OnlineStatus(OnlineStatusPayload) / OfflineStatus(OfflineStatusPayload) — User (per friend) AND Group(guild)
  OnlineStatusPayload(UserId, Status, StatusMessage?); OfflineStatusPayload(UserId)
StatusChanged(StatusChangedPayload)                    — User (friends, masked) + Group(guild) + User (self, unmasked)
  (UserId, Status, StatusMessage?)
MuteExpired(MuteExpiredPayload)                        — User (mute owner)
  (TargetId, TargetType)
FriendRequest(FriendUserPayload) / FriendAccepted(FriendUserPayload) — User (addressee / both parties)
  (Id, Username, AvatarKey?, BannerKey?)
FriendRemoved(FriendRemovedPayload)                    — User (other party)
  (UserId)
NotificationReceived(NotificationPayload)              — User (owner)
  (Id, Type, ActorId, GuildId?, ChannelId?, MessageId?, CreatedAt)
NotificationBadgeUpdate(int unreadCount)               — User (owner)
MemberJoined(MemberJoinedPayload) / MemberRemoved(MemberRemovedPayload) / MemberUpdated(MemberUpdatedPayload) — Group(guild)
  MemberJoinedPayload(GuildId, GuildMemberResponse); MemberRemovedPayload(GuildId, UserId);
  MemberUpdatedPayload(GuildId, UserId, Nickname?, CommunicationDisabledUntil?)
Kicked(KickedPayload)                                   — User (affected user)
  (GuildId, Reason?, Banned)
RoleCreated(RoleResponse) / RoleUpdated(RoleResponse) / RoleDeleted(RoleDeletedPayload) — Group(guild)
  RoleDeletedPayload(GuildId, RoleId)
MemberRoleUpdated(MemberRoleUpdatedPayload)             — Group(guild)
  (GuildId, UserId, RoleIds[])   -- the member's FULL current role set, not a delta
DmChannelUpdated(DmChannelUpdatedPayload)               — Users(explicit participant set)
  (ChannelId)   -- coarse resync signal; client refetches the DM's current state
ProfileUpdated(ProfileUpdatedPayload)                    — Group(guild) AND User (self + friends)
  (UserId, AvatarKey?, Username?)   -- AvatarKey is ALWAYS the current real value (null = genuinely no
  --   avatar, applied unconditionally client-side); Username is null = untouched by this update
  --   (applied conditionally client-side). Never assume symmetry between the two fields.
GuildInvitesChanged(GuildInvitesChangedPayload)          — Group(guild)
  (GuildId)   -- coarse resync, carries no invite data itself
VoiceParticipantJoined(VoiceParticipantPayload) / VoiceStateUpdated(VoiceParticipantPayload) / VoiceParticipantLeft(VoiceParticipantLeftPayload) — Group(channel) + Group(guild) if a guild voice room
  VoiceParticipantPayload(ChannelId, GuildId?, UserId, IsMuted, IsDeafened, IsVideoOn, IsStreaming,
    IsServerMuted, IsServerDeafened, JoinedAt); VoiceParticipantLeftPayload(ChannelId, GuildId?, UserId)
VoiceForceMoved(VoiceForceMovedPayload)                  — User (the moved user)
  (FromChannelId, ToChannelId, GuildId?)
IncomingCall(IncomingCallPayload)                        — Users(participants minus caller)
  (ChannelId, CallerId, StartedAt)
CallCancelled(CallCancelledPayload)                      — Users(recipients)
  (ChannelId)
CallDeclined(CallDeclinedPayload)                        — User (caller)
  (ChannelId, UserId)
ReactionAdded(ReactionPayload) / ReactionRemoved(ReactionPayload) — Group(channel)
  (MessageId, ChannelId, GuildId?, Emoji, UserId)
```
`IHubBroadcaster` has exactly one `BroadcastXAsync` method per event above; fan-out targets are 1:1
between the interface and `HubBroadcaster`'s implementation — no drift found there as of 2026-07-18.

**Client → Server** (`ChatHub`, the only hub class — `[Authorize]`):
```
Heartbeat()                                              -- no authz; refreshes presence TTL
SetIdle(bool idle)                                       -- no authz; client-reported 15-min inactivity
JoinChannel(long channelId) / LeaveChannel(long channelId) -- Join throws HubException unless
                                                             CanAccessChannelAsync (guild: ViewChannel
                                                             w/ overrides; DM: participant). Leave: no authz.
JoinGuild(long guildId) / LeaveGuild(long guildId)        -- Join throws unless a guild member. Leave: no authz.
SendMessage(channelId, guildId?, content, attachmentIds?, replyToId?, nonce?) -> HubResult<SendMessageResponse>
  -- inline length/empty validation; real authz (ViewChannel|SendMessage, or DM participation +
  --   not-blocked) delegated to IMessageService; expected failures become a HubResult, not a throw.
StartTyping(channelId) / StopTyping(channelId)            -- StartTyping is a SILENT no-op (not a throw)
                                                              unless CanAccessChannelAsync.
JoinVoice(channelId)                                       -- throws unless CanConnectVoiceAsync (guild:
  ConnectVoice; DM: participant); enforces UserLimit (bypass: MoveMembers, or already in-room);
  re-arms sticky server mute/deafen from voice:moderation:{guildId}; ends a live ring if answering a call.
LeaveVoice(channelId)                                      -- no authz; the service resolves the
                                                               current room authoritatively.
UpdateVoiceState(isMuted, isDeafened, isVideoOn, isStreaming) -- clamps isVideoOn/isStreaming to
  UseVideo/Stream in a guild room; unclamped in a DM room (DMs have no per-channel permission concept).
ModerateVoiceState(targetUserId, serverMute?, serverDeafen?) -- throws unless target is in a guild
  voice room; MuteMembers gates serverMute, DeafenMembers gates serverDeafen, each checked independently.
MoveVoiceParticipant(targetUserId, toChannelId)             -- throws unless target is in a guild voice
  room; needs MoveMembers on the SOURCE channel + the target's own ConnectVoice on the destination;
  destination must be the same guild and Type=="voice".
StartCall(channelId)                                        -- DM-only (throws for guild channels);
  requires participant + caller already joined the voice room via JoinVoice + no other participant yet;
  NX ring claim (silent no-op on a duplicate start); best-effort stages a PushKind.Call outbox row.
CancelCall(channelId, missed)                                -- silent no-op unless the caller owns the
  live ring; posts a "missed_call" system message if missed=true and nobody else ever joined.
DeclineCall(channelId)                                       -- silent no-op unless participant + a
  live ring exists + it isn't your own ring; ends the ring outright in a 1:1 (group ring continues
  for the others).
```
