// Scenario 1 — REST read path under load.
//
// Models what a client actually does on a cold open: fetch the bootstrap payload, then the guild's
// channels, members and a page of message history. These are the endpoints that fan out into
// Postgres joins and Scylla reads, so this is where AddDbContextPool, the response compression, and
// the permission-resolution caches show up.
//
//   k6 run load-tests/rest-read.js
//   k6 run -e API_BASE=http://localhost:5057 --vus 50 --duration 2m load-tests/rest-read.js

import http from 'k6/http';
import { check, group } from 'k6';
import { Trend } from 'k6/metrics';
import { apiBase, authHeaders, channelId, guildId, userForVu } from './lib/fixture.js';

const bootstrapLatency = new Trend('harmony_bootstrap_duration', true);
const historyLatency = new Trend('harmony_history_duration', true);

export const options = {
  scenarios: {
    reads: {
      executor: 'ramping-vus',
      startVUs: 0,
      // Gentle ramp — a step function measures the ramp, not the steady state.
      stages: [
        { duration: '30s', target: Number(__ENV.VUS || 20) },
        { duration: '1m', target: Number(__ENV.VUS || 20) },
        { duration: '15s', target: 0 },
      ],
      gracefulRampDown: '10s',
    },
  },
  thresholds: {
    // A breached threshold exits k6 with code 99, which is what makes this usable as a CI gate.
    checks: ['rate>0.99'],
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
    harmony_bootstrap_duration: ['p(95)<800'],
    harmony_history_duration: ['p(95)<500'],
  },
  // Response bodies are discarded once checked — holding them per VU is pure memory cost.
  discardResponseBodies: false,
};

export default function () {
  const user = userForVu(__VU);
  const base = apiBase();
  const params = { headers: authHeaders(user) };

  group('bootstrap', () => {
    const res = http.get(`${base}/api/users/me/bootstrap`, {
      ...params,
      tags: { name: 'bootstrap' },
    });
    bootstrapLatency.add(res.timings.duration);
    check(res, { 'bootstrap 200': (r) => r.status === 200 });
  });

  group('guild', () => {
    // batch() issues these concurrently from one VU, which is how the real client loads a guild —
    // sequential gets would understate connection-pool and DbContext-pool pressure.
    const responses = http.batch([
      ['GET', `${base}/api/guilds/${guildId()}/channels`, null, { ...params, tags: { name: 'channels' } }],
      ['GET', `${base}/api/guilds/${guildId()}/members`, null, { ...params, tags: { name: 'members' } }],
    ]);
    check(responses[0], { 'channels 200': (r) => r.status === 200 });
    check(responses[1], { 'members 200': (r) => r.status === 200 });
  });

  group('history', () => {
    const res = http.get(
      `${base}/api/guilds/${guildId()}/channels/${channelId()}/messages`,
      { ...params, tags: { name: 'history' } }
    );
    historyLatency.add(res.timings.duration);
    check(res, {
      'history 200': (r) => r.status === 200,
      // `degraded` is the Scylla circuit breaker reporting that it served a fallback rather than a
      // real read. A load run that trips it is measuring the breaker, so surface it as a failure.
      'history not degraded': (r) => r.status === 200 && r.json('degraded') === false,
    });
  });
}
