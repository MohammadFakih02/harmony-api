# 0011 — Deploy the defense demo on a single EC2 instance

**Status:** Accepted — the concrete realization of [ADR-0007](0007-aws-amazon-mq.md)'s stand-up/tear-down
clause. It does not supersede 0007: 0007 is the *designed* managed topology; this records what actually
runs for the defense and why it diverges.

## Context

[ADR-0007](0007-aws-amazon-mq.md) chose a managed AWS topology — API on ECS/Fargate, Postgres on RDS,
Redis on ElastiCache, RabbitMQ on Amazon MQ, client on S3+CloudFront — and already flagged that it would
run **stand-up/tear-down** rather than always-on. Pricing it out makes the reason concrete: a full
always-on managed stack is **~$90–120/month**, against a total student budget of **~$100 of credits**.
A managed deployment left running for the few weeks around a defense burns the entire budget, and an
AWS Budgets overrun silently rolls into real billing.

What the defense actually needs is narrower than what 0007 designs for: the app reachable at a **stable
HTTPS URL** for a few weeks, **cheap**, **reproducible from one command**, and recoverable if the box
sits cold for three weeks and won't boot cleanly. It does **not** need high availability, autoscaling,
or durable data — the only data is demo data (see *Data durability — ephemeral by design* in
`AWS-HOSTING.md`).

## Decision

Run the **exact same containers** on **one EC2 instance** via `docker-compose.prod.yml`, instead of the
managed per-service topology:

| Concern | 0007 (designed) | This ADR (demo) |
|---|---|---|
| API / web | ECS/Fargate · S3+CloudFront | containers on the box (nginx serves the SPA, reverse-proxies `/api`+`/hubs`) |
| Postgres / Redis / RabbitMQ / Scylla | RDS · ElastiCache · Amazon MQ · EC2 | all four as **containers on the same box** |
| Object storage | S3 | **S3** (unchanged — real, via the instance IAM role) |
| Voice | LiveKit Cloud | **LiveKit Cloud** (unchanged) |
| TLS / DNS | CloudFront | **DuckDNS + Let's Encrypt** on the box |
| Image source | ECR | **GHCR** — built+pushed by CD, pulled by the box (never built on it) |

The box **only ever pulls images**; the CD workflow (`.github/workflows/cd.yml` in each repo) builds and
pushes `latest` + `sha-<commit>` tags to GHCR on merge to `main`. Deployment is `docker compose pull &&
up -d`; rollback is pinning `IMAGE_TAG=sha-<commit>` in `.env`. The whole stack is codified
(`docker-compose.prod.yml` + `AWS-HOSTING.md`), stood up ~3–4 weeks before the defense and torn down
after; parked it costs only the ~$2.60/month EBS volume.

## Consequences

- **Cost drops from ~$90–120/mo to a few dollars while running** (and ~$2.60/mo parked) — the budget now
  comfortably covers the demo window with credits to spare for a scale-up during the k6 load test.
- **The artifact is byte-identical to what Fargate would run.** Images are built off-box and pulled from
  a registry, so the running container is the one CI tested — the same immutable-artifact model as ECR.
  Moving to ECS later is a change of *scheduler*, not of build or release process. This is the honest
  answer to "why not Fargate?" and it is only true because CD builds the images (not a hand-built box).
- **Single point of failure, no HA, no autoscaling.** Acceptable for a time-boxed demo; it is exactly
  what 0007 remains the target for if this were a real product.
- **Data is ephemeral** — one box, one EBS volume, no backups by design. Recovery is the golden AMI +
  re-seed, not a data restore (documented in `AWS-HOSTING.md`).
- **Horizontal scale is untested here** (one instance), but remains *safe* by
  [ADR-0006](0006-client-side-message-ordering.md) if the API is later fanned out on Fargate.

## Alternatives considered

- **The full managed topology of [ADR-0007](0007-aws-amazon-mq.md).** The correct choice for production,
  and still the documented target — but ~$90–120/mo burns the entire credit budget for a few-week demo
  that needs none of the availability it buys. Deferred, not rejected.
- **Cloudflare tunnel instead of open 80/443.** Outbound-only and exposes nothing, but Google Sign-In
  needs a stable authorized origin (quick tunnels didn't work for OAuth), and the tunnel adds a moving
  part on demo day. The EC2 + DuckDNS + Let's Encrypt path was chosen with that trade written down (see
  *What this path traded away* in `AWS-HOSTING.md`).
- **Keep it on the home PC.** Zero cloud cost, but it requires that machine powered on and reachable from
  the venue's network during the defense. A stopped EC2 instance + a golden AMI + a recorded fallback
  video is more reliable than a home box behind an unknown NAT.
- **Managed services à la carte on a budget (e.g. RDS only).** Even a single small always-on RDS +
  ElastiCache + Amazon MQ trio approaches the credit ceiling; consolidating every stateful service into
  containers on one box is what actually fits the budget.
