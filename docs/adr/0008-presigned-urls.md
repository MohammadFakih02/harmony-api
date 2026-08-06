# 0008 — Presigned URLs; never expose the object store directly

**Status:** Accepted

## Context

Users upload and download files — images, video, audio, documents — attached to messages. File bytes
are large and their transfer is slow relative to a JSON API call. There are two ways to move them:

1. **Through the API** — the client POSTs bytes to the API, the API streams them to storage; downloads
   reverse it. Every byte crosses the application tier twice.
2. **Directly to/from the object store** — the client talks to storage itself, with the API only
   authorizing and bookkeeping.

Option 1 turns the stateless, connection-light API into a bandwidth pipe: a few concurrent large
uploads tie up request threads and memory that should be serving thousands of small real-time calls.
And exposing the object store directly to clients (public bucket, raw credentials) is a non-starter —
anyone could enumerate or overwrite anyone's files.

## Decision

The object store (**MinIO** in dev/test, **S3** in prod) is **never exposed directly** to clients
(non-negotiable #5). All access is via **short-lived presigned URLs** minted by the API:

- **Upload** is a three-step lifecycle (see the `FilesController` docs): the API mints a presigned
  **PUT** URL and an unconfirmed row → the client uploads bytes **straight to storage** → the client
  calls **confirm**, which validates the object (size, image dimensions) and marks the row usable.
- **Download** mints a presigned **GET** URL (≈15-minute lifetime, client-cacheable for just under
  that). A batch endpoint mints URLs for a whole message page in one round trip.
- The API holds the storage credentials; the client never does. A presigned URL grants exactly one
  operation on exactly one object for a few minutes.

## Consequences

- **The API stays a control plane.** File bytes flow client↔storage directly and never occupy an
  application request thread. The tier that must stay responsive for real-time traffic isn't a file
  proxy.
- **Authorization is still fully the API's.** Presign is gated on channel-scoped `AttachFiles`,
  download on `ViewChannel` — the URL is only minted after the permission check, and it expires fast.
- **Orphan handling is required.** A presign that the client abandons (uploads bytes but never
  confirms, or never uploads at all) leaves an unconfirmed row and maybe a stray object. A background
  sweep deletes unconfirmed rows past a grace period and best-effort removes the object, so an
  abandoned upload doesn't leak storage. This is the cost of the direct-upload model and is handled
  explicitly rather than ignored.
- **Validation happens at confirm, not at upload.** Because bytes bypass the API, size/dimension
  checks run when the client calls confirm against the now-stored object — not inline in an upload
  stream.
- The same presign pattern generalizes to avatars, banners, and guild icons (user-scoped presign +
  an anonymous public-serve route for images that are meant to be public).

## Alternatives considered

- **Stream files through the API.** Simplest mental model and keeps all logic in one tier. Rejected:
  it makes the real-time API a bandwidth bottleneck, ties up threads/memory on slow transfers, and
  scales the wrong resource. The whole architecture works to keep expensive work *off* the hot path.
- **Public bucket / direct object-store URLs.** Fastest and zero API involvement per download, but no
  per-file authorization at all — any URL guesser reads anyone's attachments, and there's no
  channel-permission gate. Flatly incompatible with a permission system.
- **Long-lived signed URLs.** Fewer mint calls, but a leaked URL is then a long-lived capability.
  Short expiry plus client-side caching gets most of the performance with a small blast radius.
