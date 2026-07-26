/**
 * CloakBrowser license handling.
 *
 * Flow the user sees:
 *   1. "Sign in with GitHub" → opens https://cloakbrowser.dev/api/license/free/github/start
 *      in the system browser. CloakBrowser emails a free key to the GitHub email.
 *   2. The user pastes that key (or an existing paid key) into the app.
 *   3. We validate it against the license API, then store it at
 *      ~/.cloakbrowser/license.key — the exact location the wrapper *and* the
 *      Pro binary itself read, so a key activated here also works for the
 *      `cloakbrowser` CLI and any script on the machine.
 *
 * Tier semantics (from the upstream project):
 *   - no key      → older free binary
 *   - free key    → latest binary, 1 concurrent session
 *   - paid key    → latest binary, plan-defined concurrent sessions
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { shell } from 'electron';
import type { BinaryState, LicenseState } from '../../shared/types';
import { mask } from './secrets';

const API = 'https://cloakbrowser.dev';
export const GITHUB_FREE_KEY_URL = `${API}/api/license/free/github/start`;
export const PRICING_URL = `${API}/`;
const VALIDATE_URL = `${API}/api/license/validate`;
const SESSION_COUNT_URL = `${API}/api/license/session/count`;

const REQUEST_TIMEOUT_MS = 12_000;

/**
 * The CloakBrowser cache dir. Honours CLOAKBROWSER_CACHE_DIR so a user who
 * already customised it keeps a single source of truth.
 */
export function cloakCacheDir(): string {
  const override = process.env.CLOAKBROWSER_CACHE_DIR?.trim();
  return override || path.join(os.homedir(), '.cloakbrowser');
}

export function licenseKeyFile(): string {
  return path.join(cloakCacheDir(), 'license.key');
}

/** Read the saved key, if any. */
export function readSavedKey(): string | undefined {
  // An env var always wins: the wrapper resolves it first, so reporting
  // anything else here would show the user a key that is not actually in use.
  const envKey = process.env.CLOAKBROWSER_LICENSE_KEY?.trim();
  if (envKey) return envKey;
  try {
    const content = fs.readFileSync(licenseKeyFile(), 'utf-8').trim();
    return content || undefined;
  } catch {
    return undefined;
  }
}

/** Persist a key with 0600 permissions. */
export function saveKey(key: string): void {
  const dir = cloakCacheDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = licenseKeyFile();
  fs.writeFileSync(file, key.trim() + '\n', { mode: 0o600 });
  try {
    fs.chmodSync(file, 0o600);
  } catch {
    /* not supported on Windows */
  }
}

export function clearKey(): void {
  try {
    fs.unlinkSync(licenseKeyFile());
  } catch {
    /* already gone */
  }
}

/** Open the GitHub sign-in page that emails a free key. */
export async function openGithubSignIn(): Promise<void> {
  await shell.openExternal(GITHUB_FREE_KEY_URL);
}

export async function openPricing(): Promise<void> {
  await shell.openExternal(PRICING_URL);
}

interface RawLicense {
  valid?: boolean;
  plan?: string;
  expires?: string | null;
}

/**
 * Validate a key against the license server.
 *
 * Returns `null` when the server could not be reached — deliberately distinct
 * from `{ valid: false }`, because "we cannot tell" must never be shown to the
 * user as "your key is bad".
 */
export async function validateKey(key: string): Promise<{ valid: boolean; plan: string; expires: string | null } | null> {
  try {
    const resp = await fetch(VALIDATE_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ license_key: key }),
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const data = (await resp.json()) as RawLicense;
    return {
      valid: Boolean(data.valid),
      plan: String(data.plan ?? 'solo'),
      expires: data.expires != null ? String(data.expires) : null,
    };
  } catch {
    return null;
  }
}

/**
 * Concurrent sessions the server currently counts against this key.
 * Never cached — a stale seat count is a wrong seat count.
 */
export async function activeSessionCount(key: string): Promise<number | null> {
  try {
    const resp = await fetch(SESSION_COUNT_URL, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ license_key: key }),
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const data = (await resp.json()) as { active?: unknown };
    return typeof data.active === 'number' ? data.active : null;
  } catch {
    return null;
  }
}

/**
 * Full license state for the UI.
 *
 * `localSessions` is supplied by the session manager so the panel can show
 * "2 of 5 sessions" without the license module knowing about sessions.
 */
export async function licenseState(localSessions = 0, opts: { refresh?: boolean } = {}): Promise<LicenseState> {
  const key = readSavedKey();
  if (!key) {
    return { tier: 'none', valid: false, localSessions };
  }

  const base: LicenseState = {
    tier: 'free',
    maskedKey: mask(key, 6),
    valid: false,
    localSessions,
  };

  const info = await validateKey(key);
  if (!info) {
    return {
      ...base,
      error: 'Could not reach the license server. The saved key will still be used offline.',
      checkedAt: Date.now(),
    };
  }

  const tier = info.valid ? (info.plan === 'free' ? 'free' : 'pro') : 'none';
  const state: LicenseState = {
    ...base,
    tier,
    plan: info.plan,
    valid: info.valid,
    expires: info.expires,
    checkedAt: Date.now(),
  };

  if (info.valid && opts.refresh !== false) {
    state.activeSessions = await activeSessionCount(key);
  }
  if (!info.valid) {
    state.error = 'This license key is invalid or expired.';
  }
  return state;
}

/** Concurrent-session allowance per plan, used as a soft guard in the UI. */
export function planSeatHint(plan: string | undefined): number | null {
  switch ((plan ?? '').toLowerCase()) {
    case 'free':
      return 1;
    case 'solo':
      return 5;
    case 'team':
      return 20;
    case 'scale':
      return 200;
    case 'enterprise':
      return null; // unbounded / negotiated
    default:
      return null;
  }
}

// ---------------------------------------------------------------------------
// Binary state
// ---------------------------------------------------------------------------

/**
 * Report which stealth Chromium binary would launch right now.
 *
 * Delegated to the wrapper's own `binaryInfo()` so the answer always matches
 * what an actual launch will use, rather than re-deriving the cache layout here.
 */
export async function binaryState(browserVersion?: string, releaseChannel?: string): Promise<BinaryState> {
  try {
    // NOTE: binaryInfo takes (browserVersion, releaseChannel) and does NOT take
    // a license key — unlike ensureBinary, which does. Passing a key as the
    // first argument makes the wrapper treat it as a version pin and throw
    // "Invalid browser version pin", so every licensed user would see the
    // binary reported as missing forever. It reads the tier off what is
    // actually on disk instead.
    const mod = (await import('cloakbrowser')) as {
      binaryInfo: (
        browserVersion?: string,
        releaseChannel?: string,
      ) => {
        version: string;
        platform: string;
        tier: 'free' | 'pro';
        binaryPath: string;
        installed: boolean;
        cacheDir: string;
      };
    };
    const info = mod.binaryInfo(browserVersion, releaseChannel);
    return {
      installed: info.installed,
      version: info.version,
      platform: info.platform,
      tier: info.tier,
      path: info.binaryPath,
      cacheDir: info.cacheDir,
    };
  } catch (e) {
    return { installed: false, error: (e as Error)?.message ?? String(e) };
  }
}

/**
 * Download the binary that matches the current license/channel.
 * Progress is reported through the wrapper's stdout, so we surface only the
 * result; the UI shows an indeterminate progress state meanwhile.
 */
export async function ensureBinaryDownloaded(
  browserVersion?: string,
  releaseChannel?: string,
): Promise<{ path: string }> {
  const mod = (await import('cloakbrowser')) as {
    ensureBinary: (licenseKey?: string, browserVersion?: string, releaseChannel?: string) => Promise<string>;
  };
  const p = await mod.ensureBinary(readSavedKey(), browserVersion, releaseChannel);
  return { path: p };
}
