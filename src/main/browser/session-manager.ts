/**
 * Session manager: starts and stops stealth browser sessions.
 *
 * One session == one `launchPersistentContext()` against the profile's own
 * user-data dir. A persistent context (rather than a fresh incognito context)
 * is deliberate: it keeps cookies, localStorage, IndexedDB, service workers and
 * cached fonts across runs, which is both what account work needs and what
 * defeats the "empty ephemeral profile" heuristics.
 *
 * Cookie handling around a session:
 *   start → inject the encrypted jar into the context (repaired for Chromium)
 *   stop  → read the context's cookies back and re-encrypt the jar
 *
 * Injecting on every start is intentional even though the profile dir already
 * has a Cookies DB: an imported jar must reach a profile that has never run,
 * and re-injecting is harmless (same values, same domains).
 */

import { EventEmitter } from 'node:events';
import fs from 'node:fs';
import type { BrowserContext } from 'playwright-core';
import type {
  AutomationEndpoint,
  Profile,
  ProfileStatus,
  SessionInfo,
  SessionLogEntry,
} from '../../shared/types';
import { resolveLaunch } from '../../shared/fingerprint-args';
import { loadCookiesInto, saveCookiesFrom } from '../services/cookies';
import { paths } from '../services/paths';
import { knownPlanSeats, readSavedKey } from '../services/license';
import { resolveSessionLimit } from '../../shared/session-limit';
import { noSandboxOverride, resolveSandboxArgs } from './sandbox-args';
import { needsSearchEngineSeed, seedGoogleSearch, seedingArgs } from './search-engine';
import { Profiles, Settings, profileDataDir } from '../services/repos';

/** Options the CloakBrowser wrapper accepts for a persistent context. */
interface PersistentContextOptions {
  userDataDir: string;
  headless: boolean;
  args: string[];
  /**
   * Include the wrapper's own default flag set. We pass `false` and supply the
   * equivalents ourselves — see the call site for why that is what removes the
   * hardcoded `--no-sandbox`.
   */
  stealthArgs?: boolean;
  proxy?: string | { server: string; bypass?: string; username?: string; password?: string };
  timezone?: string;
  locale?: string;
  geoip?: boolean;
  licenseKey?: string;
  browserVersion?: string;
  releaseChannel?: 'stable' | 'preview';
  extensionPaths?: string[];
  userAgent?: string;
  humanize?: boolean;
  humanPreset?: 'default' | 'careful';
  humanConfig?: Record<string, unknown>;
  viewport?: { width: number; height: number } | null;
}

interface CloakBrowserModule {
  launchPersistentContext: (options: PersistentContextOptions) => Promise<BrowserContext>;
}

interface LiveSession {
  profileId: string;
  profileName: string;
  context: BrowserContext;
  startedAt: number;
  /** Set while stop() is in flight so a double-click can't run teardown twice. */
  closing: boolean;
  /** DevTools port this session bound, when automation is enabled. */
  cdpPort?: number;
  /** Resolved CDP WebSocket URL, read from /json/version once at start. */
  wsEndpoint?: string;
}

const MAX_LOG_LINES = 500;

/**
 * Ask the OS for an unused TCP port.
 *
 * Binding port 0 and reading back what the kernel assigned is the only
 * race-free way to do this: picking a number and hoping it is free would
 * collide as soon as two profiles start at once, and Chromium would then fail
 * with a confusing "address in use". Bound to 127.0.0.1 so the probe itself is
 * never externally reachable.
 */
async function freePort(): Promise<number> {
  const net = await import('node:net');
  return new Promise<number>((resolve, reject) => {
    const srv = net.createServer();
    srv.once('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address();
      const port = typeof addr === 'object' && addr ? addr.port : 0;
      srv.close(() => (port ? resolve(port) : reject(new Error('Could not allocate a port'))));
    });
  });
}

/**
 * Read the CDP WebSocket URL from Chromium's own /json/version.
 *
 * The URL contains a per-launch UUID that cannot be derived from the port, so
 * it has to be fetched. Chromium writes the endpoint slightly after the process
 * is up, so this retries briefly rather than failing on the first refusal.
 */
async function resolveWsEndpoint(port: number, timeoutMs = 10_000): Promise<string | undefined> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/json/version`);
      if (res.ok) {
        const body = (await res.json()) as { webSocketDebuggerUrl?: string };
        if (body.webSocketDebuggerUrl) return body.webSocketDebuggerUrl;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((r) => setTimeout(r, 200));
  }
  return undefined;
}

export interface SessionManagerEvents {
  sessions: (sessions: SessionInfo[]) => void;
  log: (entry: SessionLogEntry) => void;
}

export class SessionManager extends EventEmitter {
  private sessions = new Map<string, LiveSession>();
  /** Profiles in a transitional state, so the UI can disable the button. */
  private pending = new Map<string, ProfileStatus>();
  private logs = new Map<string, SessionLogEntry[]>();
  private lastError = new Map<string, string>();

  // -------------------------------------------------------------------------
  // Queries
  // -------------------------------------------------------------------------

  status(profileId: string): ProfileStatus {
    if (this.pending.has(profileId)) return this.pending.get(profileId)!;
    if (this.sessions.has(profileId)) return 'running';
    return this.lastError.has(profileId) ? 'error' : 'idle';
  }

  statusMessage(profileId: string): string | undefined {
    return this.lastError.get(profileId);
  }

  isRunning(profileId: string): boolean {
    return this.sessions.has(profileId) || this.pending.has(profileId);
  }

  runningCount(): number {
    return this.sessions.size + this.pending.size;
  }

  list(): SessionInfo[] {
    const out: SessionInfo[] = [];
    for (const s of this.sessions.values()) {
      out.push({
        profileId: s.profileId,
        profileName: s.profileName,
        status: 'running',
        startedAt: s.startedAt,
        pages: s.context.pages().length,
      });
    }
    for (const [profileId, status] of this.pending) {
      const profile = Profiles.get(profileId);
      out.push({ profileId, profileName: profile?.name ?? profileId, status, pages: 0 });
    }
    return out;
  }

  /**
   * Automation endpoint for a running session, or undefined when the session is
   * not running or was started while the automation API was off (the DevTools
   * port cannot be added to a live Chromium after the fact).
   */
  endpoint(profileId: string): AutomationEndpoint | undefined {
    const s = this.sessions.get(profileId);
    if (!s?.cdpPort || !s.wsEndpoint) return undefined;
    return {
      profileId: s.profileId,
      profileName: s.profileName,
      wsEndpoint: s.wsEndpoint,
      httpEndpoint: `http://127.0.0.1:${s.cdpPort}`,
      port: s.cdpPort,
    };
  }

  logsFor(profileId: string): SessionLogEntry[] {
    return this.logs.get(profileId) ?? [];
  }

  clearLogs(profileId: string): void {
    this.logs.delete(profileId);
  }

  // -------------------------------------------------------------------------
  // Start
  // -------------------------------------------------------------------------

  async start(profileId: string): Promise<{ ok: true } | { ok: false; error: string }> {
    const profile = Profiles.get(profileId);
    if (!profile) return { ok: false, error: 'Profile not found.' };
    if (this.isRunning(profileId)) return { ok: false, error: 'This profile is already running.' };

    const settings = Settings.get();
    // The limit is the lower of the plan's seats and the user's own preference,
    // and the error has to say which one bound — "raise it in Settings" is
    // actively misleading advice when the plan is what's capping you.
    const { limit, cappedByPlan, reason } = resolveSessionLimit(
      settings.maxConcurrentSessions,
      knownPlanSeats(),
    );
    if (limit > 0 && this.runningCount() >= limit) {
      return {
        ok: false,
        error: cappedByPlan
          ? `Concurrent session limit reached (${limit}) — that is ${reason}. Close a session, or upgrade your plan for more.`
          : `Concurrent session limit reached (${limit}) — that is ${reason}. Close a session, or raise the limit in Settings.`,
      };
    }

    this.lastError.delete(profileId);
    this.setPending(profileId, 'starting');
    this.log(profileId, 'info', `Starting "${profile.name}"…`);

    try {
      // Only open a DevTools port when the automation API is actually on. An
      // always-on debugging port would let any local process drive every
      // session, which is a real escalation path and not something a manual
      // user has asked for.
      const cdpPort = settings.automation?.enabled ? await freePort() : undefined;

      const context = await this.launch(profile, cdpPort);
      this.pending.delete(profileId);

      let wsEndpoint: string | undefined;
      if (cdpPort) {
        wsEndpoint = await resolveWsEndpoint(cdpPort);
        if (wsEndpoint) {
          this.log(profileId, 'info', `Automation endpoint ready on port ${cdpPort}.`);
        } else {
          // Non-fatal: the session itself is fine, only scripted control is
          // unavailable, so the user is told rather than the launch failed.
          this.log(
            profileId,
            'warn',
            `Started, but the automation endpoint on port ${cdpPort} did not come up. Scripted control is unavailable for this session.`,
          );
        }
      }

      this.sessions.set(profileId, {
        profileId,
        profileName: profile.name,
        context,
        startedAt: Date.now(),
        closing: false,
        cdpPort,
        wsEndpoint,
      });
      Profiles.markRun(profileId);
      this.emitSessions();
      this.log(profileId, 'info', 'Session ready.');
      return { ok: true };
    } catch (e) {
      this.pending.delete(profileId);
      const error = describeLaunchError(e);
      this.lastError.set(profileId, error);
      this.log(profileId, 'error', error);
      this.emitSessions();
      return { ok: false, error };
    }
  }

  private async launch(profile: Profile, cdpPort?: number): Promise<BrowserContext> {
    const settings = Settings.get();
    const resolved = resolveLaunch(profile);
    const userDataDir = profileDataDir(profile.id);

    if (resolved.proxy) {
      const server = typeof resolved.proxy === 'string' ? resolved.proxy : resolved.proxy.server;
      this.log(profile.id, 'info', `Proxy: ${server}${resolved.geoip ? ' (timezone/locale from exit IP)' : ''}`);
    } else {
      this.log(profile.id, 'warn', 'No proxy set — the session uses this machine’s IP address.');
    }

    // Only pass extension dirs that actually exist: Chromium refuses to start
    // when --load-extension points at a missing path, which would look like a
    // mysterious launch failure.
    const extensionPaths = resolved.extensionPaths.filter((p) => {
      const ok = p && fs.existsSync(p);
      if (!ok) this.log(profile.id, 'warn', `Skipping missing extension path: ${p}`);
      return ok;
    });

    // Sandbox: keep it on where the kernel supports it, and suppress the
    // "unsupported command-line flag" infobar where it has to be off. The bar is
    // not only ugly — it shifts innerHeight by ~40px, which breaks the viewport
    // coherence the rest of the fingerprint work is trying to maintain.
    const sandbox = resolveSandboxArgs(process.platform, { forceNoSandbox: noSandboxOverride() });
    this.log(profile.id, sandbox.disabled ? 'warn' : 'info', sandbox.reason);

    // Bind the DevTools port to loopback explicitly. Chromium's default for
    // --remote-debugging-port is all interfaces on some builds, which would
    // expose every session to the local network.
    // First launch of a profile also configures the search engine, which means
    // driving chrome://settings — a localised UI. Pinning en-US makes that DOM
    // deterministic, but only when the profile has not pinned its own locale:
    // overriding a deliberate --lang would change the Accept-Language header the
    // site sees, and a fingerprint change is a worse problem than an unset
    // search engine.
    const seedArgs = needsSearchEngineSeed(profile)
      ? seedingArgs(profile.locale?.mode === 'manual' && !!profile.locale.locale)
      : [];

    const args = [
      ...resolved.args,
      ...sandbox.args,
      ...seedArgs,
      ...(cdpPort
        ? [`--remote-debugging-port=${cdpPort}`, '--remote-debugging-address=127.0.0.1']
        : []),
    ];

    // Logged after the CDP flags are appended so the line matches what actually
    // launched — the log is the first thing anyone checks when debugging a
    // fingerprint, and a line that omits real flags would mislead.
    this.log(profile.id, 'info', `Chromium flags: ${args.join(' ')}`);

    const options: PersistentContextOptions = {
      userDataDir,
      headless: resolved.headless,
      args,
      // Take over the wrapper's default flag set.
      //
      // This is what removes the "You are using an unsupported command-line
      // flag: --no-sandbox" infobar. The flag is hardcoded in the wrapper's
      // `getDefaultStealthArgs()`, and `buildArgs` deduplicates by flag *key* —
      // so passing our own args can override a value but can never delete a
      // default. `stealthArgs: false` is the only lever that removes it.
      //
      // Safe only because `buildFingerprintArgs` already emits the other two
      // defaults (`--fingerprint=<seed>` and `--fingerprint-platform=<os>`)
      // from the profile itself, with a *stable* seed rather than the random
      // one the wrapper rolls per launch. See `sandboxArgs()` for why dropping
      // --no-sandbox is a security improvement and not just cosmetic.
      stealthArgs: false,
      geoip: resolved.geoip,
      humanize: resolved.humanize,
      humanPreset: resolved.humanPreset,
      // Headed sessions must not get an emulated viewport: a window where
      // innerWidth > outerWidth is impossible on a real machine.
      viewport: resolved.headless ? undefined : null,
    };
    if (resolved.proxy) options.proxy = resolved.proxy;
    if (resolved.timezone) options.timezone = resolved.timezone;
    if (resolved.locale) options.locale = resolved.locale;
    if (resolved.userAgent) options.userAgent = resolved.userAgent;
    if (resolved.humanConfig) options.humanConfig = resolved.humanConfig;
    if (extensionPaths.length) options.extensionPaths = extensionPaths;

    const licenseKey = readSavedKey();
    if (licenseKey) options.licenseKey = licenseKey;
    if (settings.browserVersion) options.browserVersion = settings.browserVersion;
    if (settings.releaseChannel) options.releaseChannel = settings.releaseChannel;

    const context = await this.launchWithGeoipFallback(profile, options);

    // Inject the saved cookie jar before any navigation happens.
    const jar = paths.cookieJar(profile.id);
    const injected = await loadCookiesInto(context, jar, (m) => this.log(profile.id, 'info', m));
    if (injected.missing.length) {
      this.log(
        profile.id,
        'warn',
        `Some session cookies did not survive import (${injected.missing.join(', ')}). The site may ask you to log in again.`,
      );
    }

    if (profile.geo.mode === 'manual' && profile.geo.latitude != null && profile.geo.longitude != null) {
      // The binary flag covers the fingerprint layer; granting the permission
      // stops the browser prompting for it during automated flows.
      try {
        await context.grantPermissions(['geolocation']);
      } catch {
        /* permission model varies per platform; non-fatal */
      }
    }

    // The user closing the last window is a normal way to end a session, so
    // treat it exactly like pressing Stop (including saving cookies).
    context.on('close', () => {
      void this.handleUnexpectedClose(profile.id);
    });

    await this.seedSearchEngineOnce(context, profile);

    await this.openStartPages(context, profile);
    return context;
  }

  /**
   * Give a new profile a working address bar, once.
   *
   * The binary is de-Googled and ships no prepopulated search engine, so a fresh
   * profile cannot search from the omnibox at all. The only supported fix is to
   * drive the settings UI in a persistent profile — see `search-engine.ts` for
   * why editing Preferences or Web Data does not work.
   *
   * Non-fatal by construction: a failure leaves the user with no default engine,
   * which is an annoyance. Failing the launch over it would be worse, so the
   * outcome is logged either way and the flag is only set on success — a failed
   * attempt should be retried on the next start, not silently given up on.
   */
  private async seedSearchEngineOnce(context: BrowserContext, profile: Profile): Promise<void> {
    if (!needsSearchEngineSeed(profile)) return;

    const result = await seedGoogleSearch(context);
    this.log(profile.id, result.ok ? 'info' : 'warn', result.message);
    if (result.ok) Profiles.update(profile.id, { searchEngineSeeded: true });
  }

  /**
   * Launch, degrading gracefully when GeoIP cannot run.
   *
   * `geoip: true` makes the wrapper import `mmdb-lib`, which is an *optional*
   * peer dependency: when it is absent the wrapper throws
   * "mmdb-lib is required for geoip: true" and the whole launch fails. The
   * GeoIP database is also a ~70 MB download on first use, so it can fail on a
   * slow or filtered connection.
   *
   * Neither is a reason to refuse to open the browser. Timezone matching is a
   * quality-of-fingerprint concern; not launching at all is strictly worse than
   * launching with the machine's timezone and telling the user. So the failure
   * is retried once with geoip off, and the reason is logged loudly rather than
   * swallowed — a silently unmatched timezone is exactly the kind of drift that
   * gets an account flagged.
   */
  private async launchWithGeoipFallback(
    profile: Profile,
    options: PersistentContextOptions,
  ): Promise<BrowserContext> {
    const cloak = (await import('cloakbrowser')) as unknown as CloakBrowserModule;
    try {
      return await cloak.launchPersistentContext(options);
    } catch (e) {
      const msg = String((e as Error)?.message ?? e);
      const geoipFailed = options.geoip === true && /mmdb-lib|geoip/i.test(msg);
      if (!geoipFailed) throw e;

      this.log(
        profile.id,
        'warn',
        `Could not resolve timezone/locale from the exit IP (${msg.split('\n')[0]}). ` +
          'Starting with this machine’s timezone instead — set the timezone manually in the profile ' +
          'if the exit IP is in another country.',
      );
      return cloak.launchPersistentContext({ ...options, geoip: false });
    }
  }

  private async openStartPages(context: BrowserContext, profile: Profile): Promise<void> {
    const urls = (profile.startup.startUrls ?? []).map((u) => u.trim()).filter(Boolean);
    const first = context.pages()[0] ?? (await context.newPage());

    if (!urls.length) return;

    for (let i = 0; i < urls.length; i++) {
      const url = normaliseUrl(urls[i]!);
      const page = i === 0 ? first : await context.newPage();
      try {
        // Not waiting for full load: a slow third-party asset must not make the
        // session look like it failed to start.
        await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45_000 });
      } catch (e) {
        this.log(profile.id, 'warn', `Could not open ${url}: ${firstLine(e)}`);
      }
    }
  }

  // -------------------------------------------------------------------------
  // Stop
  // -------------------------------------------------------------------------

  async stop(profileId: string): Promise<{ ok: true } | { ok: false; error: string }> {
    const session = this.sessions.get(profileId);
    if (!session) return { ok: false, error: 'This profile is not running.' };
    if (session.closing) return { ok: true };

    session.closing = true;
    this.setPending(profileId, 'stopping');
    try {
      await this.persistCookies(session);
      await session.context.close();
      return { ok: true };
    } catch (e) {
      const error = firstLine(e);
      this.log(profileId, 'error', `Error while closing: ${error}`);
      return { ok: false, error };
    } finally {
      this.sessions.delete(profileId);
      this.pending.delete(profileId);
      this.emitSessions();
      this.log(profileId, 'info', 'Session closed.');
    }
  }

  /** Save cookies, then forget the session (window closed by the user). */
  private async handleUnexpectedClose(profileId: string): Promise<void> {
    const session = this.sessions.get(profileId);
    if (!session || session.closing) return;
    session.closing = true;
    // The context is already gone, so cookies must be read before we drop the
    // reference — Playwright still answers ctx.cookies() during teardown.
    await this.persistCookies(session);
    this.sessions.delete(profileId);
    this.pending.delete(profileId);
    this.emitSessions();
    this.log(profileId, 'info', 'Browser window closed.');
  }

  private async persistCookies(session: LiveSession): Promise<void> {
    if (!Settings.get().saveCookiesOnClose) return;
    try {
      const jar = paths.cookieJar(session.profileId);
      const count = await saveCookiesFrom(session.context, jar, (m) =>
        this.log(session.profileId, 'info', m),
      );
      if (count > 0) {
        const cookies = { count, domains: 0, updatedAt: Date.now(), source: 'session' as const };
        Profiles.update(session.profileId, { cookies });
      }
    } catch (e) {
      this.log(session.profileId, 'warn', `Could not save cookies: ${firstLine(e)}`);
    }
  }

  /** Close every session — used on app quit. */
  async stopAll(): Promise<void> {
    await Promise.allSettled([...this.sessions.keys()].map((id) => this.stop(id)));
  }

  // -------------------------------------------------------------------------
  // Internals
  // -------------------------------------------------------------------------

  private setPending(profileId: string, status: ProfileStatus): void {
    this.pending.set(profileId, status);
    this.emitSessions();
  }

  private emitSessions(): void {
    this.emit('sessions', this.list());
  }

  log(profileId: string, level: SessionLogEntry['level'], message: string): void {
    const entry: SessionLogEntry = { profileId, at: Date.now(), level, message };
    const list = this.logs.get(profileId) ?? [];
    list.push(entry);
    // Keep memory bounded: a chatty session could otherwise grow without limit.
    if (list.length > MAX_LOG_LINES) list.splice(0, list.length - MAX_LOG_LINES);
    this.logs.set(profileId, list);
    this.emit('log', entry);
  }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function normaliseUrl(input: string): string {
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(input) || input.startsWith('about:')) return input;
  return `https://${input}`;
}

function firstLine(e: unknown): string {
  return String((e as Error)?.message ?? e).split('\n')[0] ?? 'Unknown error';
}

/**
 * Turn a launch failure into something actionable. The wrapper already maps the
 * Pro binary's license exit codes to readable text, so those pass through; the
 * rest are the mistakes users actually make.
 */
function describeLaunchError(e: unknown): string {
  const msg = String((e as Error)?.message ?? e);
  const first = msg.split('\n')[0] ?? msg;

  if ((e as Error)?.name === 'CloakBrowserLicenseError') return first;
  if (/session limit/i.test(msg)) {
    return 'Your license has no free concurrent session left. Close another session or upgrade the plan.';
  }
  if (/license key is invalid|expired|missing/i.test(msg)) {
    return 'The stealth binary refused the license key. Re-activate it in the License tab.';
  }
  if (/ProcessSingleton|SingletonLock|profile.*in use/i.test(msg)) {
    return 'That profile folder is already open in another browser process. Close it and try again.';
  }
  if (/ENOENT.*chrome|executable doesn't exist|no such file/i.test(msg)) {
    return 'The stealth Chromium binary is missing. Open the License tab and download it.';
  }
  if (/error while loading shared libraries|libnss3|libatk/i.test(msg)) {
    return `Chromium is missing a system library: ${first}. On Debian/Ubuntu install libnss3, libatk-bridge2.0-0, libgtk-3-0 and libasound2.`;
  }
  if (/ERR_PROXY|proxy/i.test(msg) && /connect|auth/i.test(msg)) {
    return `The proxy rejected the connection: ${first}`;
  }
  if (/Timeout.*exceeded|timed out/i.test(msg)) {
    return 'The browser took too long to start. Check the proxy, then try again.';
  }
  return first;
}

export const Sessions = new SessionManager();
