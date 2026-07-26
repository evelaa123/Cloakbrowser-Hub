/**
 * Local automation REST API.
 *
 * Lets a script drive the app the way Dolphin Anty's local API does: list
 * profiles, start one, get back a CDP endpoint, attach Puppeteer / Playwright /
 * Selenium, then stop it. Without this, every launch is a manual click, which
 * rules out the whole class of work people actually buy an anti-detect browser
 * for (bulk account tasks, scheduled checks, scraping under a stable identity).
 *
 * Design decisions worth stating:
 *
 * - Bound to 127.0.0.1 only. This endpoint can start browsers and hand out CDP
 *   URLs that allow arbitrary page control and cookie theft, so it must never be
 *   reachable off-box. There is deliberately no setting to change the host.
 *
 * - Bearer token on every request, compared in constant time. A page's own
 *   JavaScript can issue requests to 127.0.0.1, so "it's only local" is not a
 *   security boundary on its own.
 *
 * - Browser preflight (CORS) is refused outright. We never send
 *   Access-Control-Allow-Origin, so a page cannot read our responses; rejecting
 *   OPTIONS makes that explicit rather than relying on the default.
 *
 * - Implemented on node:http with no framework: the surface is eight routes, and
 *   an Express dependency in the main process would be more attack surface and
 *   more bundle for no benefit.
 */

import crypto from 'node:crypto';
import http from 'node:http';
import { URL } from 'node:url';
import type { AutomationEndpoint, AutomationSettings, Profile } from '../../shared/types';

/** What the server needs from the rest of the app, injected for testability. */
export interface AutomationDeps {
  listProfiles: () => Profile[];
  getProfile: (id: string) => Profile | undefined;
  createProfile: (partial?: Partial<Profile>) => Profile;
  updateProfile: (id: string, patch: Partial<Profile>) => Profile | undefined;
  deleteProfile: (id: string, deleteData?: boolean) => boolean;
  startSession: (id: string) => Promise<{ ok: true } | { ok: false; error: string }>;
  stopSession: (id: string) => Promise<void>;
  endpoint: (id: string) => AutomationEndpoint | undefined;
  isRunning: (id: string) => boolean;
  log?: (message: string) => void;
}

interface Ctx {
  req: http.IncomingMessage;
  res: http.ServerResponse;
  url: URL;
  body: unknown;
}

const MAX_BODY_BYTES = 256 * 1024;

export class AutomationServer {
  private server?: http.Server;
  private settings?: AutomationSettings;

  constructor(private deps: AutomationDeps) {}

  get running(): boolean {
    return !!this.server?.listening;
  }

  get port(): number | undefined {
    const addr = this.server?.address();
    return typeof addr === 'object' && addr ? addr.port : undefined;
  }

  /** Idempotent: restarts cleanly if the port or token changed. */
  async start(settings: AutomationSettings): Promise<void> {
    await this.stop();
    if (!settings.enabled) return;

    if (!settings.token) {
      throw new Error('Refusing to start the automation API without a token.');
    }

    this.settings = settings;
    const server = http.createServer((req, res) => {
      void this.route(req, res);
    });

    await new Promise<void>((resolve, reject) => {
      server.once('error', (e: NodeJS.ErrnoException) => {
        reject(
          e.code === 'EADDRINUSE'
            ? new Error(`Port ${settings.port} is already in use. Pick another in Settings.`)
            : e,
        );
      });
      // Loopback only - not configurable by design.
      server.listen(settings.port, '127.0.0.1', () => resolve());
    });

    this.server = server;
    this.deps.log?.(`Automation API listening on http://127.0.0.1:${settings.port}`);
  }

  async stop(): Promise<void> {
    const server = this.server;
    this.server = undefined;
    if (!server) return;
    await new Promise<void>((resolve) => server.close(() => resolve()));
  }

  // -------------------------------------------------------------------------
  // Routing
  // -------------------------------------------------------------------------

  private async route(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    try {
      // Never advertise CORS. A cross-origin page may still *send* a simple
      // request, but the missing token stops it, and the missing ACAO header
      // stops it reading any reply.
      if (req.method === 'OPTIONS') {
        this.send(res, 405, { error: 'Cross-origin requests are not supported.' });
        return;
      }

      const url = new URL(req.url ?? '/', `http://127.0.0.1`);

      // /health is unauthenticated on purpose: it reports nothing but liveness
      // and a version, so a script can wait for the port without holding the
      // token yet.
      if (url.pathname === '/health' && req.method === 'GET') {
        this.send(res, 200, { ok: true, api: 'cloakbrowser-hub', version: 1 });
        return;
      }

      if (!this.authorized(req)) {
        // No detail about why: distinguishing "missing" from "wrong" only helps
        // someone probing the port.
        this.send(res, 401, { error: 'Unauthorized.' });
        return;
      }

      const body = await this.readBody(req);
      const ctx: Ctx = { req, res, url, body };
      await this.dispatch(ctx);
    } catch (e) {
      const msg = (e as Error)?.message ?? String(e);
      this.send(res, 400, { error: msg.split('\n')[0] });
    }
  }

  private async dispatch(ctx: Ctx): Promise<void> {
    const { req, res, url } = ctx;
    const path = url.pathname.replace(/\/+$/, '') || '/';
    const method = req.method ?? 'GET';

    // GET /profiles
    if (path === '/profiles' && method === 'GET') {
      const profiles = this.deps.listProfiles().map((p) => ({
        id: p.id,
        name: p.name,
        platform: p.fingerprint?.platform,
        running: this.deps.isRunning(p.id),
      }));
      this.send(res, 200, { profiles });
      return;
    }

    // POST /profiles
    if (path === '/profiles' && method === 'POST') {
      const patch = (ctx.body ?? {}) as Partial<Profile>;
      const created = this.deps.createProfile(patch);
      this.send(res, 201, { profile: created });
      return;
    }

    const profileMatch = /^\/profiles\/([^/]+)(\/[a-z]+)?$/.exec(path);
    if (profileMatch) {
      const id = decodeURIComponent(profileMatch[1]!);
      const action = profileMatch[2];

      const profile = this.deps.getProfile(id);
      if (!profile) {
        this.send(res, 404, { error: `No profile with id "${id}".` });
        return;
      }

      // GET /profiles/:id
      if (!action && method === 'GET') {
        this.send(res, 200, { profile });
        return;
      }

      // PATCH /profiles/:id - the fingerprint editor, scriptable.
      if (!action && method === 'PATCH') {
        const updated = this.deps.updateProfile(id, (ctx.body ?? {}) as Partial<Profile>);
        this.send(res, 200, { profile: updated });
        return;
      }

      // DELETE /profiles/:id
      if (!action && method === 'DELETE') {
        if (this.deps.isRunning(id)) {
          this.send(res, 409, { error: 'Stop the session before deleting the profile.' });
          return;
        }
        const deleteData = url.searchParams.get('keepData') !== 'true';
        this.send(res, 200, { deleted: this.deps.deleteProfile(id, deleteData) });
        return;
      }

      // POST /profiles/:id/start -> returns the CDP endpoint
      if (action === '/start' && method === 'POST') {
        if (this.deps.isRunning(id)) {
          // Idempotent: a retry after a timeout should not be an error, so
          // return the existing endpoint instead.
          const existing = this.deps.endpoint(id);
          if (existing) {
            this.send(res, 200, { ...existing, alreadyRunning: true });
            return;
          }
          this.send(res, 409, { error: 'Profile is already running.' });
          return;
        }

        const started = await this.deps.startSession(id);
        if (!started.ok) {
          this.send(res, 500, { error: started.error });
          return;
        }

        const endpoint = this.deps.endpoint(id);
        if (!endpoint) {
          this.send(res, 500, {
            error:
              'Session started but no CDP endpoint is available. Restart the session with the automation API enabled.',
          });
          return;
        }
        this.send(res, 200, endpoint);
        return;
      }

      // POST /profiles/:id/stop
      if (action === '/stop' && method === 'POST') {
        await this.deps.stopSession(id);
        this.send(res, 200, { stopped: true });
        return;
      }

      // GET /profiles/:id/endpoint - re-attach to an already running session.
      if (action === '/endpoint' && method === 'GET') {
        const endpoint = this.deps.endpoint(id);
        if (!endpoint) {
          this.send(res, 404, { error: 'That profile has no automation endpoint (not running?).' });
          return;
        }
        this.send(res, 200, endpoint);
        return;
      }
    }

    this.send(res, 404, { error: `No route for ${method} ${path}` });
  }

  // -------------------------------------------------------------------------
  // Helpers
  // -------------------------------------------------------------------------

  private authorized(req: http.IncomingMessage): boolean {
    const expected = this.settings?.token;
    if (!expected) return false;

    const header = req.headers.authorization ?? '';
    const presented = header.startsWith('Bearer ')
      ? header.slice(7)
      : ((req.headers['x-api-token'] as string | undefined) ?? '');

    return timingSafeEqual(presented, expected);
  }

  private readBody(req: http.IncomingMessage): Promise<unknown> {
    if (req.method === 'GET' || req.method === 'DELETE') return Promise.resolve(undefined);

    return new Promise((resolve, reject) => {
      const chunks: Buffer[] = [];
      let size = 0;
      req.on('data', (c: Buffer) => {
        size += c.length;
        // Cap the body so a stray large POST cannot exhaust main-process memory.
        if (size > MAX_BODY_BYTES) {
          reject(new Error('Request body too large.'));
          req.destroy();
          return;
        }
        chunks.push(c);
      });
      req.on('end', () => {
        if (!chunks.length) return resolve(undefined);
        const text = Buffer.concat(chunks).toString('utf-8').trim();
        if (!text) return resolve(undefined);
        try {
          resolve(JSON.parse(text));
        } catch {
          reject(new Error('Body must be valid JSON.'));
        }
      });
      req.on('error', reject);
    });
  }

  private send(res: http.ServerResponse, status: number, payload: unknown): void {
    const body = JSON.stringify(payload);
    res.writeHead(status, {
      'content-type': 'application/json; charset=utf-8',
      'content-length': Buffer.byteLength(body),
      // Defence in depth: this API returns JSON only, never a document.
      'x-content-type-options': 'nosniff',
    });
    res.end(body);
  }
}

/**
 * Constant-time string compare.
 *
 * `crypto.timingSafeEqual` throws on length mismatch, which itself leaks the
 * expected length, so both sides are hashed to a fixed width first.
 */
export function timingSafeEqual(a: string, b: string): boolean {
  const ha = crypto.createHash('sha256').update(a).digest();
  const hb = crypto.createHash('sha256').update(b).digest();
  return crypto.timingSafeEqual(ha, hb);
}
