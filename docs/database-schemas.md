## 10. REFERENCE — PostgreSQL schema

> Everything relational lives in PostgreSQL via EF Core + Npgsql. IDs are snowflake `bigint`
> (`created_at` = snowflake timestamp) except `RefreshTokens`/`TrustedDevices` (uuid + random token).
> `MessagesSearch` is a **standalone read model** — its FK to `Channels` was dropped
> (`DecoupleSearchIndex`). `MessageReactions`/`Notifications`/`PushOutbox` also carry FK-less
> `message_id`/`channel_id` pointers into ScyllaDB — deliberate cross-store design, not a bug.
> **Column-naming quirk (verified against `HarmonyDbContextModelSnapshot.cs`, 2026-07-18):**
> almost every column is snake_case, but `Users.Bio`, `Users.StatusMessage`, `Users.AccountStatus`,
> `Users.CreatedAt`, and `Guilds.RequireVerifiedEmail` have **no** `HasColumnName` override and are
> stored PascalCase in the actual DB — the one inconsistency in an otherwise-universal convention.
> Also: `ChannelPermissionOverrides` carries an extra literal-PascalCase shadow FK column `RoleId`
> (separate from the polymorphic `target_id`/`target_type` pair) that looks unused/leftover — don't
> assume it's load-bearing without checking call sites first. Standard ASP.NET Identity plumbing
> columns (`NormalizedUserName`, `NormalizedEmail`, `SecurityStamp`, `ConcurrencyStamp`, plus the
> `EmailConfirmed`/`TwoFactorEnabled`/`PasswordHash` flags folded into `Users` below) are framework
> boilerplate and omitted from the per-table listings for brevity.

```
Users(id PK, username, email UNIQUE, password_hash?, avatar_key?, banner_key?, banner_color?, bio,
      date_of_birth?, status_message, status_message_expires_at?,
      preferred_status='online'[online|away|dnd|invisible], preferred_status_expires_at?,
      account_status='active', dm_privacy='everyone'[everyone|friends_only], guild_order bigint[]?,
      email_confirmed, two_factor_enabled, created_at)
      -- discriminator column dropped by DropDiscriminator (§5.22) — username is globally unique.
      -- *_expires_at: unix-ms; nullable; added by AddStatusExpiry (§5.21). Swept by StatusExpiryService
      --   (60s) — preferred reverts to online, message clears.
      -- date_of_birth: DateOnly?, added by AddDateOfBirth (§5.28). Public API exposes computed Age,
      --   never the raw DOB; only /me returns DOB.
      -- dm_privacy: varchar32 default 'everyone', added by AddUserDmPrivacy (§5.30). Gates a NON-friend
      --   opening a *new* DM when 'friends_only'; existing convos always reachable; enforced on EVERY
      --   DM send + group add, not just at creation (§5.43).
      -- banner_color: added alongside banner_key (§5.47) — image > colour > default in render priority.
      -- guild_order: bigint[], user's own drag-reordered guild-sidebar sequence.
      -- password_hash: nullable (ASP.NET Identity default) — a Google-only account (§5.68) has none
      --   until it uses "Set Password"; `UserResponse.HasPassword` = `password_hash != null`.
      -- email_confirmed/two_factor_enabled: Identity-managed bools, driven by the §5.68 verify-email +
      --   email-code 2FA flows — there is no TOTP secret column, every 2FA code is emailed via Redis.
RefreshTokens(id uuid PK, user_id FK→Users CASCADE, token_hash UNIQUE, family_id uuid,
              expires_at timestamptz, revoked_at timestamptz?, created_at)
TrustedDevices(id uuid PK, user_id FK→Users CASCADE, token_hash UNIQUE, expires_at timestamptz,
               created_at)
      -- §5.68 (AddTrustedDevices). "Remember this device for 30 days" for email-code 2FA — same shape
      --   as RefreshTokens (raw random token in an httpOnly `trusted_device` cookie, only the SHA-256
      --   hash stored). Pruned by TokenPruningService (daily) alongside expired refresh tokens. A
      --   password change or a 2FA disable deletes ALL of a user's rows here.
Guilds(id PK, name, description?, owner_id FK→Users RESTRICT, icon_key?, banner_key?, is_public,
       member_count, welcome_channel_id FK?→Channels, welcome_message?, system_messages_enabled=true,
       require_verified_email=false, created_at)
       -- invite_code column dropped by ManageInvitesAndDropGuildInviteCode (§5.23) — joins now go
       --   through the managed GuildInvites table, no permanent per-guild code.
       -- welcome_* added by AddGuildWelcomeAndNotificationSettings (§5.31). welcome_channel_id null =
       --   post member-join notices to the first text channel; welcome_message null = default greeting;
       --   system_messages_enabled=false suppresses join notices. Set via PATCH …/{id}/welcome.
       -- require_verified_email: added by AddGuildRequireVerifiedEmail (§5.68). When true, both join
       --   paths (InvitesController.Join, DiscoveryController.Join) reject an unconfirmed-email joiner
       --   with 403 `{error, requiresVerifiedEmail: true}`, checked after the ban check, before member
       --   creation. Set via PATCH /api/guilds/{id}.
       -- NOTE: guild-create seeds NO channels (only owner member + @everyone role) — only the DevSeed
       --   tool makes channels.
Channels(id PK, guild_id FK?→Guilds CASCADE, name, topic?, type[text|voice|category|dm|group_dm],
         position, icon_key?, category_id FK?→Channels SET NULL, is_nsfw, slowmode_seconds, bitrate?,
         user_limit?, created_at)
         -- icon_key: group-DM icon upload (§5.56, AddChannelIconKey). category_id self-FK for the
         --   channel-category grouping feature; bitrate/user_limit are voice-channel settings (null
         --   for text channels).
GuildMembers(user_id FK CASCADE, guild_id FK CASCADE, nickname?, joined_at, is_owner,
             communication_disabled_until?, PK(user_id,guild_id))
Roles(id PK, guild_id FK→Guilds CASCADE, name, color, permission_bits bigint, position, is_hoisted,
      is_mentionable, is_default, created_at)
RoleAssignments(user_id FK CASCADE, role_id FK CASCADE, guild_id, assigned_at, PK(user_id,role_id))
ChannelPermissionOverrides(id PK, channel_id FK→Channels CASCADE, target_id, target_type[role|user],
                           allow_bits bigint, deny_bits bigint)
                           -- unique(channel_id,target_id,target_type). Also carries an extra shadow
                           --   FK `role_id`→Roles — see the column-naming note above; don't assume
                           --   it's load-bearing without checking.
Friends(requester_id FK CASCADE, addressee_id FK CASCADE, status[pending|accepted|declined],
        created_at, updated_at, PK(requester_id,addressee_id))
UserBlocks(blocker_id FK CASCADE, blocked_id FK CASCADE, created_at, PK(blocker_id,blocked_id))
UserMutes(user_id FK CASCADE, target_id, target_type[guild|channel|user], muted_until?, created_at,
          PK(user_id,target_id,target_type))
          -- target_id is a polymorphic id (guild/channel/user) — no FK, by design.
UserNicknames(owner_id FK→Users CASCADE, target_id FK→Users CASCADE, nickname, created_at, updated_at,
              PK(owner_id,target_id))
              -- §5.29 (AddUserNicknames). Friend-nicknames: creator-only-visible, distinct from
              --   GuildMembers.nickname (server nicknames, visible to the whole guild).
DirectMessageChannels(channel_id FK→Channels CASCADE, user_id FK→Users CASCADE, is_hidden,
                      last_read_id, PK(channel_id,user_id))
FileAttachments(id PK, uploader_id FK→Users RESTRICT, guild_id?, channel_id FK?→Channels CASCADE,
                minio_key, filename, content_type, size_bytes, width?, height?, is_confirmed,
                thumbnail_key?, created_at)
                -- guild_id carries no FK constraint (just a column); channel_id nullable + FK
                --   (user-scoped avatar/banner uploads have neither, §5.47).
                -- thumbnail_key: §5.69 (AddAttachmentThumbnailKey). Object key of the display-only
                --   downscaled WebP derivative for large chat images (source >1024px on either axis,
                --   non-GIF). The ORIGINAL at minio_key is never touched — lightbox/download always
                --   serve full quality; only the inline chat <img> prefers the thumbnail. Null when
                --   the source is small enough or animated. Avatars/banners/guild+group-DM icons are
                --   instead capped IN PLACE at confirm (512px icons/avatars, 1280px banners); GIFs
                --   are never resized (would flatten animation).
MessageReactions(message_id, emoji varchar64, user_id FK→Users CASCADE, channel_id, created_at,
                 PK(message_id,emoji,user_id))
                 -- §5.64 (AddMessageReactions). Reactions are Postgres, NOT Scylla, despite messages
                 --   themselves living in Scylla — message_id/channel_id are FK-less cross-store
                 --   pointers, same pattern as MessagesSearch. emoji varchar64 is a forward-compat
                 --   token: a literal Unicode grapheme today, `custom:{id}` if custom emoji ever ships.
Notifications(id PK, user_id FK→Users CASCADE, type[mention|reply|friend_request|guild_invite|message],
              actor_id FK?→Users RESTRICT, guild_id?, channel_id?, message_id?, is_read, created_at)
              -- "message" producer (the `all` notification level) added §5.65. message_id is an
              --   FK-less pointer into Scylla.
NotificationPreferences(user_id PK FK→Users CASCADE, mentions_enabled, replies_enabled,
                        friend_requests, guild_invites, push_enabled)
NotificationSettings(user_id FK→Users CASCADE, scope_type[guild|channel], scope_id,
                     level[all|mentions|nothing], suppress_everyone=false, PK(user_id,scope_type,scope_id))
                     -- per-user per-guild/channel notify level (§5.31). Resolution at notify-time:
                     --   channel-scope → guild-scope → default "mentions"; "nothing" silences the scope.
                     --   Distinct from NotificationPreferences (global master switch) + UserMutes (temp).
                     --   suppress_everyone added §5.65 — an @everyone/@here-only mention origin is
                     --   dropped for a recipient with this set (channel-level overrides guild-level);
                     --   a direct @mention always still notifies.
UserPushSubscriptions(id PK, user_id FK→Users CASCADE, endpoint, p256dh, auth_key, created_at)
                     -- §5.50. PUT /api/notifications/push-subscription upserts by endpoint (reassigns
                     --   owner across logins); dispatcher prunes "Gone" rows.
PushOutbox(id PK, kind[mention|reply|friend_request|guild_invite|message|dm|call], recipient_id,
           actor_id?, guild_id?, channel_id?, message_id?, exclude_user_ids?, attempts,
           next_attempt_at, created_at)
           -- §5.50 (AddPushOutbox), entity class `PushOutboxMessage` (table stays `PushOutbox` — a
           --   naming mismatch, not a typo to "fix"). Transactional web-push outbox: staged in the SAME
           --   save as the Notification row for mention/reply/guild_invite/message; "dm" and "call"
           --   rows are staged directly by the message consumer / ChatHub.StartCall — they never pass
           --   through the Notifications table, only the outbox, and resolve recipients dynamically
           --   from the DM's current participant list minus exclude_user_ids at send time.
           --   INDEX(next_attempt_at); rows deleted on dispatch or after 5 attempts (no separate sweep).
GuildInvites(code PK varchar16, guild_id FK→Guilds CASCADE, channel_id FK?→Channels SET NULL,
             creator_id FK→Users RESTRICT, max_uses?, use_count, expires_at?, created_at)
             -- §5.23. channel_id nullable = guild-level invite. expires_at?/max_uses null =
             --   never/unlimited; use_count bumped on redeem. CreateInvite to mint your own,
             --   ManageInvites to see/revoke everyone's; revoke = creator-or-ManageInvites (§5.24 #7).
GuildBans(guild_id FK CASCADE, user_id FK CASCADE, banned_by FK→Users RESTRICT, reason?, created_at,
          PK(guild_id,user_id))
VoiceStates(user_id PK FK→Users CASCADE, channel_id FK→Channels RESTRICT, guild_id FK?→Guilds CASCADE,
            is_muted, is_deafened, is_server_muted, is_server_deafened, is_streaming, is_video_on,
            joined_at)
            -- guild_id nullable = a DM/group-DM call room (made nullable by
            --   MakeVoiceStateGuildNullable, §5.57). Note the app's real-time voice roster/moderation
            --   state actually lives in REDIS (`voice:channel:{channelId}` etc, see docs/redis-and-
            --   events.md) — this Postgres table exists in the schema but is not the live source of
            --   truth for an active call; don't assume reads here reflect current voice state.
AuditLogs(id PK, guild_id FK→Guilds CASCADE, actor_id FK→Users RESTRICT, action_type, target_id?,
          changes jsonb?, reason?, created_at)
          -- Current producers (§5.23+): member_kick/ban/unban/timeout/nickname_update, role_create/
          --   update/delete, member_role_update, invite_create/delete (code masked to a 3-char prefix),
          --   message_delete/pin/unpin. `channel_create/update/delete` constants EXIST on the action-type
          --   enum but have NO call sites today — channel management is not actually audited yet,
          --   despite the enum implying it is.
MessagesSearch(message_id PK, channel_id, guild_id?, user_id, content, created_at)
               -- standalone read model, NO FK to Channels (dropped, DecoupleSearchIndex). A
               --   `content_search tsvector` column is maintained by a Postgres trigger and is NOT
               --   in the EF model at all (invisible to C#, real in the DB) — don't expect to find it
               --   via EF/LINQ. INDEX GIN(content_search); INDEX(channel_id, created_at DESC). Search
               --   matches `plainto_tsquery('english', q)` against that tsvector OR'd with an escaped
               --   `ILIKE '%q%'` fallback (so stop-word/punctuation-only queries still hit), ordered by
               --   created_at DESC (recency — NOT `ts_rank` relevance, despite an earlier session's
               --   note claiming ts_rank was added; verify against `MessageSearchRepository` before
               --   trusting either claim if this matters).
```

---

## 11. REFERENCE — ScyllaDB schema

> ScyllaDB stores **messages + read_states + pinned_messages only** (4 tables total), via the Cassandra
> driver (no EF Core). RF=1 + LocalQuorum → read-after-write is immediately consistent.
> **Keyspace name is `harmony`** (verified in `ScyllaSessionFactory.cs` + `KeyspaceInitializer.cs`,
> config key `ScyllaDB:Keyspace`, 2026-07-18) — the old "verify the actual name, might be `nexus`" note
> is stale, drop it.
> Reminder: query results are single-pass `RowSet`s — **materialize with `.ToList()`** before any
> double enumeration (this caused 3 test failures; see §5.2).
> Reactions live in **Postgres** (`MessageReactions`), not here — don't assume symmetry between pins
> (Scylla) and reactions (Postgres). Voice/call state is Postgres-only (`VoiceStates`) — nothing
> voice-related exists in Scylla.
> A column added to an **already-running** dev/prod keyspace needs the keyspace/table dropped and
> recreated locally — `CREATE TABLE IF NOT EXISTS` is a no-op against an existing table. CI is
> unaffected (fresh Scylla every run).

```
messages_by_channel(channel_id, message_id, user_id, content, attachment_ids list<bigint>,
  mention_ids list<bigint>, reply_to_id, is_deleted, is_edited, edited_at, message_type,
  forward_snapshot,
  PRIMARY KEY(channel_id, message_id)) WITH CLUSTERING ORDER BY (message_id DESC)
  + TimeWindowCompactionStrategy (1 DAY)
  -- forward_snapshot: text, JSON-serialized MessageForwardSnapshot(AuthorId, AuthorName, Content,
  --   SentAt) — added non-destructively for server-verified message forwarding (drain Slice 4). Exists
  --   ONLY on this table, not messages_by_id (see below) — a forwarded message's snapshot is not
  --   resolvable via the by-id lookup table.
read_states(user_id, channel_id, last_read_message_id, PRIMARY KEY(user_id, channel_id))
messages_by_id(message_id PK, channel_id, user_id, content, attachment_ids list<bigint>,
  mention_ids list<bigint>, reply_to_id, is_deleted, is_edited, edited_at)
  -- single-column PK, no clustering, default compaction. A lookup-by-id table, NOT a full mirror of
  --   messages_by_channel — it lacks message_type and forward_snapshot.
pinned_messages(channel_id, pinned_at, message_id, pinned_by,
  PRIMARY KEY(channel_id, pinned_at)) WITH CLUSTERING ORDER BY (pinned_at DESC)
  -- PUT-to-pin is an idempotent upsert on this clustering key.
```

---
