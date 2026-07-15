// Minimal SignalR JSON-protocol client for k6.
//
// There is no k6 SignalR library, and @microsoft/signalr can't run here — k6 executes JS on goja,
// not Node, so there is no XHR/WebSocket global for it to bind to. The protocol is small enough to
// speak directly, which is what this does. Everything below matches the server's configuration in
// Harmony.API/Extensions/DependencyInjection.cs.
//
// Wire format, in order:
//   1. POST {base}/hubs/chat/negotiate?negotiateVersion=1  → { connectionToken, ... }
//   2. WS   {base}/hubs/chat?id={connectionToken}&access_token={jwt}
//   3. send {"protocol":"json","version":1}<RS>      → server replies {}<RS> on success
//   4. invocations/broadcasts, each terminated by <RS>
//
// <RS> is 0x1e (ASCII record separator). It TERMINATES each record, and one WebSocket frame may
// carry several, so every read has to split rather than JSON.parse the frame whole.

import http from 'k6/http';

/** ASCII record separator — the frame delimiter for SignalR's JSON protocol. */
export const RS = '\x1e';

export const HANDSHAKE = JSON.stringify({ protocol: 'json', version: 1 }) + RS;

/** SignalR message types (the subset this harness cares about). */
export const MessageType = {
  INVOCATION: 1,
  COMPLETION: 3,
  PING: 6,
  CLOSE: 7,
};

/**
 * Performs the negotiate handshake and returns the connection token used to open the socket.
 * The JWT rides the Authorization header here; the WebSocket upgrade can't set headers from a
 * browser, which is why the server also accepts ?access_token= for /hubs paths (Program.cs).
 */
export function negotiate(baseUrl, token) {
  const res = http.post(`${baseUrl}/hubs/chat/negotiate?negotiateVersion=1`, null, {
    headers: { Authorization: `Bearer ${token}` },
    tags: { name: 'negotiate' },
  });

  if (res.status !== 200) {
    throw new Error(`negotiate failed: ${res.status} ${res.body}`);
  }
  return res.json('connectionToken');
}

/** Builds the WebSocket URL. http(s):// → ws(s):// — k6's ws module rejects an http scheme. */
export function socketUrl(baseUrl, connectionToken, token) {
  const wsBase = baseUrl.replace(/^http/, 'ws');
  return `${wsBase}/hubs/chat?id=${connectionToken}&access_token=${encodeURIComponent(token)}`;
}

/**
 * Splits a raw frame into parsed records. Returns [] for the keep-alive-only frames so callers can
 * ignore them uniformly. A trailing empty segment is expected: <RS> terminates rather than
 * separates, so "a<RS>b<RS>".split(RS) always yields a final "".
 */
export function parseRecords(raw) {
  const out = [];
  for (const chunk of String(raw).split(RS)) {
    if (chunk.length === 0) continue;
    try {
      out.push(JSON.parse(chunk));
    } catch (_) {
      // A record we can't parse is not worth failing a load run over.
    }
  }
  return out;
}

/** True for the server's handshake reply — the only record with no `type` field. */
export function isHandshakeResponse(record) {
  return record.type === undefined;
}

/**
 * Encodes a hub invocation. `invocationId` is what makes it a request/response call: omit it and
 * the server treats the call as fire-and-forget and never sends a Completion back.
 *
 * IMPORTANT: `args` must supply EVERY hub parameter. SignalR binds arguments positionally and
 * rejects a count mismatch ("Invocation provides 3 argument(s) but target expects 6") before the
 * method body runs — a C# default value does not make a parameter optional over the wire.
 */
export function invocation(target, args, invocationId) {
  const msg = { type: MessageType.INVOCATION, target, arguments: args };
  if (invocationId !== undefined) msg.invocationId = String(invocationId);
  return JSON.stringify(msg) + RS;
}

export const ping = () => JSON.stringify({ type: MessageType.PING }) + RS;

/**
 * Keeps the connection alive. The server disconnects a client it hasn't heard from within
 * ClientTimeoutInterval (30s), and a load test's VU can easily sit idle longer than that between
 * sends, so this pings well inside the window.
 */
export function startKeepAlive(socket) {
  return socket.setInterval(() => socket.send(ping()), 10000);
}
