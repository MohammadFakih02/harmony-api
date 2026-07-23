## 6. application-resiliency — COMPLETE (design reference, decisions LOCKED)

> ✅ **Built and merged** (Phase 2; as-built file list in §5.2 item 7). Retained below as the locked
> design rationale — backoff caps, retry-count reasoning, and the §6.6 poison-message hardening that is
> still **open backlog** (§18). This is reference, not a to-do.
> **A THIRD mechanism landed later and isn't covered by the M1/M2 split below:** a **write-side**
> circuit breaker in `ScyllaMessageConsumer` (§5.66) — requeue + pause + periodic health-probe while
> Scylla is down, so sends aren't lost during an outage (they get replayed once Scylla recovers) rather
> than dead-lettered. That session also fixed a dedup-in-retry bug where the check-and-set dedup gate
> running *inside* the Polly retry lambda caused attempt 1 to see attempt 0's own claimed key, report
> "duplicate," exit normally, and ACK — silently defeating the retry ladder. Not detailed further here;
> see `harmony-api/HISTORY.md` §5.66 for the full writeup.

It was **two mechanisms with opposite failure philosophies** (plus the write-side breaker noted above,
added later):

- **(M1) Circuit breakers on reads** — *fail fast, degrade gracefully.* When a dependency is clearly
  down, stop hammering it; return a degraded fallback instead of timing out every request.
- **(M2) `MessageFailed` write-path notification** — *the write genuinely failed after retries; tell
  the client so it can roll back its optimistic message.* Not a breaker.

### 6.0 Sequencing (do in this order; each is its own reviewable step)

1. **Polly v7 → v8 migration** of the two existing consumer retry policies — *behavior-preserving*.
   Do **both consumers at once**, confirm suite stays green, on a clean base. (§6.2)
2. **Circuit breakers** via decorator. (§6.3)
3. **`MessageFailed`** path. (§6.4)

We are **migrating everything to Polly v8 `ResiliencePipeline`** (one idiom, not two). Already on
Polly 8.6.6, so no package bump.

### 6.1 Decisions locked

- **A — breaker placement: decorator.** `ResilientMessageRepository : IMessageRepository` wrapping the
  concrete repo. Keeps data-access pure; resiliency is a composable layer. (Textbook clean-arch.)
- **B — Polly v8 `ResiliencePipeline` everywhere.** Migrate the two existing v7 retry policies too.
- **C — fallback = signal degradation, NOT silent empty list.** A bare empty list is
  indistinguishable from "no messages." The contract is a **`degraded` flag** the client reads as
  "couldn't load older messages." **Now:** empty list + `degraded` flag. **Later:** when a Redis
  message-cache feature lands, the fallback serves cached messages + `degraded`. The flag is the
  durable decision; what fills the fallback is swappable. **Do not** drag a caching feature into this
  branch.
- **D — keep dedup; clear the dedup key on terminal failure.** Scylla writes are idempotent (upserts),
  so the *persistence* half is safe to reprocess — but dedup does **not** exist to protect the upsert.
  It protects the **non-idempotent side effects**: the hub broadcast (double-send) and the unread INCR
  fan-out (double-increment). Because reprocessing is safe-to-idempotent on the write, a **terminal
  failure should CLEAR the dedup key** so a genuine retry/redelivery can recover instead of being
  swallowed as a duplicate.

### 6.2 Step 1 — Polly v7 → v8 migration (behavior-preserving)

**Critical v8 gotcha:** in v7 `WaitAndRetryAsync`, `sleepDurationProvider`'s `retryAttempt` is
**1-based** → `Math.Pow(2, attempt)` gives **2s, 4s, 8s**. In v8 `DelayGenerator`,
`args.AttemptNumber` is **0-based**. To preserve the exact ladder you **must** use
`args.AttemptNumber + 1`. Also **cap the backoff at 30 s** with `Math.Min(..., 30)` so the tail can't
run away. Keep `MaxRetryAttempts = 3` (same as v7 `retryCount: 3` — no off-by-one; it means 3 retries).

`ShouldHandle` predicates must be preserved exactly:
- **ScyllaMessageConsumer:** handle everything except `JsonException` (JSON → straight to DLQ).
- **SearchIndexConsumer:** handle everything except `JsonException` **and** `ServiceUnavailableException`
  (the latter is the out-of-order requeue signal — must NOT be retried, gets requeued instead).

**ScyllaMessageConsumer pipeline (construction):**
```csharp
using Polly;
using Polly.Retry;

private readonly ResiliencePipeline _retryPipeline;

_retryPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not JsonException),
        MaxRetryAttempts = 3,
        DelayGenerator = args =>
        {
            // v8 AttemptNumber is 0-based; +1 reproduces the v7 1-based 2s/4s/8s ladder. Cap 30s.
            var seconds = Math.Min(Math.Pow(2, args.AttemptNumber + 1), 30);
            return new ValueTask<TimeSpan?>(TimeSpan.FromSeconds(seconds));
        },
        OnRetry = args =>
        {
            _logger.LogWarning(args.Outcome.Exception,
                "ScyllaConsumer: retry {RetryCount} after {Delay:0.0}s",
                args.AttemptNumber + 1, args.RetryDelay.TotalSeconds);
            return default;
        },
    })
    .Build();
```

**SearchIndexConsumer pipeline (construction):** identical, but
```csharp
ShouldHandle = new PredicateBuilder().Handle<Exception>(ex =>
    ex is not JsonException && ex is not ServiceUnavailableException),
// ... and log prefix "SearchIndexConsumer: retry ..."
```

**Invocation site change (v7 → v8):** the lambda gains a `ct` parameter and the call takes the token.
```csharp
// v7
await _retryPolicy.ExecuteAsync(async () => { ... });
// v8
await _retryPipeline.ExecuteAsync(async ct => { ... }, cancellationToken);
```
**MUST preserve verbatim** the surrounding try/catch, the `BasicNack`/`BasicAck`, DLQ routing, and the
`ServiceUnavailableException` requeue branch in each `OnMessageReceivedAsync`. Read the real files
(`@harmony-api/src/Harmony.Infrastructure/RabbitMQ/Consumers/ScyllaMessageConsumer.cs` and
`SearchIndexConsumer.cs`) and transform — do not reconstruct.

**Postgres `EnableRetryOnFailure` (independent, in `DependencyInjection.cs`):** raise
`maxRetryCount` 3 → **5**, `maxRetryDelay` 2 s → **5 s**. Request-local, bounded, matches a brief
failover window. Leaves token-rotation OCC untouched (`DbUpdateConcurrencyException` is non-transient,
EF won't retry it).
```csharp
npgsqlOptions.EnableRetryOnFailure(
    maxRetryCount: 5,
    maxRetryDelay: TimeSpan.FromSeconds(5),
    errorCodesToAdd: null);
npgsqlOptions.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
```

### 6.3 Step 2 — circuit breakers (decorator)

- `ResilientMessageRepository : IMessageRepository` wraps the concrete repo; wrap Scylla **reads** in a
  v8 circuit-breaker pipeline. On `BrokenCircuitException`, return the **degraded fallback** (empty list
  + `degraded` flag per Decision C). `GlobalExceptionHandler` already maps
  `BrokenCircuitException → 503` where a 503 is the right surface.
- **CRITICAL state constraint:** a circuit breaker is **stateful** (counts failures, trips), but
  `IMessageRepository` is registered **scoped** (per-request). A new breaker per request never trips.
  So the **breaker pipeline MUST be a singleton**, injected into the scoped decorator. Register the
  `ResiliencePipeline`/breaker as a singleton; the scoped decorator consumes it. Get this wrong and the
  breaker is decorative.
- Consider a breaker around **RabbitMQ publish** as well (the handover mentions it). Same singleton rule.

### 6.4 Step 3 — `MessageFailed` path

When the consumer's Scylla persist fails after retries (today → `BasicNack` → DLQ), *also* notify the
**sender** so the client rolls back its optimistic message.

- New event `MessageFailed`; `IChatClient.MessageFailed(MessageFailedPayload)`;
  `IHubBroadcaster.BroadcastMessageFailedAsync`. Same pattern as everything else.
- **Per-sender only:** `Clients.User(senderId)` — other users never saw an optimistic copy. (Uses the
  same `Clients.User` machinery the unread feature validated.)
- Goes in the consumer's `catch`, **before** `BasicNack`, only for the `MessageSentKey` routing key,
  only after retries are exhausted.
- **Dedup interaction (Decision D):** the dedup key is set at the top of the handler, so a redelivery
  would be swallowed as a duplicate and never reprocessed. On terminal failure, **clear the dedup key**
  so a genuine retry can recover. Design this carefully against *where* in the handler the failure
  happened (pre- vs post-upsert).

### 6.5 Retry / timeout tuning — decided, do NOT "improve" further

- **Consumer retry counts stay at 3.** More retries on a *queue consumer* is a footgun: the consumer
  processes serially, so a message burning a long `2^n` backoff ladder causes **head-of-line stalls**
  for healthy traffic behind it. At 8 retries that's ~8.5 min per poison message; during a dependency
  outage *every* message burns the full ladder and the queue backs up unboundedly. Outage resilience is
  the **circuit breaker's** job, not a longer ladder. Retries handle the *transient blip*; the breaker
  handles the *outage*.
- **Backoff capped at 30 s** (`Math.Min(Math.Pow(2, attempt+1), 30)`).
- **Dedup TTL stays 60 s.** It's sized to exceed the 14 s ladder + broadcast. Raising it causes
  **false-positive dedup** (a legitimately-distinct reprocess after a long gap gets wrongly swallowed).
  The cost isn't memory; it's correctness. Leave it.
- **Postgres retry raised to 5 / 5 s** (request-local, bounded — see §6.2). This is the one place "more"
  is fine.

### 6.6 Poison messages — future hardening (separate step, not this branch)

Poison message = fails deterministically every time (not transient). Current predicates only fast-fail
**parse** poison (`JsonException`) and requeue **ordering** signals (`ServiceUnavailableException`).
**Still ride the full ladder:** DB-constraint poison (`23503` FK, `23505` unique, not-null, check),
concurrency poison (`DbUpdateConcurrencyException` on an already-deleted row), oversized/invalid
payloads, producer/consumer schema drift. These are *inherent to async messaging* (state changes
between enqueue and consume), not bugs. The right hardening is **widen the fast-fail predicate**:
DLQ constraint-violation poison immediately (check Postgres error code: `23503`/`23505` = poison →
don't retry; connection/deadlock errors = transient → retry). Delicate — do it as its **own** step
after the v8 migration, not folded in.

---

## 9. KEY ARCHITECTURAL SOLUTIONS (prior sessions — context)

- **`IHubBroadcaster` pattern (critical).** Infrastructure can't reference API, but the consumer (in
  Infrastructure) must broadcast via SignalR, and `IHubContext<THub, TClient>` needs the concrete hub
  type (in API). Solution: `IChatClient` + payload records in `Application/Hubs/`; `IHubBroadcaster`
  (no SignalR types) in `Application/Interfaces/Services/`; `HubBroadcaster : IHubBroadcaster` (holds
  the real `IHubContext<ChatHub, IChatClient>`) in `API/Hubs/`, registered **singleton in Program.cs**
  (not `DependencyInjection.cs`, because it depends on `ChatHub`). Infrastructure injects
  `IHubBroadcaster` only. A failed earlier attempt used a `ChatHubMarker` base class — **deleted**,
  because `IHubContext<ChatHubMarker>` resolves to a context with zero connections (SignalR keys
  contexts by concrete hub type).
- **`HubResult<T>` pattern.** `ChatHub.SendMessage` returns `HubResult<T>(bool Succeeded, T? Data,
  string? ErrorMessage)` instead of throwing for expected validation failures. `HubExceptionFilter`
  (via `options.AddFilter<HubExceptionFilter>()`) catches unexpected crashes and masks details.
- **`IRedisConnectionProvider`.** Wraps `IConnectionMultiplexer` so Infrastructure and tests handle the
  null/unavailable case explicitly. `RedisConnectionProvider` owns the single process-wide multiplexer
  via `RedisConnectionFactory.CreateMultiplexer`. Everything Redis injects this provider, never the raw
  multiplexer. The SignalR backplane and the deduplicator share the same connection.
- **`ChannelDeleted` async cleanup.** `ChannelDeletedEvent` → RabbitMQ → both consumers:
  `ScyllaMessageConsumer` purges partitions (O(1) `PurgeChannelPartitionsAsync`), `SearchIndexConsumer`
  runs `ExecuteDeleteAsync` on `MessagesSearch`. Create/update/reorder broadcast `ChannelUpdated`;
  delete broadcasts `ChannelDeleted` (separate `IChatClient` method + `ChannelDeletedPayload`) so the
  client removes from sidebar vs updates metadata.
- **Message dedup.** `IMessageDeduplicator` (constants `Sent`/`Deleted`/`Edited`,
  `Task<bool> IsDuplicateAsync(eventType, messageId, ct)`). `RedisMessageDeduplicator` does atomic
  `SET key "1" NX PX 60000` → true if duplicate, false if first time; **fails OPEN** (returns false =
  process) if Redis is null/disconnected/throws. Key `dedup:msg:{eventType}:{messageId}`, TTL 60 s,
  **per-event-type** so a sent→edited→deleted sequence on one ID isn't wrongly blocked.
  `HandleChannelDeletedAsync` has **no** dedup (partition deletes are idempotent). ScyllaDB-level
  idempotency in `MessageConsumerHandler` is the second line of defence.

---

## 16. ANGULAR STORE MAP (frontend phase reference)

> Verified against `harmony-client/src/app/core/stores/*.ts` (2026-07-18) — 22 stores, one file each.
> There is **no `AuthStore` in this directory** — auth/session state lives in `AuthService`
> (`core/services/auth.service.ts`), not a signal store; don't go looking for it here.

`GuildStore` — guilds[], selectedGuildId, loading; computed `selectedGuild`; loadGuilds/setGuilds/
reorderGuilds/selectGuild/createGuild/leaveGuild/deleteGuild/applyGuildUpdate.
`ChannelStore` — channelsByGuild{}, selectedChannelId, collapsedCategories, currentCapabilities;
computed sidebarEntries/currentCategories/selectedChannel; loadChannels/selectChannel/
loadCapabilities/applyRoleChange/reorderChannels/createChannel/moveToCategory/saveChannel/
deleteChannel + gateway add/update/removeChannel.
`MessageStore` — messages[] (windowed, cap 200), isLoading/hasMore/degraded, activeChannelId/
activeGuildId, anchored, replyTarget, jumpRequest/pendingJump, slowmodeRemainingSeconds,
mentionHighlights; loadMessages/jumpToMessage/loadOlder/loadNewer/jumpToPresent/sendMessage/
retryMessage/editMessage/deleteMessage/toggleReaction/**insertByTimeOrder** (snowflake-id ordered
insert, §5.67/§18) + gateway apply(Message/Reaction)*. Reconciles by **nonce first**, then temp-id —
not the plain "optimisticIds" shape an older doc describes.
`RoleStore` — byGuild{} (rank-sorted); loadIfNeeded/reload/create/update/remove/reorder/
applyRoleUpserted/applyRoleDeleted.
`MemberStore` — byGuild{}, capsByGuild{}, viewersByChannel{}; loadIfNeeded/loadCapabilitiesIfNeeded/
loadViewersIfNeeded/kick/ban/timeout/clearTimeout/setNickname/setOwnNickname + gateway
patchMember/applyMemberRoleUpdated/applyAvatar.
`DmStore` — dms[], loading; load/open/createGroup/addParticipant/leave/rename/hide/**find**/resync/
ensureVisible/applyAvatar (no `peerOf` method — that name is stale if seen elsewhere).
`FriendStore` — friends[], pending[], loading; computed incoming/outgoing/incomingCount/friendCount;
load/sendRequest/accept/remove + applyFriendRequest/Accepted/Removed/applyAvatar.
`VoiceStore` — **a full LiveKit-orchestration store, not a stub** — participantsByChannel{},
activeChannelId, lastRoomChannelId, connecting, mediaSuspended, selfMuted/Deafened/VideoOn/Streaming,
watchedStreamUserIds, hiddenVideoUserIds; join/leave/cancelJoin/followForceMove/suspendMedia/
resumeMedia/toggleMute/toggleDeafen/toggleCamera/toggleScreenShare/serverMute/serverDeafen/
moveParticipant + gateway applyJoined/applyStateUpdated/applyLeft. Owns an alone-in-room 300s
media-suspend timer internally.
`CallStore` — **not in any older doc; DM/group-DM ring state.** incoming, outgoing;
startCall/accept/decline/dismiss/hangUpWhileRinging + gateway applyIncomingCall/applyCallCancelled/
applyCallDeclined/applyVoiceJoined/applyVoiceLeft. Ring timeout 120s, alone-in-a-DM-call auto-leave 300s.
`PresenceStore` — statuses{}, statusMessages{}, myStatus, myStatusMessage,
myStatusExpiresAt/myStatusMessageExpiresAt; loadStatuses/statusOf/statusMessageOf/initMyStatus/
setMyStatus/setCustomStatus + applyOnline/applyOffline/applyStatusChanged.
`TypingStore` — byChannel{}; typersOf/applyStarted/applyStopped (6s per-typer TTL, self-excluded).
`UnreadStore` — counts{}, channelGuild{}, loading; loadAll/applyAll/setCount/markRead/guildUnreadCount.
`NotificationStore` — notifications[], unreadCount, actors{}; load/set/markRead/markAllRead/clearAll/
markChannelMentionsRead/markFriendRequestRead/delete/resolveActor + applyNotificationReceived/
applyBadgeCount.
`NotificationPreferenceStore` — preferences (nullable); load/setFlag (optimistic per-flag PATCH).
`GuildNotificationSettingsStore` — byGuild{} (guildLevel + per-channel overrides + suppressEveryone);
load/setGuildLevel/setChannelLevel/setGuildSuppressEveryone/setChannelSuppressEveryone.
`BlockStore` — blocked[], loading; computed blockedIds Set; isBlocked/load/block/unblock (optimistic).
`MuteStore` — mutes[], loading; computed mutedChannelIds/mutedGuildIds/mutedUserIds; isMuted/mute/
load/remove (targetType: channel/guild/user).
`PinStore` — **not in any older doc.** guildId/channelId (active-channel scope), pins[], loading;
computed pinnedIds; load/pin/unpin + applyPinned/applyUnpinned/applyMessageDeleted/clear.
`FileStore` — **not in any older doc.** cache{fileId→download metadata w/ presigned URL}; get/resolve
(re-mints the URL within 30s of expiry).
`NicknameStore` — **not in any older doc.** byUser{} (caller's private friend-nickname aliases),
loaded; nicknameOf/setAll/load/set/remove (optimistic).
`ProfileStore` — **not in any older doc.** profiles{userId→PublicUserProfile} cache; profileOf/
loadIfNeeded/refresh/patch + gateway ProfileUpdated.
`LocalSettingsStore` — messageDisplay, fontScale, reducedMotion, channelSidebarWidth,
rightSidebarWidth (persisted to localStorage); setMessageDisplay/setFontScale/setReducedMotion/
setChannelSidebarWidth/setRightSidebarWidth/reset.

No search/audit-log/invite store exists client-side — those surfaces are handled inline in their own
components/services rather than as a signal store.

---

## 17. BACKGROUND SERVICES (.NET hosted services)

> Verified against `harmony-api/src/Harmony.Infrastructure/Services/` +
> `RabbitMQ/Consumers/` + `Extensions/DependencyInjection.cs` (2026-07-18). **There is no
> `RabbitMQConsumerService` class** — that name never existed as written; it's two separate
> consumers. **There is no `LiveKitWebhookHandler`** either — no LiveKit webhook endpoint exists in
> this codebase at all; voice cleanup is `VoiceStateSweepService`, a polling reaper, not a webhook.

**Startup-only initializers** (`IHostedService`, run once before the app serves traffic):
`KeyspaceInitializer` (ensures the ScyllaDB keyspace/tables exist) · `ObjectStorageBucketInitializer`
(ensures the MinIO/S3 bucket exists; best-effort, never crashes the host).

**Continuous consumers** (`BackgroundService`, RabbitMQ):
`ScyllaMessageConsumer` (self-healing subscriber on the Scylla message-write queue — persists
MessageSent/Edited/Deleted/ChannelDeleted, broadcasts via SignalR, fans out unread counts; has its
own Scylla-down circuit breaker probing every 3s + an out-of-order-edit requeue path, 2s backoff, max
8 retries) · `SearchIndexConsumer` (self-healing subscriber on the search-index queue — keeps the FTS
read model in sync with sent/edited/deleted/channel-deleted events; prefetch 20, 3-retry ladder
2s/4s/8s capped 30s).

**Polling sweeps** (`BackgroundService`):
`MuteExpiryService` (60s — expired-mute cleanup, broadcasts `MuteExpired`) · `StatusExpiryService`
(60s — reverts expired preferred statuses + clears expired custom status messages, re-broadcasts live)
· `PresenceSweepService` (30s, 90s stale threshold — crash-recovery reap of stale presence; the
original "10s" plan was relaxed) · `VoiceStateSweepService` (30s — ghost-recovery reap of voice
participants whose presence has gone offline, mirrors PresenceSweepService's pattern) ·
`InviteCleanupService` (hourly — deletes dead/exhausted guild invites) · `OrphanFileSweepService`
(hourly — deletes unconfirmed file-attachment rows + their MinIO objects past a 15-min grace window) ·
`PushNotificationService` ✅ (§5.50 — PushOutbox dispatcher: wakes on a nudge or a 5s backstop poll,
drains due rows in batches of 32, offline-only web push, exponential backoff + Gone-subscription
pruning) · `TokenPruningService` (daily, prod-only registration — deletes long-revoked/expired refresh
tokens and expired trusted devices).

> **The polling-sweep block is gated `if (!isTest)` in `DependencyInjection.AddInfrastructureServices`**
> — none of these run inside an integration-test host (they'd race Respawn's table resets / drain
> outbox rows mid-assertion). ⚠️ **`isTest` MUST come from the injected `IHostEnvironment`, NOT from
> `configuration["ASPNETCORE_ENVIRONMENT"]`** — the method runs during Program's top-level statements,
> before `WebApplicationFactory`'s config sources are appended, so an eager config read saw null →
> "Production" → registered every sweep in the test host. That was the true root cause of the §5.67
> "2 PushOutbox failures, root cause unknown" (the live dispatcher deleted staged rows mid-test); fixed
> §5.69 by threading `builder.Environment` in. Lazy config reads (connection strings etc.) were never
> affected because they resolve at runtime when config is complete.

---

## 18. OBSERVABILITY (Serilog + health checks — §5.69)

> Verified against `Program.cs` + `Harmony.Infrastructure/HealthChecks/` +
> `Extensions/DependencyInjection.cs` (2026-07-20). This is the "structured logging + health checks"
> half of the Phase-5 production-readiness work; OpenTelemetry/metrics tracing was **CUT** (see
> CLAUDE.md §19 — log-based CloudWatch metric filters are the intended substitute, decided at hosting).

**Serilog** replaces the default console logger entirely (`UseSerilog`, `writeToProviders: false`). A
bootstrap logger is active from the first line so a crash *during configuration* still logs. Sink/format
is chosen in code by environment: **Test** → `MinimumLevel.Warning()` + plain console (an integration run
fires hundreds of requests; keep it quiet); **Development** → a human-readable single line; **everything
else** → one-line JSON (`CompactJsonFormatter`) because ECS/Fargate ships container stdout straight to
CloudWatch Logs, making each line a directly-queryable event with no separate shipper. `MinimumLevel` +
overrides are config-driven (the `Serilog` section), so verbosity turns up via an env var with no rebuild
(same pattern as `RateLimiting:Enabled` / `Cors:AllowedOrigins`).

**Health checks** — `AddHealthChecks()` with five checks in `Harmony.Infrastructure/HealthChecks/`, mapped
at `/health` in `Program.cs`:
- `PostgresHealthCheck`, `ScyllaHealthCheck`, `RabbitMqHealthCheck` — core dependencies; **Unhealthy → 503**
  so the ALB pulls the task out of rotation.
- `RedisHealthCheck` — reports **Degraded** (still 200): the app is designed to keep serving through a Redis
  outage (fail-open presence/dedup/unread-cache paths).
- `DeadLetterQueueHealthCheck` — reports **Degraded** with the DLQ message count in the payload when the
  queue is non-empty. This is the agreed home for the §18 "nothing watches the DLQ" gap (CLAUDE.md): the
  genuine prod poison paths (class-23 constraint violations, the catch-all) mean "a human must look", so the
  valuable half is *knowing the depth*, surfaced here rather than in a browse/replay endpoint.

---