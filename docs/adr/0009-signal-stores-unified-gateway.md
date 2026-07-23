# 0009 — Self-subscribing signal stores over one unified gateway stream

**Status:** Accepted

## Context

The client receives a constant flow of server-pushed events over SignalR: new messages, edits,
deletes, reactions, presence changes, typing, unread updates, notifications, voice-state changes,
member joins, role updates, and more. Something has to (a) turn each raw SignalR payload into a
typed event, and (b) route each event to the piece of state it affects.

An early design wired all of this centrally: the shell component subscribed to every server method and
dispatched into every store. That one file grew a dependency on **every feature in the app** — adding
a feature meant editing shared wiring, and the shell became the thing everyone touched and everyone
feared. It was a coupling magnet.

## Decision

Two moving parts, no central dispatcher:

- **One gateway, one stream.** `HarmonyHubClient` is the *only* code that knows about raw SignalR
  payloads. It registers a handler per server method, normalizes the payload (crucially, coercing
  every snowflake ID to a string — [ADR-0003](0003-snowflake-ids.md)), and emits a single
  discriminated union, `GatewayEvent`, on one observable. The event `type` mirrors the backend
  `IChatClient` method name exactly, so one log line on the stream traces the entire live pipeline.
- **Self-subscribing stores.** State is split into **one `@ngrx/signals` store per slice** (messages,
  presence, unread, notifications, voice, …). Each store subscribes to the gateway stream **itself**
  via `withHooks(onInit)`, filters for the event types it cares about, and patches its own slice.
  Components never subscribe to the socket and never call HTTP — they read signals and call store
  methods.

## Consequences

- **Features are self-contained.** A new store wires up its own subscription; no shared file changes.
  The shell was slimmed to only genuinely cross-cutting, location-aware concerns. The coupling magnet
  is gone.
- **The gateway is the single seam for the whole real-time surface.** All payload coercion (ID
  stringification, `long → number`) lives in one place; the rest of the app consumes a clean typed
  union and never sees a raw frame.
- **Change detection is signal-driven and zoneless.** A `patchState` updates signals, components
  re-render, and no component holds a manual subscription — the reconnect/refetch logic lives in the
  stores and the connection service, not scattered through the view layer.
- Testing concentrates where bugs are expensive: store reducers and the gateway's payload coercion are
  unit-tested; components are largely untested by design because the logic has been pushed out of
  them.
- A store reading events it doesn't own is possible in principle (they all see the one stream), so the
  discipline is "filter by type, patch only your slice" — enforced by convention and review, not the
  type system.

## Alternatives considered

- **Central dispatcher in the shell (the original).** All subscriptions in one place — easy to see the
  whole map at once, but it made one file depend on everything and turned every feature addition into
  a shared-file edit. The concrete pain that motivated the rewrite.
- **A store per feature that each open their own SignalR subscription.** Decouples features, but now
  raw-payload knowledge (and the ID-stringification gotcha) is duplicated across every store — exactly
  the fragile, silently-corrupting code [ADR-0003](0003-snowflake-ids.md) wants confined to one place.
  The single gateway keeps decoupling *and* one coercion seam.
- **NgRx global store (actions + reducers + effects).** Powerful and familiar, but heavy for this app:
  a lot of boilerplate, and signal stores compose more naturally with Angular's signal-based zoneless
  change detection. The per-slice signal store gives the same separation with far less ceremony.
- **RxJS subjects in components, no store layer.** Fine for a small app; here it would scatter
  real-time state and reconciliation logic across the view layer, which is what the store boundary
  exists to prevent.
