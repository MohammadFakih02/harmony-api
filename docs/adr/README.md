# Architecture Decision Records

This directory records the **load-bearing decisions** behind Harmony — the ones that shaped the rest
of the system and would be expensive to reverse. Each ADR states the decision, the context that
forced it, the consequences we accepted, and — most importantly — the **alternatives we rejected and
why**. The rejected options are the interesting part: a decision is only as good as the option it beat.

These are not a tutorial and not exhaustive API docs. They answer "why is it built *this* way?" — the
question a reviewer asks first.

| # | Decision | One-line rationale |
|---|---|---|
| [0001](0001-polyglot-persistence.md) | Polyglot persistence — Postgres + ScyllaDB + Redis | Messages have a different shape and scale than everything else; one database can't serve both well |
| [0002](0002-persist-then-broadcast.md) | Persist, *then* broadcast — never fan out from the hub | The database is the source of truth; a broadcast that outruns the write is a lie |
| [0003](0003-snowflake-ids.md) | Snowflake IDs everywhere | Time-sortable, client-mergeable, single-authority IDs across two databases with no coordination |
| [0004](0004-jwt-in-memory-refresh-cookie.md) | JWT in memory, refresh token in an HttpOnly cookie | Split the XSS-exposed short token from the theft-critical long one |
| [0005](0005-redis-cache-scylla-truth.md) | Redis is cache; ScyllaDB `read_states` is truth | Unread counts must survive a Redis flush; a cache miss must be recoverable, not fatal |
| [0006](0006-client-side-message-ordering.md) | Order messages client-side by snowflake, not by arrival | Lets the API scale horizontally without the consumer's ordering guarantee becoming a correctness bug |
| [0007](0007-aws-amazon-mq.md) | Deploy on AWS; RabbitMQ via Amazon MQ | AMQP 0.9.1 keeps `RabbitMQ.Client` unchanged — the constraint that ruled Azure out |
| [0008](0008-presigned-urls.md) | Presigned URLs; never expose the object store | The API stays a control plane and never proxies file bytes |
| [0009](0009-signal-stores-unified-gateway.md) | Self-subscribing signal stores over one gateway stream | Kills the central wiring file that grew a dependency on every feature |
| [0010](0010-email-code-2fa.md) | Email-code 2FA, no TOTP | Reuses the email pipeline we already had; no authenticator-app UX or recovery-code burden |

## Format

Each record is lightweight — Status, Context, Decision, Consequences, Alternatives. A record is
immutable once accepted: if a decision is reversed, a new record supersedes it rather than editing
history. That way the reasoning that applied at the time is never lost, even after the conclusion
changes.
