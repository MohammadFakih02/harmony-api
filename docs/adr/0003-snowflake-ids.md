# 0003 — Snowflake IDs everywhere

**Status:** Accepted

## Context

Harmony needs primary keys for entities that live in two different databases (PostgreSQL and
ScyllaDB), are created on multiple API instances, are sorted chronologically in the UI, and are
frequently generated *optimistically on the client* before the server has confirmed anything. The ID
scheme has to satisfy all four at once:

1. **No central coordinator.** Two API instances must mint IDs without asking a shared sequence.
2. **Time-sortable.** A channel's messages are read newest-first; the ID itself should encode order
   so "sort by id" *is* "sort by time" with no separate timestamp column to order by.
3. **Cheap to compare and store.** These IDs are on every message, every row, every URL.
4. **Mergeable on the client.** The UI places live messages by ID without trusting arrival order
   ([ADR-0006](0006-client-side-message-ordering.md)).

## Decision

Use **Snowflake IDs** — 64-bit integers composed of `(timestamp − epoch) | worker | sequence`, with
a project epoch of **2024-01-01**. Every entity uses them, stored as `bigint`. The one exception is
`RefreshTokens`, which use random opaque strings — those are bearer secrets, where *unguessable*
matters and *sortable* would be a liability.

## Consequences

- **Sorting by ID is sorting by time.** Message pagination, jump-to-message, and client-side ordered
  insertion all key off the ID with no companion timestamp.
- **No coordination on the write path.** Any instance mints an ID locally; the timestamp+worker+seq
  structure keeps them globally unique without a round trip.
- **Client-side generation for optimistic UI.** An unsent message gets a temporary negative
  placeholder ID and reconciles against the real snowflake when the server echoes back.
- **The 53-bit JavaScript problem — the load-bearing frontend gotcha.** A 64-bit snowflake exceeds
  `Number.MAX_SAFE_INTEGER`, so `JSON.parse` silently rounds it to a nearby float and corrupts every
  downstream lookup and URL. **Every ID is a string end-to-end on the client.** Two mechanisms enforce
  it, one per transport: an HTTP interceptor re-quotes bare long integers in JSON value position
  before Angular parses them, and the SignalR client coerces each ID field with `String()`. This is
  documented as a non-negotiable in the client README because it fails silently and catastrophically.
- IDs leak creation time (they encode a timestamp). Acceptable — message and entity creation time is
  not sensitive here.

## Alternatives considered

- **Auto-increment / database sequences.** Requires a single authority to hand out IDs, which
  reintroduces the coordination bottleneck the polyglot, multi-instance design exists to avoid — and
  there's no shared sequence across Postgres and Scylla anyway. Also enumerable (id+1 is a valid
  guess).
- **UUIDv4.** Coordination-free and unguessable, but **not sortable** — the killer flaw. Every
  message query would need a separate indexed timestamp to order by, and the client couldn't place a
  live message by ID. Also 128 bits, doubling key size on the hottest table.
- **UUIDv7 (time-ordered UUID).** Solves sortability and is a genuinely reasonable modern choice.
  Rejected mostly on ergonomics: 128-bit, string-shaped, heavier on the message table and in every
  URL, with no benefit over a 64-bit snowflake for a system that already accepts leaking creation
  time. Snowflakes are also the model the domain is explicitly imitating.
- **Snowflake for everything including refresh tokens.** Rejected for that one table — a refresh
  token is a credential, and a sortable, structured, partially-predictable value is the wrong shape
  for a secret. Random strings there, snowflakes everywhere else.
