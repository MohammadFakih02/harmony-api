// Shared access to the users.json written by `dotnet run --project tools/Harmony.DevSeed
// -- --load-test-users=N`. Every scenario loads its accounts through here.

import { SharedArray } from 'k6/data';

// Relative to THIS file, not the working directory: k6's open() resolves against the module that
// calls it, so from lib/ the fixture is one level up. Override with an absolute path.
const FIXTURE_PATH = __ENV.USERS_FILE || '../users.json';

// SharedArray, not a plain array: k6 gives every VU its own JS runtime, so a top-level array would
// be parsed and held once PER VU. At a few hundred VUs on a 12 GB laptop that is the difference
// between a load test and an OOM. SharedArray keeps one copy in the host and hands out read-only
// views. The callback runs exactly once, in the init context.
const fixture = new SharedArray('harmony-fixture', () => {
  const raw = open(FIXTURE_PATH);
  const parsed = JSON.parse(raw);

  if (!parsed.users || parsed.users.length === 0) {
    throw new Error(`${FIXTURE_PATH} has no users — re-run the seeder.`);
  }
  // SharedArray must wrap an ARRAY, so the single fixture object is boxed in one.
  return [parsed];
});

export const data = () => fixture[0];

/**
 * One account per VU, wrapping around when there are more VUs than seeded users.
 *
 * Reusing an account across VUs is safe here but not free: several server limits partition by user
 * id (the write limiter's `user:w:{id}` bucket, slowmode), so a run with far fewer users than VUs
 * measures those limits rather than the system. Seed at least as many users as peak VUs.
 */
export function userForVu(vuId) {
  const users = data().users;
  return users[(vuId - 1) % users.length];
}

export const apiBase = () => __ENV.API_BASE || data().apiBase;
export const guildId = () => data().guildId;
export const channelId = () => data().channelId;

export const authHeaders = (user) => ({
  Authorization: `Bearer ${user.token}`,
  'Content-Type': 'application/json',
});
