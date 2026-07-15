// Scenario 2 — end-to-end message fan-out latency over SignalR.
//
// This is the number that actually characterises Harmony. Each VU opens a real hub connection,
// joins the load-test channel, and sends messages stamped with a client nonce. The hub's reply is
// only an ACCEPT ack — it means "queued", not "delivered". The message is not real until it has
// crossed RabbitMQ, been persisted to Scylla by the consumer, and been broadcast back out as
// MessageReceived (IChatClient: "Fired after the RabbitMQ consumer confirms ScyllaDB persistence").
//
// So we time send → our own MessageReceived echo, matched on the nonce that survives the round trip
// (MessageResponse.Nonce is echoed on the live broadcast only). That measures the whole pipeline,
// which is the thing that falls over first under load.
//
//   k6 run load-tests/message-fanout.js
//   k6 run -e VUS=25 -e SESSION_SECONDS=60 load-tests/message-fanout.js

import ws from 'k6/ws';
import { check, fail } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import {
  HANDSHAKE,
  MessageType,
  invocation,
  isHandshakeResponse,
  negotiate,
  parseRecords,
  socketUrl,
  startKeepAlive,
} from './lib/signalr.js';
import { apiBase, channelId, guildId, userForVu } from './lib/fixture.js';

const fanoutLatency = new Trend('harmony_fanout_duration', true);
const ackLatency = new Trend('harmony_send_ack_duration', true);
const delivered = new Rate('harmony_fanout_delivered');
const failedSends = new Counter('harmony_message_failed');

const SESSION_SECONDS = Number(__ENV.SESSION_SECONDS || 60);
const SEND_INTERVAL_MS = Number(__ENV.SEND_INTERVAL_MS || 3000);

// Stop sending this long before closing, so messages already in flight can complete the round trip.
// Without it every session closes on top of its own last send and reports it as dropped — the
// harness truncating itself, scored as a system failure. Sized well above a healthy p95 (~40ms
// locally) so it only ever forgives the tail, never a real drop.
const DRAIN_SECONDS = Number(__ENV.DRAIN_SECONDS || 3);

export const options = {
  scenarios: {
    fanout: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '20s', target: Number(__ENV.VUS || 10) },
        { duration: `${SESSION_SECONDS}s`, target: Number(__ENV.VUS || 10) },
        { duration: '10s', target: 0 },
      ],
      gracefulRampDown: '15s',
    },
  },
  thresholds: {
    checks: ['rate>0.99'],
    // Every accepted send must come back out. A miss here is a dropped message, which is the one
    // failure mode this whole architecture exists to prevent — so the bar is absolute.
    harmony_fanout_delivered: ['rate>0.99'],
    harmony_fanout_duration: ['p(95)<2000'],
    harmony_send_ack_duration: ['p(95)<500'],
    harmony_message_failed: ['count<1'],
  },
};

export default function () {
  const user = userForVu(__VU);
  const base = apiBase();
  const connectionToken = negotiate(base, user.token);

  // Sent nonce → send timestamp. An entry that never clears is an undelivered message.
  const pending = {};
  let sent = 0;
  let acked = 0;
  let handshakeDone = false;
  let invocationId = 0;

  const res = ws.connect(socketUrl(base, connectionToken, user.token), {}, function (socket) {
    socket.on('open', () => socket.send(HANDSHAKE));

    socket.on('message', (raw) => {
      for (const record of parseRecords(raw)) {
        if (!handshakeDone && isHandshakeResponse(record)) {
          handshakeDone = true;
          if (record.error) fail(`handshake rejected: ${record.error}`);

          // Subscribing to the channel group is what makes the broadcast reach this connection.
          socket.send(invocation('JoinChannel', [channelId()], ++invocationId));
          startKeepAlive(socket);
          startSending(socket);
          continue;
        }

        if (record.type === MessageType.PING) continue;

        if (record.type === MessageType.COMPLETION) {
          // The hub's HubResult envelope: failures come back as { succeeded: false }, NOT as
          // exceptions, so a completion without an `error` can still be a rejected send.
          const result = record.result;
          if (result && result.succeeded === false) {
            failedSends.add(1);
            console.error(`send rejected: ${result.errorMessage}`);
          }
          continue;
        }

        if (record.type === MessageType.INVOCATION) {
          handleServerCall(record);
          continue;
        }

        if (record.type === MessageType.CLOSE) {
          socket.close();
        }
      }
    });

    function handleServerCall(record) {
      const payload = record.arguments && record.arguments[0];
      if (!payload) return;

      if (record.target === 'MessageReceived' && payload.nonce && pending[payload.nonce]) {
        fanoutLatency.add(Date.now() - pending[payload.nonce]);
        delivered.add(true);
        delete pending[payload.nonce];
        acked++;
        return;
      }

      // Without this the pipeline could be silently dropping messages and the run would just look
      // slow — an unacked send is indistinguishable from an in-flight one.
      if (record.target === 'MessageFailed') {
        failedSends.add(1);
        delivered.add(false);
        console.error(`MessageFailed: ${JSON.stringify(payload)}`);
      }
    }

    function startSending(socket) {
      // A flag rather than clearInterval: k6/ws's Socket exposes setInterval but NOT clearInterval,
      // and calling the missing method throws inside the callback, which aborts the whole iteration.
      let sending = true;

      socket.setInterval(() => {
        if (!sending) return;
        // Ids go as strings: the hub reads them via AllowReadingFromString, and JS numbers cannot
        // hold a 64-bit snowflake without losing precision.
        const nonce = `k6-${__VU}-${++sent}-${Date.now()}`;
        pending[nonce] = Date.now();
        const sentAt = Date.now();

        socket.send(
          invocation(
            'SendMessage',
            // All six parameters, always — SignalR requires an exact argument count.
            [channelId(), guildId(), `load test message ${sent} from VU ${__VU}`, null, null, nonce],
            ++invocationId
          )
        );
        ackLatency.add(Date.now() - sentAt);
      }, SEND_INTERVAL_MS);

      // Quiesce, then close: stop sending first and let the in-flight tail land.
      socket.setTimeout(
        () => {
          sending = false;
        },
        Math.max(1, SESSION_SECONDS - DRAIN_SECONDS) * 1000
      );
      socket.setTimeout(() => socket.close(), SESSION_SECONDS * 1000);
    }
  });

  check(res, { 'websocket handshake 101': (r) => r && r.status === 101 });

  // Anything still pending when the socket closed was never delivered. Counting it here (rather
  // than only on receipt) is what stops a silent drop from reading as a pass.
  for (const nonce in pending) {
    delivered.add(false);
  }
  check(null, { 'all sends acked': () => Object.keys(pending).length === 0 });
}
