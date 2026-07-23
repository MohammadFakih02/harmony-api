# 0001 — Polyglot persistence: PostgreSQL + ScyllaDB + Redis

**Status:** Accepted

## Context

Harmony stores data with three genuinely different profiles:

- **Relational, consistency-critical, moderate volume** — users, guilds, channels, roles,
  permissions, invites, friendships, notifications. Rich relationships, transactional integrity,
  ad-hoc queries. Read and written constantly but never at message scale.
- **Append-heavy, huge volume, simple access** — chat messages and per-user read states. Written on
  every keystroke-turned-send, read in reverse-chronological pages, never joined, never updated in
  bulk. The one table that must survive 10k concurrent users.
- **Ephemeral, hot, disposable** — presence, typing indicators, unread-count cache, the SignalR
  backplane, rate-limit counters, dedup keys. Sub-second lifetimes, loss-tolerant, must never touch a
  disk-backed relational store on the hot path.

Forcing all three into one engine means one of them is served badly. A message table in PostgreSQL
becomes the write bottleneck of the whole system; presence in PostgreSQL is a disk write for data
that's stale in seconds.

## Decision

Use three stores, each for what it is best at:

- **PostgreSQL** (EF Core + Npgsql) — everything relational. The system of record for identity,
  structure, and permissions.
- **ScyllaDB** (Cassandra driver, no ORM) — messages and read states only. Partitioned by channel,
  clustered by message ID, so a channel's history is a single-partition reverse-order scan. Chosen
  over Cassandra for the same data model at lower latency on commodity hardware.
- **Redis** — presence, typing, unread-count cache, SignalR backplane, rate limiting, dedup.

The repository pattern wraps **both** durable stores, but there is deliberately **no shared
abstraction forced across Postgres and Scylla** (see the consequence below).

## Consequences

- Each store is used idiomatically: EF Core's change tracking and migrations for the relational side;
  hand-written CQL with explicit partition keys for messages; raw Redis commands for the ephemeral
  tier.
- **No distributed transaction across stores.** A message write (Scylla) and its notification rows
  (Postgres) can't be one atomic commit. This is handled by the write pipeline
  ([ADR-0002](0002-persist-then-broadcast.md)) and a transactional outbox, not by 2PC.
- Three engines to run, health-check, and deploy. The local `docker-compose` stack and the AWS
  target ([ADR-0007](0007-aws-amazon-mq.md)) both carry all three.
- The repository interfaces intentionally don't share a base type. A "generic repository over both
  databases" was tried in spirit and rejected — the two stores have nothing in common at the query
  level, and a forced abstraction would leak Cassandra's partition-key constraints into code that
  thinks it's talking to SQL.

## Alternatives considered

- **All PostgreSQL.** Simplest to operate, one transaction boundary, one migration story. Rejected:
  the messages table is the scale target of the project, and a single relational table taking every
  send is exactly the bottleneck the architecture exists to avoid. The whole point of the exercise is
  a system that could hold 10k concurrent users, not a CRUD app.
- **All Cassandra/Scylla.** Great for messages, hostile to everything else — no joins, no ad-hoc
  queries, no transactions, denormalize-everything modeling for data that is inherently relational
  (permission resolution across roles and overrides would be a nightmare).
- **PostgreSQL + Redis only (no Scylla).** Viable at small scale, but concedes the headline goal.
  Keeping messages in the relational store means the one thing that must scale is coupled to the one
  thing that must stay consistent.
- **A document store (MongoDB) for messages.** Workable, but Scylla's partition-per-channel model is
  a cleaner fit for "read the last N messages of one channel, fast" and has a stronger horizontal
  scaling story for the specific access pattern.
