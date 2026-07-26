/**
 * Automation API tests.
 *
 * Driven over real HTTP against a real `node:http` server on an ephemeral port,
 * not against the class methods directly. The things most likely to break here
 * are HTTP-level - auth header parsing, status codes, JSON body handling, route
 * matching - and none of those are exercised by calling `dispatch()` in
 * isolation.
 *
 * The security assertions are the important ones: this endpoint can start
 * browsers and hand out CDP URLs that allow full page control, so an auth
 * regression is a local privilege-escalation bug, not a cosmetic one.
 */

import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { AutomationDeps } from '../src/main/services/automation';
import { AutomationServer, timingSafeEqual } from '../src/main/services/automation';
import type { AutomationEndpoint, Profile } from '../src/shared/types';
import { newProfile } from '../src/shared/defaults';

const TOKEN = 'a'.repeat(32);

let server: AutomationServer;
let port: number;
let profiles: Profile[];
let running: Set<string>;
let deps: AutomationDeps;
let startCalls: string[];

/** Pick a free port the same way the app does, to avoid clashing with anything. */
async function freePort(): Promise<number> {
  const net = await import('node:net');
  return new Promise((resolve) => {
    const srv = net.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      const p = typeof addr === 'object' && addr ? addr.port : 0;
      srv.close(() => resolve(p));
    });
  });
}

function endpointFor(id: string): AutomationEndpoint {
  return {
    profileId: id,
    profileName: 'x',
    wsEndpoint: `ws://127.0.0.1:9222/devtools/browser/${id}`,
    httpEndpoint: 'http://127.0.0.1:9222',
    port: 9222,
  };
}

async function api(
  path: string,
  init: { method?: string; token?: string | null; body?: unknown; headers?: Record<string, string> } = {},
): Promise<{ status: number; body: any }> {
  const headers: Record<string, string> = { ...(init.headers ?? {}) };
  if (init.token !== null) headers.authorization = `Bearer ${init.token ?? TOKEN}`;
  if (init.body !== undefined) headers['content-type'] = 'application/json';

  const res = await fetch(`http://127.0.0.1:${port}${path}`, {
    method: init.method ?? 'GET',
    headers,
    body: init.body === undefined ? undefined : JSON.stringify(init.body),
  });

  const text = await res.text();
  let body: unknown;
  try {
    body = text ? JSON.parse(text) : undefined;
  } catch {
    body = text;
  }
  return { status: res.status, body };
}

beforeEach(async () => {
  profiles = [newProfile('Alpha', 'windows', 'id-alpha'), newProfile('Beta', 'macos', 'id-beta')];
  running = new Set();
  startCalls = [];

  deps = {
    listProfiles: () => profiles,
    getProfile: (id) => profiles.find((p) => p.id === id),
    createProfile: (partial) => {
      const p = { ...newProfile('New', 'windows'), ...partial } as Profile;
      profiles.push(p);
      return p;
    },
    updateProfile: (id, patch) => {
      const i = profiles.findIndex((p) => p.id === id);
      if (i < 0) return undefined;
      profiles[i] = { ...profiles[i]!, ...patch } as Profile;
      return profiles[i];
    },
    deleteProfile: (id) => {
      const before = profiles.length;
      profiles = profiles.filter((p) => p.id !== id);
      return profiles.length < before;
    },
    startSession: async (id) => {
      startCalls.push(id);
      running.add(id);
      return { ok: true };
    },
    stopSession: async (id) => {
      running.delete(id);
    },
    endpoint: (id) => (running.has(id) ? endpointFor(id) : undefined),
    isRunning: (id) => running.has(id),
  };

  port = await freePort();
  server = new AutomationServer(deps);
  await server.start({ enabled: true, port, token: TOKEN });
});

afterEach(async () => {
  await server.stop();
});

describe('lifecycle', () => {
  it('does not listen when disabled', async () => {
    const s = new AutomationServer(deps);
    await s.start({ enabled: false, port: await freePort(), token: TOKEN });
    expect(s.running).toBe(false);
  });

  it('refuses to start with an empty token', async () => {
    const s = new AutomationServer(deps);
    // An unauthenticated port that can launch browsers must never come up, even
    // if a caller passes a blank token.
    await expect(s.start({ enabled: true, port: await freePort(), token: '' })).rejects.toThrow(
      /without a token/i,
    );
    expect(s.running).toBe(false);
  });

  it('reports a clear error when the port is taken', async () => {
    const other = new AutomationServer(deps);
    await expect(other.start({ enabled: true, port, token: TOKEN })).rejects.toThrow(
      /already in use/i,
    );
  });

  it('stop() releases the port for reuse', async () => {
    await server.stop();
    const again = new AutomationServer(deps);
    await again.start({ enabled: true, port, token: TOKEN });
    expect(again.running).toBe(true);
    await again.stop();
  });
});

describe('authentication', () => {
  it('rejects a request with no token', async () => {
    const res = await api('/profiles', { token: null });
    expect(res.status).toBe(401);
  });

  it('rejects a wrong token', async () => {
    const res = await api('/profiles', { token: 'b'.repeat(32) });
    expect(res.status).toBe(401);
  });

  it('rejects a token that is a prefix of the real one', async () => {
    const res = await api('/profiles', { token: TOKEN.slice(0, 16) });
    expect(res.status).toBe(401);
  });

  it('accepts the X-Api-Token header as an alternative', async () => {
    const res = await fetch(`http://127.0.0.1:${port}/profiles`, {
      headers: { 'x-api-token': TOKEN },
    });
    expect(res.status).toBe(200);
  });

  it('leaves /health open so scripts can wait for the port', async () => {
    const res = await api('/health', { token: null });
    expect(res.status).toBe(200);
    expect(res.body.ok).toBe(true);
  });

  it('does not leak profile data through /health', async () => {
    const res = await api('/health', { token: null });
    expect(JSON.stringify(res.body)).not.toMatch(/Alpha|id-alpha/);
  });

  it('never returns a CORS allow-origin header', async () => {
    // A page on any origin can *send* a simple request to 127.0.0.1; the missing
    // ACAO header is what stops it reading the reply.
    const res = await fetch(`http://127.0.0.1:${port}/health`, {
      headers: { origin: 'https://evil.example' },
    });
    expect(res.headers.get('access-control-allow-origin')).toBeNull();
  });

  it('refuses preflight outright', async () => {
    const res = await fetch(`http://127.0.0.1:${port}/profiles`, { method: 'OPTIONS' });
    expect(res.status).toBe(405);
  });
});

describe('profiles', () => {
  it('lists profiles with running state', async () => {
    running.add('id-alpha');
    const res = await api('/profiles');
    expect(res.status).toBe(200);
    expect(res.body.profiles).toHaveLength(2);
    expect(res.body.profiles.find((p: any) => p.id === 'id-alpha').running).toBe(true);
    expect(res.body.profiles.find((p: any) => p.id === 'id-beta').running).toBe(false);
  });

  it('gets one profile', async () => {
    const res = await api('/profiles/id-alpha');
    expect(res.status).toBe(200);
    expect(res.body.profile.name).toBe('Alpha');
  });

  it('404s an unknown profile', async () => {
    const res = await api('/profiles/nope');
    expect(res.status).toBe(404);
  });

  it('creates a profile', async () => {
    const res = await api('/profiles', { method: 'POST', body: { name: 'Scripted' } });
    expect(res.status).toBe(201);
    expect(res.body.profile.name).toBe('Scripted');
    expect(profiles).toHaveLength(3);
  });

  it('patches a fingerprint, which is the scriptable editor', async () => {
    const res = await api('/profiles/id-alpha', {
      method: 'PATCH',
      body: { fingerprint: { ...profiles[0]!.fingerprint, platform: 'linux' } },
    });
    expect(res.status).toBe(200);
    expect(res.body.profile.fingerprint.platform).toBe('linux');
  });

  it('rejects a malformed JSON body', async () => {
    const res = await fetch(`http://127.0.0.1:${port}/profiles`, {
      method: 'POST',
      headers: { authorization: `Bearer ${TOKEN}`, 'content-type': 'application/json' },
      body: '{not json',
    });
    expect(res.status).toBe(400);
  });

  it('deletes a stopped profile', async () => {
    const res = await api('/profiles/id-beta', { method: 'DELETE' });
    expect(res.status).toBe(200);
    expect(res.body.deleted).toBe(true);
  });

  it('refuses to delete a running profile', async () => {
    running.add('id-beta');
    const res = await api('/profiles/id-beta', { method: 'DELETE' });
    // Deleting the data dir out from under a live Chromium would corrupt it.
    expect(res.status).toBe(409);
    expect(profiles.some((p) => p.id === 'id-beta')).toBe(true);
  });
});

describe('sessions', () => {
  it('starts a session and returns a CDP endpoint', async () => {
    const res = await api('/profiles/id-alpha/start', { method: 'POST' });
    expect(res.status).toBe(200);
    expect(res.body.wsEndpoint).toMatch(/^ws:\/\/127\.0\.0\.1:/);
    expect(res.body.httpEndpoint).toMatch(/^http:\/\/127\.0\.0\.1:/);
    expect(startCalls).toEqual(['id-alpha']);
  });

  it('is idempotent: a retry returns the same endpoint instead of failing', async () => {
    await api('/profiles/id-alpha/start', { method: 'POST' });
    const again = await api('/profiles/id-alpha/start', { method: 'POST' });
    expect(again.status).toBe(200);
    expect(again.body.alreadyRunning).toBe(true);
    // The important part: the browser was not launched twice.
    expect(startCalls).toEqual(['id-alpha']);
  });

  it('surfaces a launch failure with its real reason', async () => {
    deps.startSession = async () => ({ ok: false, error: 'Concurrent session limit reached (5).' });
    const res = await api('/profiles/id-alpha/start', { method: 'POST' });
    expect(res.status).toBe(500);
    expect(res.body.error).toMatch(/limit reached/);
  });

  it('reports when a session starts but exposes no endpoint', async () => {
    // Real case: automation was toggled on after the session had launched, so
    // Chromium has no debugging port and cannot be given one retroactively.
    deps.endpoint = () => undefined;
    const res = await api('/profiles/id-alpha/start', { method: 'POST' });
    expect(res.status).toBe(500);
    expect(res.body.error).toMatch(/restart the session/i);
  });

  it('stops a session', async () => {
    running.add('id-alpha');
    const res = await api('/profiles/id-alpha/stop', { method: 'POST' });
    expect(res.status).toBe(200);
    expect(running.has('id-alpha')).toBe(false);
  });

  it('re-attaches to a running session via /endpoint', async () => {
    running.add('id-alpha');
    const res = await api('/profiles/id-alpha/endpoint');
    expect(res.status).toBe(200);
    expect(res.body.wsEndpoint).toContain('id-alpha');
  });

  it('404s /endpoint for a stopped session', async () => {
    const res = await api('/profiles/id-alpha/endpoint');
    expect(res.status).toBe(404);
  });
});

describe('routing', () => {
  it('404s an unknown path', async () => {
    const res = await api('/nope');
    expect(res.status).toBe(404);
  });

  it('404s a known path with the wrong method', async () => {
    const res = await api('/profiles/id-alpha/start', { method: 'GET' });
    expect(res.status).toBe(404);
  });

  it('tolerates a trailing slash', async () => {
    const res = await api('/profiles/');
    expect(res.status).toBe(200);
  });

  it('ignores query strings when matching', async () => {
    const res = await api('/profiles?foo=bar');
    expect(res.status).toBe(200);
  });

  it('sets nosniff on responses', async () => {
    const res = await fetch(`http://127.0.0.1:${port}/health`);
    expect(res.headers.get('x-content-type-options')).toBe('nosniff');
  });
});

describe('timingSafeEqual', () => {
  it('matches identical strings', () => {
    expect(timingSafeEqual('abc', 'abc')).toBe(true);
  });

  it('rejects different strings', () => {
    expect(timingSafeEqual('abc', 'abd')).toBe(false);
  });

  it('rejects different lengths without throwing', () => {
    // crypto.timingSafeEqual throws on a length mismatch, which is why both
    // sides are hashed to a fixed width first.
    expect(() => timingSafeEqual('a', 'abcdef')).not.toThrow();
    expect(timingSafeEqual('a', 'abcdef')).toBe(false);
  });

  it('handles empty strings', () => {
    expect(timingSafeEqual('', '')).toBe(true);
    expect(timingSafeEqual('', 'x')).toBe(false);
  });
});
