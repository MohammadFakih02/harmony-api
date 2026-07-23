# 0002 — Persist, then broadcast: never fan out from the SignalR hub

**Status:** Accepted

## Context

When a user sends a message, two things must happen: it must be **durably stored**, and it must be
**delivered live** to everyone watching the channel. The tempting shape is to do both in the hub
method — save, then call `Clients.Group(...).MessageReceived(...)` right there. It's one method, it's
synchronous, it's easy to read.

It's also wrong in ways that only show up under failure and scale:

- If the broadcast happens *before* the write is durable and the write then fails, clients have
  rendered a message that doesn't exist. On reload it vanishes — the worst kind of bug, invisible
  until someone refreshes.
- The hub instance that received the send is only one of N behind a load balancer. It can only
  broadcast to the connections *it* holds unless every send blocks on the backplane.
- Fan-out (resolving who can see the channel, incrementing unread counts, writing notifications) is
  real work. Doing it inline makes the sender's send-ack wait for everyone else's bookkeeping.

## Decision

The hub **never broadcasts**. The write path is a pipeline:

```
ChatHub.SendMessage
  → validate + authorize + rate-limit
  → IMessageService: persist to ScyllaDB
  → publish MessageSentEvent to RabbitMQ
  → (hub returns; sender's optimistic bubble is already on screen)

RabbitMQ consumer (ScyllaMessageConsumer)
  → dedupe by snowflake id
  → resolve recipients, fan out unread counts, write notifications
  → IHubBroadcaster.BroadcastMessageReceived  ← the ONLY place a live message is emitted
```

The consumer broadcasts **after** persistence is confirmed, via `IHubBroadcaster` (which talks to the
SignalR backplane, so it reaches connections on every instance). This is architecture
non-negotiable #2, and historically the single most bug-causing rule when violated.

## Consequences

- **A broadcast is never a lie.** If clients received it, it's in ScyllaDB — the broadcast is
  downstream of the durable write, not racing it.
- **The send-ack is fast and constant.** The sender's round trip ends at "published to RabbitMQ"
  (~5ms), independent of channel size. The optimistic bubble ([ADR-0003](0003-snowflake-ids.md),
  [ADR-0006](0006-client-side-message-ordering.md)) covers the gap until the broadcast echo arrives.
- **Overload degrades as latency, not data loss.** Under a send storm the queue absorbs the backlog;
  messages arrive late but complete and correctly ordered. Load testing measured exactly this — at
  overload, delivery latency climbed while the send-ack stayed flat, and nothing was dropped.
- **Idempotency is mandatory.** RabbitMQ is at-least-once, so the consumer dedupes by snowflake ID
  (non-negotiable #10). A redelivery is a no-op, not a duplicate message.
- The consumer's failure handling is the most delicate code in the repo: a write-side circuit breaker
  requeues and replays sends while ScyllaDB is down rather than dead-lettering them, and constraint
  violations are fast-failed to a DLQ. These paths are treated as change-with-extreme-care.

## Alternatives considered

- **Broadcast directly from the hub after saving.** The simple version. Rejected on every count
  above: it couples send latency to fan-out cost, only reaches one instance's connections without
  extra backplane round-trips inside the request, and invites the broadcast-outran-the-write bug.
- **Outbox-poll instead of a message broker.** Write the message and an outbox row in one Postgres
  transaction, poll the outbox to broadcast. But messages live in Scylla, not Postgres, so there's no
  shared transaction to enlist the outbox in — and a broker gives back-pressure and at-least-once
  delivery for free. (An outbox *is* used, but specifically for the offline **push** notifications
  that must survive a crash, where the transactional guarantee is worth the polling cost.)
- **Broadcast synchronously, persist asynchronously.** Fastest possible live feel, but it makes the
  live stream the source of truth and the database an eventually-consistent follower — precisely
  inverted from what a chat app needs.
