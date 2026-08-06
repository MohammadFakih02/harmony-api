# 0010 — Email-code 2FA, no TOTP

**Status:** Accepted

## Context

The app offers two-factor authentication. The standard choice is **TOTP** — the authenticator-app
scheme (Google Authenticator, Authy) where the server stores a shared secret, shows a QR code, and the
app generates rotating 6-digit codes. It's phishing-resistant-ish, offline, and expected.

But TOTP drags a whole surface with it: QR-code provisioning UI, a securely-stored per-user secret,
clock-skew tolerance, and — the real cost — **recovery codes**. Lose the phone and, without recovery
codes, the account is gone; so TOTP effectively *requires* a recovery-code generation/storage/display
flow to be safe. That's a lot of security-critical UX for a project that already runs a full,
verified email pipeline (verification, password reset, change-email confirmation).

## Decision

Implement 2FA as **emailed one-time codes**, no TOTP:

- On login, a 2FA-enabled account that isn't on a remembered device gets a code emailed; it completes
  at `POST /api/auth/2fa/verify`.
- **30-day "remember this device"** via a `trusted_device` HttpOnly cookie, so 2FA isn't a
  per-login tax on a browser the user already proved out.
- The same emailed-code mechanism, purpose-scoped, backs a **step-up gate** on the sensitive
  credential changes (change-password, change-email): a 2FA account must supply a fresh code before
  either takes effect — closing the gap where a stolen 30-day device cookie plus a phished password
  could otherwise pass a password-only check.
- Google sign-in ([its own trust anchor]) bypasses local 2FA — a federated login is already
  second-factored by Google.

## Consequences

- **Zero new delivery infrastructure.** 2FA reuses the exact email pipeline built for verification and
  password reset. One sender, one set of templates, one thing to operate.
- **No recovery-code burden.** The recovery path *is* email — the same inbox that receives the codes.
  Lose your device and you still get the code; there's no separate secret to lose and no recovery-code
  UX to build, store, or explain.
- **No authenticator-app UX.** No QR provisioning, no shared-secret storage, no clock-skew handling.
- **The security tradeoff is explicit and accepted.** Email 2FA is weaker than TOTP: it's only as
  strong as the email account, and email is phishable/interceptable in ways an offline TOTP secret
  isn't. For this app's threat model — a demo/portfolio communication platform, not a bank — "a second
  factor that's actually enabled and can't lock you out" beats "a stronger factor with a recovery-code
  cliff." The step-up gate on credential changes recovers much of the practical gap.
- **The trusted-device cookie is a real credential** and is revoked aggressively: disabling 2FA,
  resetting or changing the password, and an explicit "require 2FA on all devices again" all clear
  every trusted device, so a stale remembered-device can't outlive the event that should have killed
  it.

## Alternatives considered

- **TOTP (authenticator app).** The stronger, more conventional factor. Rejected for this project on
  cost/benefit: it mandates recovery codes to be safe (or it becomes an account-loss trap), plus
  QR/secret/skew handling — a large security-critical surface, when a verified email pipeline already
  exists and gives a lockout-proof factor for free. A reasonable *future* addition, not the first 2FA
  to build.
- **SMS codes.** Familiar to users, but requires a paid SMS provider, carries real per-message cost,
  and is weaker than email against SIM-swap. No infrastructure advantage over email here.
- **WebAuthn / passkeys.** The genuinely strong, phishing-resistant answer and the right long-term
  direction. Disproportionate to stand up now (platform authenticator support, attestation, fallback
  flows) for a defense timeline; noted as future work.
- **No "remember device."** Simpler and marginally safer, but makes 2FA a code-per-login chore that
  users disable out of annoyance — a factor that's turned off protects nothing. The 30-day cookie,
  paired with the credential-change step-up gate, keeps 2FA tolerable without leaving the sensitive
  operations behind a cookie alone.
