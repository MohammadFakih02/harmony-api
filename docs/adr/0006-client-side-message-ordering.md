# 0006 — Order messages client-side by snowflake, not by arrival order

**Status:** Accepted

## Context

The message consumer ([ADR-0002](0002-persist-then-broadcast.md)) dispatches serially:
`ConsumerDispatchConcurrency` is left at its default of **1**, deliberately, so messages are processed
and broadcast in the order RabbitMQ delivers them. A code comment protects this as an ordering
guarantee.

Load testing surfaced the trap: **that guarantee is a single-instance property.** The production
target ([ADR-0007](0007-aws-amazon-mq.md)) runs multiple API tasks on ECS/Fargate, and two tasks are
**competing consumers** on the same queue. They interleave freely — the serial-dispatch ordering the
comment promises simply doesn't hold across instances. So scaling out from one task to two would have
been a **correctness bug**, not merely a performance tuning choice. The reordering the comment guards
against happens anyway the moment there's a second consumer.

Two ways out: make the server preserve order across instances, or stop depending on arrival order at
all.

## Decision

**Stop trusting arrival order. Place live messages on the client by snowflake ID.**

Since snowflakes are time-ordered ([ADR-0003](0003-snowflake-ids.md)), sorting by ID *is* sorting by
time. The client inserts each incoming message into its correct position by ID
(`insertByTimeOrder` / `compareSnowflakes`) instead of appending in receipt order. The rendered
stream is then correct at **any** dispatch concurrency and **any** number of API instances — the
server is free to deliver out of order because the client no longer cares.

The backend is untouched; `ConsumerDispatchConcurrency` deliberately stays at 1. This decision
*removes the reason* throughput couldn't be scaled, without adding throughput itself.

## Consequences

- **Horizontal scale is unblocked.** Running N API tasks is now safe by construction; the deploy is
  no longer gated on the ordering question.
- **Reordering self-heals in the UI.** Any out-of-order arrival — a retry, a requeue, a slow
  broadcast — now settles into the right place instead of rendering two messages swapped until the
  user reloads. A latent class of "why are these backwards" bugs is gone.
- **Two client-side gotchas the tests pin down:** unsent optimistic bubbles carry *negative*
  placeholder IDs and are excluded from the ordered region (sorting them would fling them to the top
  of the channel); and IDs exceed `Number.MAX_SAFE_INTEGER`, so the comparison is done as `BigInt`,
  not numeric subtraction. The insertion scan runs backward from newest so the common case (a brand-new
  message at the end) costs one comparison.
- Ordering correctness now lives in the client, which must be trusted to sort. Acceptable — the client
  already owns optimistic rendering and reconciliation, so message placement is its job regardless.

## Alternatives considered

- **Raise `ConsumerDispatchConcurrency` above 1.** Adds parallelism but makes the reordering *worse*,
  not better, and the consumer's failure-handling state (circuit-breaker flag, session-ended signal)
  is written assuming a single thread — parallelizing it means a full audit of the repo's most
  dangerous nack/requeue paths, for throughput we don't need (~116 msg/s per instance measured, vs
  ~83 msg/s for 10k users at one message per user per two minutes).
- **Partition the queue by `channelId` via a consistent-hash exchange.** Would preserve per-channel
  order across instances. Rejected twice over: Amazon MQ may not permit the required plugin, and the
  load harness can't even measure it (all virtual users share one channel — the worst case, where it
  yields nothing). A hard dependency on a broker feature the managed platform might veto is the wrong
  bet.
- **Server-side sequence numbers per channel + client resume.** The most "correct" answer, and kept
  as a research note — but it means stamping a sequence through every broadcast path and rebuilding on
  reconnect, a large change to the most fragile code for a benefit the client-side sort already
  delivers. Explicitly deferred as future work, not built.
