# 0004 — JWT in memory, refresh token in an HttpOnly cookie

**Status:** Accepted

## Context

A single-page app has to hold *some* credential to make authenticated calls, and where it holds it is
the whole ballgame for two attack classes:

- **XSS** — any script running on the page can read anything JavaScript can read: `localStorage`,
  `sessionStorage`, in-memory variables. If the long-lived credential is reachable from JS, one XSS
  is a permanent account takeover.
- **CSRF** — a cookie the browser attaches automatically can be triggered by a malicious third-party
  page.

No single storage location is safe from both. The design has to split the credential so that the
piece exposed to XSS is nearly worthless and the piece that matters is unreachable from JS.

## Decision

Two tokens with different lifetimes and different homes:

- **Access token (JWT, 15 minutes)** — returned in the response body, held in **Angular memory only**,
  sent as a `Bearer` header. Never written to `localStorage` (non-negotiable #4).
- **Refresh token (7 days)** — lives only in an **`HttpOnly`, `Secure`, `SameSite=Strict` cookie**
  the browser attaches to `/api/auth/*` and that JavaScript cannot read. Exchanged for a fresh access
  token at `POST /api/auth/refresh`, rotating on each use.

`SameSite=Strict` is the CSRF defense for the cookie; the 15-minute access-token lifetime is the blast
radius for the in-memory token.

## Consequences

- **XSS can't steal a durable session.** The only JS-reachable token expires in 15 minutes and can't
  be refreshed without the HttpOnly cookie. An attacker who runs script gets a short window, not a
  permanent foothold.
- **CSRF can't ride the cookie to a useful endpoint.** `SameSite=Strict` keeps the refresh cookie off
  cross-site requests; the actual API calls authenticate with the `Bearer` header, which a
  cross-site page can't set.
- **A page refresh loses the access token — by design.** The app silently calls `/refresh` on boot to
  mint a new one from the cookie. This is why `refresh` and `logout` are `AllowAnonymous`: they
  authenticate off the cookie, not the (possibly-expired) bearer token. Logout being anonymous is
  load-bearing — if it required a valid access token, an expired one would 401 before the refresh
  token got revoked, leaving a cookie that could silently log the "logged-out" user back in.
- **Server-side revocation is real.** Refresh tokens are rows, not just signatures, so password reset
  / change and 2FA-disable revoke every session immediately by deleting them — something a stateless
  JWT-only scheme can't do.
- A short grace window lets a just-rotated refresh token still work once, so two near-simultaneous
  refreshes (common on app boot) don't race each other into a logout.

## Alternatives considered

- **Everything in `localStorage`.** The most common SPA pattern and the most XSS-fragile: both tokens
  readable by any script, session survives indefinitely. Directly forbidden by non-negotiable #4.
- **Access token in `localStorage`, refresh in a cookie.** Better, but still hands a working (if
  short) token to any XSS, and tempts "just refresh it" logic that re-exposes the long token. Keeping
  the access token in memory only costs a silent refresh on reload and closes that gap.
- **Both tokens in HttpOnly cookies (pure cookie auth).** Immune to XSS token theft, but now *every*
  API call is a cookie call and the whole surface needs CSRF tokens — more moving parts, and it
  couples the API to browser cookie semantics for what is otherwise a plain bearer API.
- **Stateless JWT with no refresh token.** No server round trip to refresh, but no revocation either:
  a compromised token is valid until it expires, and "log out all sessions" / "reset password kills
  every session" become impossible. Unacceptable for an app with real account-security flows
  ([ADR-0010](0010-email-code-2fa.md)).
