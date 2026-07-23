## 8. REAL-TIME MESSAGE FLOW (authoritative)

```
Client → REST POST /api/guilds/{guildId}/channels/{channelId}/messages
  → MessageService validates channel + resolved permissions (ViewChannel|SendMessage, not timed-out) + content, generates Snowflake ID
  → publishes MessageSentEvent to RabbitMQ (harmony.messages exchange, key message.sent)
  → returns 200 with provisional SendMessageResponse immediately

Background — ScyllaMessageConsumer (harmony.messages.scylla queue):
  → deserialize event
  → Polly retry (3 attempts, exp backoff capped 30s; JsonException → DLQ)
  → DEDUP GATE: IMessageDeduplicator.IsDuplicateAsync — skip if already processed
  → MessageConsumerHandler persists to ScyllaDB (messages_by_channel + messages_by_id)
     + creates mention notifications in Postgres
  → IHubBroadcaster broadcasts authoritative MessageReceived to channel:{id} group
  → IUnreadCountService.IncrementForChannelAsync (pipelined INCR + per-user UnreadCountUpdated)
  → BasicAck

Separately — SearchIndexConsumer (harmony.messages.search queue):
  → SearchIndexConsumerHandler writes Postgres MessagesSearch (FTS read model)
```
Client receives a provisional response via REST **and** the authoritative `MessageResponse` via
SignalR; reconciles by `MessageId`. The hub also supports `SendMessage` directly (returns
`HubResult<SendMessageResponse>`), but **REST is the primary write path**.

---

## 15. FULL APPLICATION FLOWS (reference — the complete intended behavior)

> These describe the *target* application end-to-end. Many are future phases; included so design
> decisions stay coherent across phases. Authentication and message flow are partly built; the rest
> are the spec to build toward.

**1. Registration** — `POST /api/auth/register` (rate limit 3/IP/hr) → validate (email, username
2–32, password 8+) → check email unused → Identity hashes (bcrypt) → snowflake id → insert `Users` +
default `NotificationPreferences` → JWT (15 min) + rotate refresh token (hashed, stored) → JWT in
body + httpOnly refresh cookie. Angular saves JWT in memory, redirects to onboarding. *(BUILT §5.68 —
registration now ALSO sends a verification email best-effort (`SendVerificationEmailAsync`, never fails
the registration itself on an SMTP hiccup); the account is usable immediately, `email_confirmed` just
starts false. See flow #1a.)*

**1a. Email verification (§5.68)** — nags only in Settings ▸ My Account (no global banner, nothing
blocks login/usage). `POST /api/auth/verify-email/request` (Authorize, 204, no-ops silently if already
confirmed or on a 60s per-user cooldown) → Identity's `GenerateEmailConfirmationTokenAsync` → email to
`{ClientUrl}/verify-email?uid={id}&token={urlencoded}` (Mailpit in dev). `POST
/api/auth/verify-email/confirm` (AllowAnonymous — the link may be opened with no session) →
`ConfirmEmailAsync` → 204 or 400. A guild can additionally require it: `Guilds.require_verified_email`
(PATCH by owner/`ManageGuild`) → both join paths (`InvitesController.Join`, `DiscoveryController.Join`)
reject an unconfirmed joiner with `403 {error, requiresVerifiedEmail: true}`.

**2. Login** — `POST /api/auth/login` (rate limit 5/IP/min) → find by email OR username, verify hash →
on invalid increment failure → 401 → account-status check → else JWT + refresh token + cookie. Redirect
to last guild/channel. *(BUILT §5.68 — a 2FA-enabled account branches here instead of issuing tokens: see
flow #2a. Response is the flat `LoginResponse(accessToken?, user?, twoFactorRequired, challengeToken?)` —
additive over the old bare-token shape, so a non-2FA login still looks identical to old clients.)*

**2a. Email 2FA challenge + remember-device (§5.68, no TOTP — every code is emailed).** If
`user.TwoFactorEnabled` and the `trusted_device` cookie doesn't match a live `TrustedDevices` row: mint a
Redis `2fa:challenge:{token}` (code + attempts, 10 min TTL), email the code, return
`{twoFactorRequired: true, challengeToken}` — **no tokens yet**. `POST /api/auth/2fa/verify`
(AllowAnonymous, `{challengeToken, code, rememberDevice}`) → validate (5-attempt cap, fails CLOSED if
Redis is down) → issue JWT + refresh cookie → if `rememberDevice`, also mint a 30-day `trusted_device`
cookie (hash stored in `TrustedDevices`). `POST /api/auth/2fa/resend` regenerates the code in the same
challenge. Enable/disable: `2fa/enable/request` (password + verified-email gate → emails a setup code) →
`2fa/enable/confirm` (code → `TwoFactorEnabled=true`); `2fa/disable` (password → flag off + deletes every
`TrustedDevices` row); `DELETE 2fa/trusted-devices` = "require 2FA on all devices again" without touching
the flag.

**2b. Google Sign-In (§5.68)** — frontend loads Google Identity Services, renders the official button,
gets back a signed ID token client-side (no redirect dance — no client secret needed anywhere, only the
public Client ID). `POST /api/auth/google {idToken}` (AllowAnonymous) → `GoogleJsonWebSignature
.ValidateAsync` server-side → resolve by existing Google-linked login, else by matching verified email
(auto-link, no extra confirmation) else auto-register (unique username from the email local-part,
`email_confirmed=true`, no password) → issue tokens. **Always bypasses 2FA**, even on a linked
2FA-enabled account (Google's own auth is the trust anchor for this path) → unverified Google email is
rejected outright (401) whether matching an existing account or brand-new.

**2c. Forgot / reset password (§5.68, pulled forward from "after OAuth").** `POST
/api/auth/forgot-password {email}` is **always 204** — unknown email, cooldown, and a genuine send
failure are all indistinguishable from the caller's side, by design (never reveal account existence).
`POST /api/auth/reset-password {userId, token, newPassword}` (AllowAnonymous, Identity's reset-token
provider) → on success, revokes **every** refresh-token family (not just one) **and** every
`TrustedDevices` row for the user — a leaked password must not leave any other session, or any
remembered 2FA device, alive.

**3. Token refresh** — `POST /api/auth/refresh` (browser auto-sends httpOnly cookie) → find token by
hash → not expired/revoked → **family reuse detection**: if same family already used → COMPROMISE →
revoke family, force logout, 401 → else rotate (new JWT + new refresh token, revoke old) + new cookie.
Angular retries the original request transparently. *(Has a 30 s grace window: a concurrent refresh
inside the window gets a fresh access token + EMPTY refresh token → controller skips Set-Cookie — this
is why the concurrency test asserts "exactly one Set-Cookie," not "exactly one 200." See §5.2.)*

**3a. Credential changes (§5.68)** — all three password-gated (`CheckPasswordAsync`, like enable/disable
2FA). A Google-only account (no `password_hash`) must `POST /api/auth/set-password {newPassword}` first
(no current-password field — the session itself is the proof); every change is 400 "Set a password
first." until then. **Change password** (`POST change-password {currentPassword, newPassword, code?}`)
→ revokes every other refresh token + every `TrustedDevices` row, then re-issues fresh tokens so the
acting browser stays signed in while every other session dies. **Change email**
(`POST change-email/request {password, newEmail, code?}`, cooldown-gated) → Identity's
`GenerateChangeEmailTokenAsync` (token bound to the NEW address) → link to
`{ClientUrl}/confirm-email-change?uid=&email=&token=` sent to the **new** address (old stays active
until confirmed) → `POST change-email/confirm` (AllowAnonymous) finishes it via `ChangeEmailAsync`.
**Change username** (`POST change-username {password, newUsername}`) → `SetUserNameAsync` → 409 on a
taken name → live `ProfileUpdated` broadcast (guilds + friends + own tabs) so other open tabs/sessions
render the new name without a refresh. **2FA step-up (added after this stage shipped, D20):** if the
account has 2FA enabled, `change-password`/`change-email` (NOT `change-username` — it's cosmetic, not a
recovery vector) first return `requiresCode: true` and email a fresh code to the account's **current**
address; the caller resubmits the same request with `code` filled in before anything actually changes.
Rationale: the 30-day trusted-device cookie means a 2FA login isn't always re-challenged, so a hijacked
session + a reused/phished password could otherwise pass the password-only gate on exactly the two
actions that matter for account recovery.

**4. App startup** — bootstrap → silent refresh → `GET /api/users/me`, `GET /api/guilds/me`,
`GET /api/users/me/notifications/unread-count` → establish SignalR with JWT →
`OnConnectedAsync`: add to `user:{userId}` group, add conn id to `session:{userId}` **ZSET**
(member=connectionId, score=heartbeat timestamp), set `user:{userId}:status=online` (TTL 60s), `ZADD
presence:online`, broadcast `OnlineStatus` to friends, load pending notifications → start 45 s
heartbeat. On failure → `/login`. *(Verified 2026-07-18: `session:{userId}` is a ZSET, not the plain
SET this sketch describes — the SET→ZSET migration (§5.61) fixed a ghost-connection bug where a
crashed API instance's stale ids never got cleaned up and suppressed both the offline and the next
online broadcast; `PresenceSweepService` (30s) reaps entries whose heartbeat score is >90s stale.)*

**5. Create guild** — `POST /api/guilds` → snowflake → insert `Guilds` (`IsPublic=false`,
`MemberCount=1`) → creator `GuildMembers` row (`IsOwner=true`) → default `@everyone` role (`IsDefault`,
implicit — no per-member `RoleAssignment` row needed) → return guild. *(Verified 2026-07-18: guild-create
seeds **NO channels** and mints **no invite code** — the old permanent `Guilds.invite_code` column was
dropped (§5.23, invite-management); a guild starts genuinely empty of channels, and an owner must
explicitly create one AND a `GuildInvites` row before anyone else can join. Only the DevSeed tool
pre-populates channels for its test guild.)*

**6. Join via invite** — `GET /api/invites/{code}` (preview, membership-agnostic) → `POST
/api/invites/{code}/join` → insert `GuildMember` (implicit `@everyone`, no `RoleAssignment` row) →
`use_count++`, `member_count++` → post the guild's welcome message (to `welcome_channel_id`, or the
first text channel if unset, or the default greeting if `welcome_message` is null — suppressed
entirely if `system_messages_enabled=false`) → broadcast `MemberJoined`. *(BUILT §5.23+§5.25+§5.68 —
join re-validates: invite alive (not expired/exhausted), not already a member, **not banned**
(`GuildBans`, §5.25) → returns the existing ban-403 shape with the reason if set, **and** — if the
guild has `RequireVerifiedEmail` on (§5.68) — the joiner's `email_confirmed` → 403
`{error, requiresVerifiedEmail: true}`. A separate `GET /api/invites/{code}/embed` is a soft,
always-200 preview for rendering an inline invite card in chat (§5.55) — expired/invalid still 200s
with a "expired" status rather than 404ing, so old invite links in message history don't spam console
errors. Frontend: `/invite/:code` landing page (outside-the-app only) + the inline chat embed
(`…/invite/{code}` link in any message renders a card with Join, §5.24 Batch C).)*

**7. Load guild** — `GET /api/guilds/{id}` (verify member) → `/channels` (**filtered server-side to
channels you can `ViewChannel`** — override-hidden channels like `#staff` are omitted from the list, not
just blocked on entry) →
`/roles` → `/members?limit=50` (online first, highest hoisted role for color) → hub `JoinGuild(guildId)`
adds to `guild:{guildId}` group. *(Verified 2026-07-18: there is no flat guild-wide `/voice-states`
endpoint — a voice channel's live roster is fetched per-channel, `GET
/api/channels/{channelId}/voice/participants`, since the roster itself is Redis-backed, not a guild
sub-resource.)*

**8. Open text channel** — permission check `ViewChannel` + `ReadHistory` →
`GET /api/channels/{id}/messages?limit=50` (ScyllaDB; resolve senders from Postgres cached in Redis;
presigned MinIO URLs per attachment; reply previews from `messages_by_id`) → render in CDK Virtual
Scroll → hub `JoinChannel(channelId)` → `POST /api/channels/{id}/read` (reset
`unread:{userId}:{channelId}`, update `read_states`). *(Current impl: `ViewChannel`+`ReadHistory` are
**enforced** on message reads and `JoinChannel` now requires `ViewChannel` (§5.8); mark-read is
`POST /api/guilds/{guildId}/channels/{channelId}/read`; unread aggregate is `GET /api/users/me/unread`.
The client reads its per-channel capabilities from
`GET /api/guilds/{guildId}/channels/{channelId}/permissions` →
`{canView, canSend, canManageMessages, canManageChannels, timedOut}` (canSend is **timeout-aware**) to
gray the composer + gate edit/delete. The unread fan-out skips members who can't `ViewChannel` the
channel, so they don't accrue unread for hidden channels.)*

**9. Send message** — Angular validates (non-empty, <2000, slowmode) → optimistic message with temp id →
REST POST publishes `MessageSentEvent` → 200 provisional → consumer (dedup → persist dual-write →
broadcast `MessageReceived` → unread INCR fan-out) → client reconciles by `MessageId`. **On Scylla fail
after retries:** publish/broadcast `MessageFailed(messageId)` to the **sender** → Angular removes
optimistic message + error toast. *(This is `feature/application-resiliency` M2 — see §6.4.)*

**10. Receive (others)** — SignalR `MessageReceived` → skip if already present (sender's optimistic) →
append; auto-scroll if at bottom else "↓ New messages" banner → `UnreadCountUpdated` increments sidebar
badge → browser toast if unfocused + notifications enabled.

**11. Edit** — `PATCH /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}` (DM equivalent:
`PATCH /api/dm/{channelId}/messages/{messageId}`) → verify owner or `ManageMessages` → update both
Scylla tables (`is_edited`, `edited_at`) + `MessagesSearch` → broadcast `MessageEdited` → "(edited)".
*(Verified 2026-07-18: edit/delete are REST endpoints on `MessagesController`/`DirectMessagesController`
today, NOT `ChatHub` methods — `ChatHub` only exposes `SendMessage` for the message pipeline; an older
doc describing `EditMessage`/`DeleteMessage` as hub methods is stale.)*

**12. Delete** — `DELETE /api/guilds/{guildId}/channels/{channelId}/messages/{messageId}` (DM:
`DELETE /api/dm/{channelId}/messages/{messageId}`) → verify owner or `ManageMessages` → **soft
delete** (`is_deleted=true`, never hard delete) → remove from `MessagesSearch` → audit log
(`message_delete`) if moderator deleting someone else's message → broadcast `MessageDeleted` →
"Message deleted" placeholder.

**13. Reply** — send flow with `replyToId` → consumer stores `reply_to_id` → on load resolve referenced
message from `messages_by_id` for compact preview → click jumps (scroll+highlight, or
`?around={messageId}`).

**14. Older messages (infinite scroll)** — scroll-to-top → guard `isLoading`/`hasMore` →
`?before={oldestLoadedId}&limit=50` (`message_id < before`) → prepend, maintain scroll position;
`<50` → `hasMore=false`.

**15. File upload** — client validates (<50MB, allowed type) → presign (check `AttachFiles`,
server-side type/size, snowflake, pending `FileAttachment`, presigned PUT 5 min) → client uploads
directly to MinIO → confirm (`is_confirmed=true`, verify object + extract dims) → send with
`attachmentId`. Display: `GET /api/files/{id}` (verify `ViewChannel`, presigned GET 15 min, cached
14 min). *(Current impl, §5.9–§5.10 — file group fully BUILT, diverges from this sketch on these points:*
*(1) all endpoints are **nested** under the channel —
`POST /api/guilds/{guildId}/channels/{channelId}/files/presign` (AttachFiles via `[RequirePermission]`),*
*`POST .../files/{fileId}/confirm` (owner-gated), `GET .../files/{fileId}` (ViewChannel via*
*`[RequirePermission]`) — not flat `/api/files/*`; (2) confirm is a **client-confirm**, not a MinIO*
*webhook — the server StatObjects MinIO to verify the upload landed and reads the **authoritative***
*size/content-type from the store; for **images** ImageSharp decodes dims (which also serves as the*
*magic-byte check), for **every other allowed type** a per-type signature sniff verifies the bytes*
*(§5.36, Batch F #18 — allowlist now covers video/audio/pdf/text/zip, SVG excluded); (3) the `GET`*
*returns enriched*
*`FileDownloadResponse(metadata + 15-min presigned URL)`, cached ~14 min; (4) **send-side attachment*
*validation** (confirmed + owned + in-channel, ≤10) lives in `MessageService`; empty content is allowed*
*with ≥1 attachment; the message carries `AttachmentIds` only and the client resolves each via the `GET`*
*(static metadata + fresh URL — message hot path untouched). **(Superseded on two points since:** the hub
IS now the primary send path with `attachmentIds` parity — drain Slice 2; and `FileAttachment.GuildId`/
`ChannelId` are now **nullable** — DM/group uploads and user/guild assets all confirmed, §5.17/§5.47.)*
*(5) **§5.69 batch presign + server compression:** a page of history prewarms all its attachment URLs in
ONE call — `POST .../files/batch` (guild, ViewChannel-gated) / `POST /api/dm/{channelId}/files/batch`
(participant-gated), body `{fileIds}` (1..100), returns only confirmed rows scoped to that exact channel
(foreign/unknown ids silently omitted, never a 404); the client `MessageStore.prewarmAttachments` awaits
it (1500ms race) before rendering so images paint exact-sized with no skeleton/layout-shift. (6) **server
compression:** at confirm, large non-GIF chat images (>1024px either axis) get a display-only WebP
thumbnail at `{minio_key}_thumb` (≤800×600) → `FileDownloadResponse.ThumbnailUrl`; the inline `<img>`
uses the thumbnail, lightbox/copy/open/download always use the untouched original. Avatars/banners/icons
are capped IN PLACE (512/1280px, GIFs exempt) — NON-NEGOTIABLE #8, the client cropper also downscales but
a raw PUT of a 4K original is still capped server-side.)*

**16. Pin** — Guild: `GET/PUT/DELETE /api/guilds/{guildId}/channels/{channelId}/pins[/{messageId}]`
(`PinsController` — list needs `ViewChannel`, pin/unpin need `PinMessages`, both `[RequirePermission]`
with overrides applied) → `PUT` is an idempotent upsert into `pinned_messages` (Scylla clustering key)
→ broadcast `MessagePinned`/`MessageUnpinned` → audit log (`message_pin`/`message_unpin`). DM:
`GET/PUT/DELETE /api/dm/{channelId}/pins[/{messageId}]` — participant-gated only (any participant can
pin/unpin, no audit log — DMs have no `AuditLogs.guild_id` to attach to). *(BUILT §5.38; capped at 50
pins per channel.)*

**16a. Reactions (§5.64)** — Guild: `PUT/DELETE
/api/guilds/{guildId}/channels/{channelId}/messages/{messageId}/reactions` (`ReactionsController`,
channel-scoped `AddReactions` via `[RequirePermission]`; emoji travels in the PUT body / DELETE query
string, **never the route**). DM: `PUT/DELETE
/api/dm/{channelId}/messages/{messageId}/reactions` — participant-gated only. Both delegate to the
same `MessageService.AddReactionAsync`/`RemoveReactionAsync` (an idempotent Postgres upsert/delete on
`MessageReactions`, `ON CONFLICT DO NOTHING`) → broadcast `ReactionAdded`/`ReactionRemoved` to the
channel group (no audit log, no system message). A per-page `GetSummariesAsync` prefetch attaches
`{count, meReacted}` per emoji onto every `MessageResponse` at read time (channel history, DM history,
and pins all share the same `MapMessage` attach point).

**17. Typing** — debounce 500 ms → hub `StartTyping(channelId)` (rate limit 1/3 s, check `ViewChannel`,
`ZADD typing:{channelId}`, broadcast `TypingStarted` excl. sender). Client expires entries >3 s old.
Send → `StopTyping` → broadcast `TypingStopped`. *(BUILT §5.43 — diverges from this sketch: **no Redis ZSET**
(typing is ephemeral, broadcast straight to the `channel:{id}` group via `IHubBroadcaster` — NON-NEGOTIABLE
#2 kept; the typer's own client filters itself, so no `excl. sender`); **no username on the wire** —
`TypingStarted(userId, channelId)`, the client resolves the nickname-aware name from its own stores;
`StartTyping` access-checks via `ChatHub.CanAccessChannelAsync` (extracted from `JoinChannel`); client
throttles StartTyping to ≤1/3 s and `TypingStore` auto-expires a typer after **6 s** (+ clears on the
sender's `MessageReceived`); rate-limited by the existing generic `RateLimitHubFilter` 20/10s bucket.)*

**18. Presence** — on connect set status + `ZADD presence:online` + broadcast `OnlineStatus` to
friends. Heartbeat 45 s resets TTL + re-ZADD. Custom status via `PATCH /api/users/me/status`. On
disconnect remove conn id from `session:{userId}` (a **ZSET** of connectionId→heartbeat-score, not a
SET — see flow #4); empty → offline + broadcast `OfflineStatus`, else keep online (other tab).
`PresenceSweepService` (30s cadence, 90s stale threshold — **not** a 10s ZSET sweep) is the
crash-recovery backstop for connections that never got a clean disconnect.

**19. DMs** — `POST /api/dm {targetUserId}` (checks the target's `dm_privacy`/`CanContactAsync`
checklist — friends always reachable, a `friends_only` stranger is rejected; find existing shared
`channel_id` else create `Channel` type=dm + two `DirectMessageChannels`) → `/channels/@me/{channelId}`.
Identical messaging flow, **no guild permission checks**. Hide via `PATCH /api/dm/{channelId}/hide`;
reappears on new message. The unread fan-out branches on channel type to read `DirectMessageChannels`
instead of `GuildMembers`.

**19a. Group DMs (§5.37)** — all under `/api/dm` (`DirectMessagesController`): `POST /group` create
(`CreateGroupDmRequest`, 2–9 other users required, capped at 10 total participants — each invitee must
individually pass the caller's `CanContactAsync` check) → `POST /{channelId}/participants` add (any
existing participant may add; idempotent if already in; posts a `group_join` system notice) →
`DELETE /{channelId}/participants/me` leave (posts a `group_leave` system notice; leaving a true 1:1
DM is rejected — "hide it instead") → `PATCH /{channelId}/name` rename + icon presign/confirm/remove
(any participant, §5.56 `AddChannelIconKey`). Every membership/name/icon change broadcasts a coarse
`DmChannelUpdated(channelId)` so every participant's client resyncs the DM list rather than trying to
patch fields individually.

**20. Friends** — request `POST /api/friends/request {targetUsername}` (not blocked, not already
friends/pending) → `Friends` pending + `FriendRequest` notification. Accept
`PATCH /api/friends/{requesterId}/accept` → accepted + `FriendAccepted` both. Decline/remove → delete +
`FriendRemoved`.

**21. Blocking** — `POST /api/users/{id}/block` → `UserBlocks` (+ delete `Friends` if any, `FriendRemoved`).
Effects: filter blocked user's messages client-side, suppress presence/DM/mention to blocker. Unblock →
delete.

**22. Muting** — guild/channel/user via `POST /api/mutes`. Guild: suppress notifications, badges dimmed.
Channel: no notifications, badge suppressed. User: no mention notifications, hide typing/presence.
`MuteExpiryService` (60 s) deletes expired + `MuteExpired`. Manual unmute → delete + `MuteExpired`.

**23. Voice (LiveKit, §5.57–§5.63) — completely rewritten from the original sketch; verified
2026-07-18.** `POST/GET /api/channels/{channelId}/voice/{token,participants}` (`VoiceController`)
mints the LiveKit token/roster; the live room itself is managed entirely through **hub methods, not
REST**: `JoinVoice(channelId)` (guild: `ConnectVoice` w/ overrides; DM/group-DM: participant; enforces
`UserLimit` — full room rejected unless already in-room or holding `MoveMembers`; re-arms sticky
server-mute/deafen from `voice:moderation:{guildId}`; ends a live incoming-call ring if answering),
`LeaveVoice(channelId)` (no authz — the service resolves the caller's current room authoritatively),
`UpdateVoiceState(isMuted, isDeafened, isVideoOn, isStreaming)` (clamps video/stream to
`UseVideo`/`Stream` in a guild room; unclamped in DMs). Publish grants are computed server-side into
the LiveKit token per-source: mic needs `Speak`, camera needs `UseVideo`, screen+its-audio needs
`Stream` (DM rooms grant every source — no per-channel permission concept there). All voice state
(roster, mute/deafen/video/streaming flags) lives in **Redis** (`voice:channel:{channelId}` etc,
docs/redis-and-events.md) — `VoiceStates` in Postgres exists in the schema but is not the live source
of truth. **Moderation (§5.62, hard enforcement):** `ModerateVoiceState(targetUserId, serverMute?,
serverDeafen?)` — `MuteMembers`/`DeafenMembers` resolved against the target's *current room* (never
client-supplied), enforced both as a sticky Redis flag AND a hard LiveKit publish/subscribe change via
`ILiveKitRoomService`; `MoveVoiceParticipant(targetUserId, toChannelId)` — needs `MoveMembers` on the
source channel plus the target's own `ConnectVoice` on the destination, force-relocates via a targeted
`VoiceForceMoved` broadcast + LiveKit room removal. There is no LiveKit webhook handler in this
codebase — crash/ghost cleanup is `VoiceStateSweepService` (30s), which reaps participants whose
presence has gone offline.

**23a. DM/group-DM calling — full ringing (§5.60).** Layered on top of voice via three more hub
methods: `StartCall(channelId)` (DM-only; requires the caller already `JoinVoice`'d the room + no other
participant present yet; NX-guarded Redis ring key `call:ring:{channelId}`, TTL 135s, so a duplicate
start is a silent no-op; broadcasts `IncomingCall` to the other participant(s); stages a
`PushKind.Call` outbox row for an offline callee). `CancelCall(channelId, missed)` — only the ring's
own caller may invoke; if `missed=true` and nobody ever joined, posts a `missed_call` system message.
`DeclineCall(channelId)` — notifies the caller (`CallDeclined`) and dismisses the decliner's own tabs
(`CallCancelled`); ends the ring outright in a 1:1, continues for the others in a group. `JoinVoice`
itself ends a live ring the moment a non-caller joins (answers), so a race against `CancelCall` can't
produce a bogus missed-call notice for an answered call.

**24. Notifications — five current producers, verified against `NotificationService` 2026-07-18.**
`NotificationService` creates exactly five `Notifications.type` values, each behind its own suppression
chain (preference → mute → block, plus per-guild/channel `NotificationSettings` level +
`suppress_everyone` for mentions): **`mention`** (server-detected from `@username`/`@role` text, §5.20/
§5.41, batch-created, respects the channel/guild notify level and everyone-suppression), **`reply`**,
**`friend_request`**, **`guild_invite`** (fired only from the server-side "invite a friend" endpoint,
`GuildInvitesController.InviteFriend` — NEVER trust a client's own claim of having invited someone),
**`message`** (the `all` notification-level producer, §5.65 — created only for recipients who opted
into "all" for that channel/guild; without this producer "all" behaved identically to "mentions").
Every producer stages a matching `PushOutbox` row in the **same transaction** as the `Notification`
insert (transactional outbox) + a best-effort live `NotificationReceived` push (suppressed only by
DnD) + a `NotificationBadgeUpdate(count)` broadcast so other open tabs' bells stay in sync. Plain DM
messages and calls are pushed too, but via a **different, non-`Notifications`-row path** — `PushKind.Dm`/
`PushKind.Call` rows staged directly by the message consumer / `ChatHub.StartCall`, resolving recipients
dynamically from the DM's live participant list at send time rather than being pre-resolved like the
five types above. Panel: `GET /api/notifications?limit=20`, mark read/read-all/clear-all. WebPush
delivery (§5.50) is offline-only (skipped if the recipient has a live SignalR connection), gated by
`NotificationPreference.PushEnabled`; see flow #28a for the dispatcher itself.

**25. Search — verified against `MessageSearchRepository` 2026-07-18; the mechanism differs from an
earlier claim that `ts_rank` relevance ordering was added, which does not match the current code.**
Guild: `GET /api/guilds/{guildId}/search?q=&channelId=&before=` (`SearchController`, no
`[RequirePermission]` — membership + a per-result `ViewChannel` filter are enforced inside
`SearchService` instead, since visibility is per-channel not per-endpoint). DM:
`GET /api/dm/{channelId}/search?q=&before=` — participant-gated. Both match
`content_search @@ plainto_tsquery('english', q)` **OR'd with an escaped `content ILIKE '%q%'`
fallback** (so a stop-word-only or punctuation-only query — which `plainto_tsquery` would silently drop
to nothing — still returns hits), and order by **`created_at DESC` (recency), not tsvector rank**. Guild
search over-fetches (`PageSize*4+1` raw rows) and trims after the per-channel `ViewChannel` filter,
since a result page can lose rows mid-filter; DM search has no such filter and fetches exactly one
page+1. Snippets are highlighted client-side. Click → navigate + `?around={messageId}` loads a window
centred on the target into the anchored history view.

**26. Guild member management (§5.25, `GuildMembersController` under
`/api/guilds/{guildId}/members`)** — `DELETE /{userId}` kick (`KickMembers`); `PUT/DELETE
/bans/{userId}` ban (optional `Reason` body) / unban (`BanMembers`); `GET /bans` list (`BanMembers`);
`PUT/DELETE /{userId}/timeout` set (`DurationSeconds` body) / clear timeout →
`communication_disabled_until`, checked in `SendMessage` resolution (`TimeoutMembers`); `PATCH
/me/nickname` (any member, self only) / `PATCH /{userId}/nickname` (`ManageNicknames`, for changing
someone else's). All `[RequirePermission]`-gated; hierarchy checks (can't act on the owner or a member
outranking you) and audit-log writes live in `IGuildMemberService`.

**27. Roles & permissions (§5.26, `RolesController` under `/api/guilds/{guildId}/roles`) — fully
BUILT, not a future item.** `GET` list (any member) · `POST` create / `PATCH /{roleId}` update /
`DELETE /{roleId}` delete (`ManageRoles`, can't grant a permission bit you don't hold yourself) ·
`PATCH /positions` bulk reorder (`ManageRoles`, routed before `{roleId}` to avoid shadowing) ·
`PUT/DELETE /{roleId}/members/{userId}` assign/unassign (`ManageRoles`, can't assign above your own
highest role). Every mutation invalidates the affected members' `perms:{userId}:{guildId}` Redis cache
and broadcasts `RoleCreated`/`RoleUpdated`/`RoleDeleted`/`MemberRoleUpdated` (the last carries the
member's **full current role-id set**, not a delta). Channel overrides (§5.8):
`PUT`/`DELETE /api/guilds/{guildId}/channels/{channelId}/overrides/{targetId}` (`ManageRoles`, upsert;
invalidates the role-target's whole-guild cache or the user-target's single cache entry; broadcasts
`ChannelOverridesChanged`).

**28. Invites (§5.23, `GuildInvitesController` + the flat `InvitesController`)** — Create:
`POST /api/guilds/{guildId}/invites`, authorized in-body as `CreateInvite OR ManageInvites` (an
attribute-level `[RequirePermission]` can't express OR). A companion `POST
/invites/invite-friend` mints an invite, DMs the link to an accepted friend, and files a
`guild_invite` notification (same OR-gate). List: `GET /api/guilds/{guildId}/invites` —
`ManageInvites` sees every invite in the guild; a `CreateInvite`-only caller sees only invites they
personally created. **Revoke: `DELETE .../invites/{code}` allows the invite's own creator OR
`ManageInvites`** — exactly mirroring own-message-delete semantics (§5.24 #7; a bug where only
`ManageInvites` could revoke, blocking a `CreateInvite`-only member from revoking their own mint, was
fixed here). Join/preview/embed live on the flat `/api/invites/{code}` route regardless of which
controller minted the code — see flow #6.

**28a. Push notification dispatch (§5.50, `PushNotificationService`)** — a `BackgroundService` that
wakes on an in-process nudge signal or a 5s crash-recovery poll, drains due `PushOutbox` rows in
batches of 32 (`next_attempt_at <= now`). Per row: skip if the recipient has a live SignalR connection
(push is offline-only) or is `dnd` or has `PushEnabled=false`; `dm`/`call` kind rows resolve recipients
dynamically from the DM's *current* participant list minus `exclude_user_ids` and get their own
mute/block check at send time (they never passed through `NotificationService`'s suppression chain);
every other kind was already fully suppression-checked when staged. Delivers to every registered
`UserPushSubscriptions` row via `IWebPushSender`; a `Gone` response prunes that subscription. Failed
sends get exponential backoff (`2^attempts * 30s`) up to 5 attempts, then the row is dropped
(at-least-once, not guaranteed delivery).

**29. Forwarding (§5.65 drain Slice 4 — server-verified "snapshot-forward")** —
`POST /api/guilds/{guildId}/channels/{channelId}/messages/forward` (DM:
`POST /api/dm/{channelId}/messages/forward`) → `MessageService.ForwardMessageAsync` loads the **source**
message, re-authorizes the forwarder against that source (guild `ViewChannel` or DM participation —
never trusts a client-supplied "I can see this" claim), and builds a server-authoritative
`MessageForwardSnapshot(AuthorId, AuthorName, Content, SentAt)` from the real row. The snapshot rides
alongside the new message through the normal send pipeline and persists as Scylla
`messages_by_channel.forward_snapshot` (a Scylla-only column — see docs/database-schemas.md; it does
NOT exist on `messages_by_id`). There is no separate client-composed "quote and repost" path in the
current backend.

**30. Audit log (§5.23, `AuditLogsController`)** — `GET /api/guilds/{id}/audit-log?limit=50&before?=
&action?=` (`ViewAuditLog`). Current producers, verified against `IAuditLogService.LogAsync` call
sites 2026-07-18: **members** (`member_kick`, `member_ban`+reason, `member_unban`, `member_timeout` —
both set and clear, `member_nickname_update`), **roles** (`role_create`/`update`/`delete`,
`member_role_update`), **invites** (`invite_create`/`delete`, the invite code masked to a 3-char prefix
in the logged `changes`), **messages** (`message_delete` — moderator deleting someone else's message
only, `message_pin`/`message_unpin`). **Gap worth knowing about:** the `AuditLogAction` enum also
*defines* `channel_create`/`channel_update`/`channel_delete` constants, but **nothing calls
`LogAsync` for them** — channel management is not actually audited today despite the enum implying it
is; don't assume a doc or a UI element claiming "channel changes are audited" is accurate without
re-checking `ChannelsController` first.

---