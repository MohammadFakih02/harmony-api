# 0007 — Deploy on AWS; RabbitMQ via Amazon MQ

**Status:** Accepted

## Context

The app needs a cloud home for a defense/demo, on a student credit budget, without rewriting working
code. The local stack is specific: ASP.NET Core, PostgreSQL, Redis, **RabbitMQ (AMQP 0.9.1)**,
S3-compatible object storage, ScyllaDB, and a SignalR backplane. The messaging layer is the hard
constraint — `RabbitMQ.Client` speaks **AMQP 0.9.1**, and the message pipeline
([ADR-0002](0002-persist-then-broadcast.md)) is the core of the system. A managed broker that speaks a
*different* protocol would mean rewriting and re-testing the most delicate code in the repo.

## Decision

Deploy on **AWS**, mapping each local component to a managed service where one exists:

| Component | AWS service |
|---|---|
| API (ASP.NET Core) | ECS / Fargate |
| Angular client | S3 + CloudFront |
| PostgreSQL | RDS |
| Redis | ElastiCache |
| **RabbitMQ** | **Amazon MQ** (managed RabbitMQ, AMQP 0.9.1) |
| Object storage | S3 (the `AWSSDK.S3` client already targets it) |
| ScyllaDB | self-hosted on EC2 (no managed Scylla) |
| Voice | LiveKit Cloud |

**Amazon MQ for RabbitMQ is the decision that picks the cloud.** It runs actual RabbitMQ, so
`RabbitMQ.Client` connects unchanged — no protocol port, no pipeline rewrite.

The stack is codified as infrastructure-as-code and run **stand-up / tear-down**, not always-on: full
always-on is ~$90/mo against ~$33/mo of credits, so it's brought up for the k6 load test and the
defense window, then torn down. An AWS Budgets alarm guards against credits silently rolling into
billing.

## Consequences

- **Zero application-code change for messaging.** The single biggest risk in a cloud migration — the
  broker — is neutralized by choosing a managed service that runs the same broker.
- **ScyllaDB is the one piece with no managed option**, so it's self-hosted on EC2 and is where the
  credit budget has to be spent carefully (it needs real memory).
- Horizontal scale on Fargate is **only safe because of [ADR-0006](0006-client-side-message-ordering.md)** —
  multiple API tasks are competing consumers that interleave message delivery, which the client-side
  snowflake ordering already tolerates. Without that decision, this one would reintroduce a
  correctness bug.
- Free-tier sizing proves the system *works*, not that it hits the 10k-concurrent target; the plan is
  to scale up briefly for load testing, then back down.

## Alternatives considered

- **Azure.** Ruled out by the broker. Azure Service Bus speaks **AMQP 1.0**, a different protocol from
  RabbitMQ's AMQP 0.9.1 — `RabbitMQ.Client` doesn't talk to it, so the message pipeline would need
  rewriting and re-verifying against a new client library. That's exactly the code the project most
  wants to leave alone. (Azure *does* offer other paths, but none keep the existing broker code
  untouched the way Amazon MQ does.)
- **Self-hosted / home-PC hybrid.** The earlier plan. Dropped: fragile for a live defense, no managed
  backing services, and it makes the "could scale to 10k" story unprovable.
- **Kubernetes (EKS or self-managed).** More portable and more powerful, but disproportionate
  operational weight for a stand-up/tear-down demo environment. Fargate runs the same containers with
  far less to manage.
- **Fully serverless (Lambda) API.** A poor fit for a stateful SignalR server holding long-lived
  WebSocket connections and a Redis backplane. ECS/Fargate keeps the connection-oriented model intact.
