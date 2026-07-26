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

/**
 * Decode a license-key file into text.
 *
 * Read as bytes and sniffed rather than assumed UTF-8, because the file is very
 * often *not* written by us. `Set-Content` in Windows PowerShell 5.1 defaults to
 * UTF-16LE, so `... > license.key` produces a UTF-16 file with a BOM. Decoding
 * that as UTF-8 yields a key with a U+FFFD replacement char followed by every
 * character separated by NULs — which still passes an `if (key)` check and gets
 * sent to the server, where it is rejected as invalid. The user sees "invalid or
 * expired" for a key that is perfectly good, with no hint that an encoding is to
 * blame. Sniffing the BOM is the only way to tell these apart.
 */
function decodeKeyFile(buf: Buffer): string {
  if (buf.length >= 2) {
    // UTF-16LE / UTF-16BE BOM. Node cannot decode BE directly, so the bytes are
    // swapped into LE first.
    if (buf[0] === 0xff && buf[1] === 0xfe) return buf.subarray(2).toString('utf16le');
    if (buf[0] === 0xfe && buf[1] === 0xff) return buf.subarray(2).swap16().toString('utf16le');
  }
  // UTF-8 BOM: harmless-looking, but the U+FEFF survives .trim() and would be
  // sent as part of the key.
  if (buf.length >= 3 && buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) {
    return buf.subarray(3).toString('utf-8');
  }
  // No BOM, but NUL bytes at odd offsets mean UTF-16LE written without one —
  // `printf '%s' key | iconv -t UTF-16LE` and some editors do exactly this.
  if (buf.length >= 4 && buf[1] === 0x00 && buf[3] === 0x00) {
    return buf.toString('utf16le');
  }
  return buf.toString('utf-8');
}

/**
 * Normalise a pasted or file-read key.
 *
 * Exported so activation and file reads cannot disagree about what counts as the
 * same key: a value that works when pasted must also work after being saved and
 * read back.
 *
 * Handles what people actually have in these files:
 *  - a trailing newline (ours) or CRLF (any Windows editor);
 *  - surrounding quotes, from `echo "KEY" > license.key`;
 *  - `CLOAKBROWSER_LICENSE_KEY=KEY`, from pasting an env-var line;
 *  - extra lines, e.g. a comment above the key — the first non-empty line wins;
 *  - stray NULs and the U+FEFF BOM left by an encoding conversion.
 */
export function normaliseKey(raw: string): string {
  let text = raw.replace(/\u0000/g, '').replace(/\uFEFF/g, '');
  const line = text
    .split(/\r?\n/)
    .map((l) => l.trim())
    .find((l) => l && !l.startsWith('#'));
  if (!line) return '';

  text = line;
  const eq = text.match(/^[A-Z_]*LICENSE[A-Z_]*\s*=\s*(.+)$/i);
  if (eq?.[1]) text = eq[1].trim();
  // Strip matching quotes only — an unpaired quote is more likely part of a
  // mistyped key than a quoting artefact, and silently removing it would send a
  // different key than the user believes they entered.
  if (text.length >= 2 && /^(".*"|'.*')$/s.test(text)) text = text.slice(1, -1).trim();
  return text;
}

/** Read the saved key, if any. */
export function readSavedKey(): string | undefined {
  // An env var always wins: the wrapper resolves it first, so reporting
  // anything else here would show the user a key that is not actually in use.
  const envKey = normaliseKey(process.env.CLOAKBROWSER_LICENSE_KEY ?? '');
  if (envKey) return envKey;
  try {
    const content = normaliseKey(decodeKeyFile(fs.readFileSync(licenseKeyFile())));
    return content || undefined;
  } catch {
    return undefined;
  }
}

/** Persist a key with 0600 permissions. Always plain UTF-8 with a trailing LF. */
export function saveKey(key: string): void {
  const dir = cloakCacheDir();
  fs.mkdirSync(dir, { recursive: true });
  const file = licenseKeyFile();
  fs.writeFileSync(file, normaliseKey(key) + '\n', { encoding: 'utf-8', mode: 0o600 });
  try {
    fs.chmodSync(file, 0o600);
  } catch {
    /* not supported on Windows */
  }
}

/**
 * Rewrite the key file as plain UTF-8 when it is stored in some other encoding.
 *
 * Necessary because this file is a *shared contract*, not our private state: the
 * `cloakbrowser` CLI and the Pro binary both read it with a plain
 * `readFileSync(file, 'utf-8')`. So a UTF-16 file does not just break the Hub —
 * it breaks every script on the machine, and fixing it only in our own memory
 * would leave the user with a Hub that works and a CLI that mysteriously does
 * not. Repairing the bytes fixes both at once.
 *
 * Returns true when the file was actually rewritten, so callers can say so
 * instead of silently changing a file the user did not ask us to touch.
 */
export function repairKeyFileEncoding(): boolean {
  const file = licenseKeyFile();
  let buf: Buffer;
  try {
    buf = fs.readFileSync(file);
  } catch {
    return false; // no file is not a problem to fix
  }

  const key = normaliseKey(decodeKeyFile(buf));
  if (!key) return false;

  const wanted = Buffer.from(key + '\n', 'utf-8');
  if (buf.equals(wanted)) return false;

  try {
    fs.writeFileSync(file, wanted, { mode: 0o600 });
    return true;
  } catch {
    // Read-only file or a permissions problem: the in-memory key still works,
    // so this must not be fatal.
    return false;
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
  // Repair before reading: a UTF-16 file would otherwise be validated as
  // mojibake and reported as an invalid key.
  const keyFileRepaired = repairKeyFileEncoding();
  const key = readSavedKey();
  if (!key) {
    return { tier: 'none', valid: false, localSessions, keyFileRepaired };
  }

  const base: LicenseState = {
    tier: 'free',
    maskedKey: mask(key, 6),
    valid: false,
    localSessions,
    keyFileRepaired,
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
    seats: info.valid ? planSeatHint(info.plan) : undefined,
  };

  // Remember the seat count so launches can enforce it without a network call.
  // Only on a valid key: an invalid or expired key should not lower a limit that
  // a still-running session was started under.
  if (info.valid) cachedPlanSeats = planSeatHint(info.plan);

  if (info.valid && opts.refresh !== false) {
    state.activeSessions = await activeSessionCount(key);
  }
  if (!info.valid) {
    // Mention the repair here specifically: this is the exact case where the
    // user was staring at "invalid key" for a key that was in fact fine.
    state.error = keyFileRepaired
      ? 'This license key was stored in the wrong text encoding (UTF-16). The file has been rewritten as UTF-8 — press Re-check.'
      : 'This license key is invalid or expired.';
  }
  return state;
}

// ---------------------------------------------------------------------------
// Cached plan seats
// ---------------------------------------------------------------------------

/**
 * Last known seat count, remembered in memory.
 *
 * `start()` needs the plan's seat limit, but it must not make a network call to
 * get it: a slow or unreachable license server would then add seconds to every
 * launch, and a failed call would have to either block the launch (punishing a
 * paying user for a network blip) or be ignored (making the limit meaningless).
 * Caching the answer from the last successful validation avoids that choice.
 *
 * `null` means "unknown", which `resolveSessionLimit` treats as "fall back to
 * the user's own preference" rather than guessing a cap.
 */
let cachedPlanSeats: number | null = null;

/** Seats last reported by the license server; null until a successful check. */
export function knownPlanSeats(): number | null {
  return cachedPlanSeats;
}

/** Overwrite the cache. Exported for tests and for activation. */
export function setKnownPlanSeats(seats: number | null): void {
  cachedPlanSeats = seats;
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
