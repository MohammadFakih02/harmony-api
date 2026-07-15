# Harmony load tests (k6)

Two scenarios:

| File                | What it measures                                                        |
| ------------------- | ----------------------------------------------------------------------- |
| `rest-read.js`      | The REST read path: bootstrap, channels, members, message history.       |
| `message-fanout.js` | End-to-end send → `MessageReceived`, over a real SignalR connection.     |

`message-fanout.js` is the one that characterises the system. The hub's reply to `SendMessage` is
only an *accept* ack — the message isn't real until it has crossed RabbitMQ, been persisted to
Scylla by the consumer, and been broadcast back out. The scenario stamps each send with a nonce and
times the round trip to its own echo, so the number it reports is the whole pipeline.

## Setup

k6 is a single Go binary (it is **not** an npm package — it embeds its own JS runtime, so `npm
install k6` gets you something else):

```bash
# Debian/Ubuntu
sudo gpg -k && sudo gpg --no-default-keyring --keyring /usr/share/keyrings/k6-archive-keyring.gpg \
  --keyserver hkp://keyserver.ubuntu.com:80 --recv-keys C5AD17C747E3415A3642D57D77C6C491D6AC1D69
echo "deb [signed-by=/usr/share/keyrings/k6-archive-keyring.gpg] https://dl.k6.io/deb stable main" \
  | sudo tee /etc/apt/sources.list.d/k6.list
sudo apt-get update && sudo apt-get install k6
```

Then, from `harmony-api/`, with the docker stack up and the API running:

```bash
# 1. Seed the demo guild (only needed once).
dotnet run --project tools/Harmony.DevSeed

# 2. Provision load-test accounts + dump their JWTs to load-tests/users.json.
#    Seed at least as many users as your peak VU count (see "Why users matter" below).
dotnet run --project tools/Harmony.DevSeed -- --load-test-users=50

# 3. Run.
k6 run load-tests/rest-read.js
k6 run load-tests/message-fanout.js
```

`users.json` holds real credentials and is gitignored — it is a dev-only artifact.

## Knobs

| Env var             | Default             | Applies to     |
| ------------------- | ------------------- | -------------- |
| `API_BASE`          | from `users.json`   | both           |
| `USERS_FILE`        | `./users.json`      | both           |
| `VUS`               | 20 / 10             | both           |
| `SESSION_SECONDS`   | 60                  | fan-out        |
| `SEND_INTERVAL_MS`  | 3000                | fan-out        |
| `DRAIN_SECONDS`     | 3                   | fan-out        |

```bash
k6 run -e VUS=50 -e SEND_INTERVAL_MS=1000 load-tests/message-fanout.js
```

## Before you trust a number

**Turn the rate limiter off.** `appsettings.Development.json` ships with `RateLimiting:Enabled:
false` for exactly this reason. Every policy partitions by user id or IP, and a k6 run drives
thousands of requests from one machine through a handful of accounts — with the limiter on you are
measuring `FixedWindowRateLimiter`, not Harmony. (To measure the limiter's own cost deliberately,
flip it back on and compare.)

**Watch the token clock.** JWTs are minted with `Jwt:AccessTokenExpiryMinutes` (15) and validated
with `ClockSkew = TimeSpan.Zero`, so they expire exactly. The harness does not refresh. A run longer
than the token lifetime turns into a wall of 401s and hub disconnects — for a soak test, raise that
key and re-seed.

**Kill any stray API.** A second `dotnet run` (or a leftover test host) competes for the RabbitMQ
queue and will consume messages this test is waiting for — the fan-out scenario reports them as
dropped. Check that the queue has exactly one consumer.

**Purge the queues between runs.** An overloaded run leaves a backlog that is still draining minutes
later, and the next run queues behind it — its numbers are then measuring the previous run. The tell is
a nonzero `min` on `fanout_duration`: the very first message of a clean run is delivered in
milliseconds. The dead-letter queue is worth clearing at the same time, so a real failure stands out
instead of hiding among test residue (integration-test edits land there by design — see CLAUDE.md §18):

```bash
docker exec harmony-rabbitmq-1 rabbitmqctl purge_queue harmony.messages.scylla
docker exec harmony-rabbitmq-1 rabbitmqctl purge_queue harmony.dead-letter.queue
```

### Why user count matters

`userForVu` wraps around when there are fewer seeded users than VUs. That's safe, but several server
limits partition **by user id** — the write limiter's `user:w:{id}` bucket, per-channel slowmode. Run
100 VUs across 5 accounts and you are measuring those limits. Seed ≥ peak VUs.

## Reading the results

Thresholds are the pass/fail gate: a breach exits k6 with code **99**, which is what makes these
usable in CI. The ones that matter:

- `harmony_fanout_delivered` — **rate > 0.99**. Every accepted send must come back out. A miss is a
  dropped message.
- `harmony_fanout_duration` — p95 < 2s, send → echo.
- `harmony_message_failed` — count < 1. Counts both `MessageFailed` broadcasts and rejected
  `HubResult` envelopes (the hub returns failures as `{succeeded: false}`, not as errors).
- `history not degraded` — `degraded: true` means the Scylla circuit breaker served a fallback, so
  the run is measuring the breaker rather than the read path.

## Running this on modest hardware

The whole stack — Postgres, Scylla, Redis, RabbitMQ, MinIO, the API, *and* k6 — on one laptop means
they compete for the same cores, and k6 is itself CPU-hungry. Two consequences:

1. **You are measuring the laptop, not the system.** Numbers from a 4-core box establish a
   *relative* baseline (did this change make it better or worse?), not a capacity figure. The
   10k-concurrent goal needs the AWS environment (§20) with k6 on a separate machine.
2. **Scylla is the first thing to starve.** `docker/docker-compose.yml` pins it to `--smp 2
   --memory 2G`. Under fan-out load it is the likeliest bottleneck, and a starved Scylla trips the
   circuit breaker, which shows up as `degraded: true` rather than as an obvious error.

Start at `VUS=10` and climb until a threshold breaks. `docker stats` in another terminal tells you
whether you found a Harmony limit or just ran out of laptop — if a container is pinned at 100% CPU,
it's the latter.
