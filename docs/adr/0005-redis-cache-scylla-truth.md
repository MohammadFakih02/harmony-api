# 0005 — Redis is a cache; ScyllaDB `read_states` is the source of truth

**Status:** Accepted

## Context

Every user needs an **unread count** per channel — the badge that says "3 new". It is read
constantly (every sidebar render), written constantly (every message bumps it for every recipient;
every channel open resets it), and shown to potentially thousands of users per active channel. Serving
that from a disk-backed relational store on every render is untenable.

Redis is the obvious cache. But a cache introduces a truth question: if the fast counter and the
durable record disagree — after a Redis restart, an eviction, or a race — **which one is right?** Get
that backwards and a Redis flush permanently corrupts everyone's unread state.

## Decision

- **ScyllaDB `read_states`** (per user, per channel: the last-read message ID) is the **source of
  truth**. It's durable, it's the same store as the messages themselves, and an unread count is
  derivable from it: everything after `last_read_message_id`.
- **Redis holds a cached count** for speed. It is authoritative for *latency*, never for *truth*
  (non-negotiable #9).
- On a cache miss, the count is **recomputed from `read_states`** and repopulated. A missing or wrong
  Redis value is a performance event, not a data-loss event.

## Consequences

- **Redis is disposable.** Flush it and the system recomputes counts from ScyllaDB on demand. Nothing
  a user cares about lives *only* in the cache.
- **The fan-out increment is a cache operation.** When a message lands, the consumer bumps each
  eligible recipient's cached count (batched into one Redis round trip per page of recipients — the
  optimization load testing proved out). The durable truth is the `read_states` row plus the message
  stream; the increment is just keeping the cache warm.
- **A known small race is accepted, not fixed.** A message arriving in the sub-second window between a
  read and its mark-as-read can have its increment wiped by the cache reset. It self-corrects on the
  next message or the next recompute. True reconciliation would mean counting Scylla messages with
  `id > last_read` on every mark-read, and Scylla has no cheap `COUNT` — so the under-count-by-a-
  moment is deliberately tolerated rather than paid for. This is documented as a known issue, not a
  hidden one.
- Two systems must agree on the invalidation protocol (reset-on-open, increment-on-receive,
  recompute-on-miss). That protocol lives in one service so the rules aren't smeared across the
  codebase.

## Alternatives considered

- **Redis as the source of truth for counts.** Fastest, and no recompute path to write. Rejected
  outright: a Redis restart or eviction would then be permanent data loss of everyone's read state,
  and Redis is deployed as a cache tier (ElastiCache) precisely because we treat it as volatile.
- **A `unread_count` integer column in PostgreSQL.** Durable and transactional, but it's a hot
  per-user-per-channel counter written on every message to every recipient — the write amplification
  the polyglot design ([ADR-0001](0001-polyglot-persistence.md)) exists to keep off the relational
  store. Also still needs a cache in front for read latency, so it's the Scylla answer with a worse
  write profile.
- **Materialize counts, no derivation.** Store the count itself durably and never recompute.
  Simplest reads, but any drift is unrecoverable — there's no ground truth to reconcile against. Deriving
  from `read_states` means the count is always reconstructable, which is what makes the cache safe to
  throw away.
