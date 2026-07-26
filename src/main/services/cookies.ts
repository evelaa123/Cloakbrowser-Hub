/**
 * Cookie engine: parse → validate → merge → encrypt → inject.
 *
 * Adapted from a battle-tested uploader implementation and generalised from
 * "Google only" to any service, because an anti-detect profile can hold a
 * session for anything (Facebook, TikTok, Amazon, a bank...).
 *
 * The two hard-won rules that make cookie import actually work:
 *
 *  1. **Sanitise before injecting.** Chromium silently *drops* cookies that
 *     violate the `__Host-` / `__Secure-` / `SameSite=None` rules. A dropped
 *     auth cookie looks exactly like a wrong password to the user, so we repair
 *     each cookie to a form Chromium will accept.
 *  2. **Never fail the batch.** `addCookies()` rejects the whole array when one
 *     entry is malformed. We retry one-by-one so a single junk row can't cost
 *     the user the entire session.
 */

import fs from 'node:fs';
import type { BrowserContext } from 'playwright-core';
import type { CookieValidation } from '../../shared/types';
import { decrypt, encrypt } from './secrets';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Playwright-compatible cookie shape (what ctx.addCookies expects). */
export interface PwCookie {
  name: string;
  value: string;
  domain?: string;
  path?: string;
  /** Unix seconds; -1 = session cookie. */
  expires?: number;
  httpOnly?: boolean;
  secure?: boolean;
  sameSite?: 'Strict' | 'Lax' | 'None';
  url?: string;
}

export type CookieLogger = (msg: string) => void;

const ENC_MARKERS = ['ENC1:', 'ENC2:'];

// ---------------------------------------------------------------------------
// Known-session signatures
// ---------------------------------------------------------------------------

/**
 * Cookie names that indicate a live login, grouped by service. Used only for
 * diagnostics and for the "this file contains a session for X" hint in the UI —
 * never to gate an import (an unknown service is still a valid session).
 */
export const AUTH_SIGNATURES: Record<string, string[]> = {
  Google: ['__Secure-1PSID', '__Secure-3PSID', 'SID', 'SAPISID', 'SSID', 'HSID', 'LOGIN_INFO'],
  Facebook: ['c_user', 'xs', 'fr', 'datr'],
  Instagram: ['sessionid', 'ds_user_id'],
  TikTok: ['sessionid', 'sessionid_ss', 'sid_tt'],
  X: ['auth_token', 'ct0'],
  LinkedIn: ['li_at', 'JSESSIONID'],
  Amazon: ['session-id', 'x-main', 'at-main'],
  Reddit: ['reddit_session', 'token_v2'],
  Discord: ['__Secure-recent_mfa', '__dcfduid', '__sdcfduid'],
  Microsoft: ['ESTSAUTH', 'ESTSAUTHPERSISTENT', 'MSPAuth'],
  eBay: ['s', 'nonsession', 'ebay'],
  PayPal: ['login_email', 'LANG', 'x-pp-s'],
  Twitch: ['auth-token', 'persistent'],
  Shopify: ['_shopify_y', '_secure_session_id'],
};

/**
 * Cookies that are HttpOnly in a real browser. Netscape exports frequently omit
 * the `#HttpOnly_` prefix, so every row parses as httpOnly=false. Restoring the
 * flag keeps the saved jar closer to reality (Chromium sends the value either
 * way, so this is correctness, not a functional fix).
 */
const HTTPONLY_HINTS = new Set([
  'SID', 'HSID', 'SSID', 'LSID', 'APISID', 'SAPISID',
  '__Secure-1PSID', '__Secure-3PSID', '__Secure-1PAPISID', '__Secure-3PAPISID',
  '__Host-1PLSID', '__Host-3PLSID', '__Host-GAPS', 'LOGIN_INFO',
  'xs', 'c_user', 'datr', 'sessionid', 'auth_token', 'li_at', 'ESTSAUTH',
]);

/**
 * Cookies whose absence after injection means a *partial* session — the state
 * that triggers "verify it's you" / instant logout. Reported in diagnostics.
 */
const CRITICAL_COOKIES: Record<string, string[]> = {
  Google: [
    '__Secure-1PSID', '__Secure-3PSID', 'SID', 'HSID', 'SSID', 'SAPISID', 'APISID',
    '__Secure-1PSIDTS', '__Secure-3PSIDTS', 'LOGIN_INFO',
    '__Host-1PLSID', '__Host-3PLSID', '__Host-GAPS', 'LSID',
  ],
  Facebook: ['c_user', 'xs', 'datr'],
  Instagram: ['sessionid', 'ds_user_id'],
  X: ['auth_token', 'ct0'],
  LinkedIn: ['li_at'],
};

/**
 * Domains that need `SameSite=None` when the source export didn't say.
 * These identity providers embed themselves cross-site (iframes, SSO popups);
 * Playwright's default of Lax would break the third-party cookie and the login
 * silently fails.
 */
const CROSS_SITE_HOSTS = [
  /(^|\.)google\.com$/, /(^|\.)youtube\.com$/, /(^|\.)google\.[a-z.]+$/,
  /(^|\.)facebook\.com$/, /(^|\.)instagram\.com$/, /(^|\.)tiktok\.com$/,
  /(^|\.)x\.com$/, /(^|\.)twitter\.com$/, /(^|\.)linkedin\.com$/,
  /(^|\.)microsoftonline\.com$/, /(^|\.)live\.com$/, /(^|\.)microsoft\.com$/,
  /(^|\.)paypal\.com$/, /(^|\.)amazon\.[a-z.]+$/, /(^|\.)doubleclick\.net$/,
  /(^|\.)recaptcha\.net$/, /(^|\.)gstatic\.com$/,
];

// ---------------------------------------------------------------------------
// Normalisation helpers
// ---------------------------------------------------------------------------

function normSameSite(v: unknown): 'Strict' | 'Lax' | 'None' | undefined {
  if (v == null) return undefined;
  const s = String(v).toLowerCase().replace(/[_-]/g, '');
  if (s === 'strict') return 'Strict';
  if (s === 'lax') return 'Lax';
  if (s === 'none' || s === 'norestriction' || s === 'unspecified') return 'None';
  return undefined;
}

function toUnixSeconds(v: unknown): number | undefined {
  if (v == null || v === '' || v === 0 || v === '0') return undefined;
  const n = Number(v);
  if (!Number.isFinite(n) || n <= 0) return undefined;
  // Values beyond ~10^12 are milliseconds (JS Date.now scale) → to seconds.
  return n > 1e12 ? Math.floor(n / 1000) : Math.floor(n);
}

function looksCrossSite(host: string, url?: string): boolean {
  const h = host.replace(/^\./, '');
  if (h && CROSS_SITE_HOSTS.some((re) => re.test(h))) return true;
  if (url) {
    try {
      const u = new URL(url).hostname;
      return CROSS_SITE_HOSTS.some((re) => re.test(u));
    } catch {
      return false;
    }
  }
  return false;
}

/** Default host used when a cookie carries no domain at all. */
function fallbackHost(cookies: PwCookie[]): string {
  const withDomain = cookies.find((c) => c.domain);
  return (withDomain?.domain ?? 'example.com').replace(/^\./, '');
}

// ---------------------------------------------------------------------------
// sanitizeCookie — the part that stops Chromium dropping cookies silently
// ---------------------------------------------------------------------------

/**
 * Repair a parsed cookie so both Playwright *and* Chromium accept it.
 *
 * Playwright rule: a cookie must carry EITHER `url` OR (`domain` + `path`),
 * never `url` + `path` together ("Cookie should have either url or path").
 *
 * Chromium rules (RFC 6265bis):
 *  - `__Host-` → host-only (no Domain attribute), Secure, Path=/.
 *    Playwright expresses host-only via `url`, so we drop domain/path and use
 *    the https origin.
 *  - `__Secure-` → Secure=true, mandatory.
 *  - `SameSite=None` → requires Secure=true, else the cookie is discarded.
 */
export function sanitizeCookie(c: PwCookie, defaultHost = 'example.com'): PwCookie | null {
  if (!c || !c.name) return null;

  const name = c.name;
  const out: PwCookie = { name, value: c.value ?? '' };
  const srcPath = c.path && c.path.trim() ? c.path : '/';

  const isHost = name.startsWith('__Host-');
  const isSecurePrefix = name.startsWith('__Secure-');

  let sameSite = normSameSite(c.sameSite);
  let secure = Boolean(c.secure);
  if (isSecurePrefix || isHost) secure = true;

  const domain = c.domain ? String(c.domain).trim() : undefined;

  if (isHost) {
    // Host-only: url ONLY. Setting domain or path here would be rejected.
    const host = (domain || '').replace(/^\./, '') || defaultHost;
    out.url = `https://${host}/`;
  } else if (domain) {
    out.domain = domain;
    out.path = srcPath;
  } else if (c.url) {
    out.url = c.url;
  } else {
    // No domain and no url: synthesise one so the cookie is not lost.
    out.url = `${secure ? 'https' : 'http'}://${defaultHost}${srcPath}`;
  }

  // SameSite defaulting for known cross-site identity providers. Netscape has
  // no SameSite column, and Playwright's implicit Lax breaks embedded flows.
  const host = (out.domain || '').replace(/^\./, '');
  if (!sameSite && looksCrossSite(host, out.url)) sameSite = 'None';
  if (sameSite === 'None') secure = true; // mandatory pairing

  out.secure = secure;
  if (sameSite) out.sameSite = sameSite;
  out.httpOnly = Boolean(c.httpOnly);
  out.expires = typeof c.expires === 'number' ? c.expires : -1;

  return out;
}

// ---------------------------------------------------------------------------
// Parsers
// ---------------------------------------------------------------------------

/**
 * Parse a JSON cookie export. Accepts:
 *  - a bare array (Playwright / Puppeteer / EditThisCookie / Cookie-Editor)
 *  - `{ cookies: [...] }`
 *  - Playwright storageState `{ cookies: [...], origins: [...] }`
 *  - extension field aliases (expirationDate, hostOnly, sameSite='no_restriction')
 */
export function parseJsonCookies(text: string): PwCookie[] {
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch {
    return [];
  }
  const obj = data as Record<string, unknown>;
  const arr: unknown[] = Array.isArray(data)
    ? data
    : Array.isArray(obj?.cookies)
      ? (obj.cookies as unknown[])
      : [];

  const out: PwCookie[] = [];
  for (const item of arr) {
    if (!item || typeof item !== 'object') continue;
    const c = item as Record<string, unknown>;
    const name = c.name ?? c.Name;
    const value = c.value ?? c.Value;
    if (name == null || value == null) continue;

    let domain = (c.domain ?? c.Domain ?? c.host ?? c.hostKey) as string | undefined;
    if (domain) domain = String(domain).trim();

    const cookie: PwCookie = {
      name: String(name),
      value: String(value),
      path: (c.path ?? c.Path ?? '/') as string,
    };
    if (domain) cookie.domain = domain;

    cookie.expires = toUnixSeconds(c.expires ?? c.expirationDate ?? c.expiry ?? c.Expires) ?? -1;
    cookie.httpOnly = Boolean(c.httpOnly ?? c.HttpOnly ?? c.httponly ?? false);
    cookie.secure = Boolean(c.secure ?? c.Secure ?? false);
    const ss = normSameSite(c.sameSite ?? c.SameSite ?? c.samesite);
    if (ss) cookie.sameSite = ss;
    if (!cookie.domain && typeof c.url === 'string') cookie.url = c.url;

    out.push(cookie);
  }
  return out;
}

/**
 * Parse a Netscape `cookies.txt` file (curl / wget / browser extensions):
 *   domain \t flag \t path \t secure \t expiration \t name \t value
 * `#` starts a comment, except `#HttpOnly_` which prefixes the domain field.
 */
export function parseNetscapeCookies(text: string): PwCookie[] {
  const out: PwCookie[] = [];
  for (const raw of text.split(/\r?\n/)) {
    let line = raw;
    if (!line || !line.trim()) continue;

    let httpOnly = false;
    if (line.startsWith('#HttpOnly_')) {
      httpOnly = true;
      line = line.slice('#HttpOnly_'.length);
    } else if (line.startsWith('#')) {
      continue;
    }

    let parts = line.split('\t');
    if (parts.length < 7) {
      // Some exports use runs of spaces instead of tabs. The value is last and
      // may itself contain spaces, so everything past field 6 is re-joined.
      const loose = line.trim().split(/\s+/);
      if (loose.length < 7) continue;
      parts = [...loose.slice(0, 6), loose.slice(6).join(' ')];
    }

    const [domain, , cpath, secure, expires, name, value] = parts;
    if (!name) continue;
    const nm = String(name).trim();

    const cookie: PwCookie = {
      name: nm,
      value: String(value ?? '').trim(),
      domain: String(domain).trim(),
      path: (cpath && cpath.trim()) || '/',
      secure: String(secure).trim().toUpperCase() === 'TRUE',
      httpOnly: httpOnly || HTTPONLY_HINTS.has(nm),
    };
    cookie.expires = toUnixSeconds(expires) ?? -1;
    out.push(cookie);
  }
  return out;
}

/**
 * Parse a raw `Cookie:` header / `name=value; name2=value2` string.
 * Domain is unknown in this format, so the caller must supply one (the UI asks
 * for it when this format is detected).
 */
export function parseHeaderCookies(text: string, domain?: string): PwCookie[] {
  const body = text.replace(/^\s*cookie\s*:\s*/i, '').trim();
  if (!body || !body.includes('=')) return [];
  const out: PwCookie[] = [];
  for (const pair of body.split(';')) {
    const idx = pair.indexOf('=');
    if (idx <= 0) continue;
    const name = pair.slice(0, idx).trim();
    const value = pair.slice(idx + 1).trim();
    if (!name) continue;
    const cookie: PwCookie = { name, value, path: '/', expires: -1, httpOnly: HTTPONLY_HINTS.has(name) };
    if (domain) cookie.domain = domain.startsWith('.') ? domain : `.${domain}`;
    out.push(cookie);
  }
  return out;
}

/** Detect the format and parse. `domain` is only used for header-style input. */
export function parseCookieText(text: string, domain?: string): PwCookie[] {
  const trimmed = text.trim();
  if (!trimmed) return [];
  if (trimmed.startsWith('[') || trimmed.startsWith('{')) {
    const json = parseJsonCookies(trimmed);
    if (json.length) return json;
  }
  const netscape = parseNetscapeCookies(trimmed);
  if (netscape.length) return netscape;
  return parseHeaderCookies(trimmed, domain);
}

// ---------------------------------------------------------------------------
// Validation (drives the import UI before anything is written)
// ---------------------------------------------------------------------------

/** Which known services this cookie set appears to hold a session for. */
export function detectAuthServices(names: Set<string>, domains: string[]): string[] {
  const hits: string[] = [];
  for (const [service, sigs] of Object.entries(AUTH_SIGNATURES)) {
    const matched = sigs.filter((n) => names.has(n)).length;
    if (!matched) continue;
    // A generic name like "sessionid" or "s" exists everywhere, so require the
    // service's own domain to be present before claiming a session for it.
    const domainHint = service.toLowerCase();
    const domainMatch = domains.some((d) => d.toLowerCase().includes(domainHint));
    if (matched >= 2 || domainMatch) hits.push(service);
  }
  return hits;
}

export function validateCookieText(text: string): CookieValidation {
  const trimmed = (text ?? '').trim();
  const empty: CookieValidation = {
    ok: false, count: 0, format: 'unknown', domains: [], authHints: [], suggestedName: '',
  };
  if (!trimmed) return { ...empty, error: 'The file is empty.' };

  let cookies: PwCookie[] = [];
  let format: CookieValidation['format'] = 'unknown';

  if (trimmed.startsWith('[') || trimmed.startsWith('{')) {
    cookies = parseJsonCookies(trimmed);
    if (cookies.length) format = 'json';
  }
  if (!cookies.length) {
    cookies = parseNetscapeCookies(trimmed);
    if (cookies.length) format = 'netscape';
  }
  if (!cookies.length) {
    cookies = parseHeaderCookies(trimmed);
    if (cookies.length) format = 'header';
  }
  if (!cookies.length) {
    return {
      ...empty,
      error: 'Unrecognised format. Supported: JSON (Cookie-Editor / EditThisCookie / Playwright), Netscape cookies.txt, or a raw Cookie: header.',
    };
  }

  const domains = [...new Set(cookies.map((c) => (c.domain ?? '').trim()).filter(Boolean))].sort();
  const names = new Set(cookies.map((c) => c.name));
  const authHints = detectAuthServices(names, domains);

  // Suggest a profile name. Cookie files rarely carry the account email, so an
  // email found anywhere in the payload wins, else the primary domain.
  let suggestedName = '';
  const emailMatch = trimmed.match(/[A-Za-z0-9._%+-]+@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}/);
  if (emailMatch) {
    suggestedName = emailMatch[0];
  } else if (authHints.length) {
    suggestedName = `${authHints[0]} account`;
  } else if (domains.length) {
    suggestedName = primaryDomain(domains);
  }

  return { ok: true, count: cookies.length, format, domains, authHints, suggestedName };
}

/** Pick the most representative domain from a list (shortest registrable-ish). */
function primaryDomain(domains: string[]): string {
  const cleaned = domains.map((d) => d.replace(/^\./, ''));
  const counts = new Map<string, number>();
  for (const d of cleaned) {
    const parts = d.split('.');
    const base = parts.slice(-2).join('.');
    counts.set(base, (counts.get(base) ?? 0) + 1);
  }
  return [...counts.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? cleaned[0] ?? '';
}

export function validateCookieFile(filePath: string): CookieValidation {
  try {
    return validateCookieText(fs.readFileSync(filePath, 'utf-8'));
  } catch (e) {
    return {
      ok: false, count: 0, format: 'unknown', domains: [], authHints: [], suggestedName: '',
      error: `Could not read the file: ${(e as Error)?.message ?? 'unknown error'}`,
    };
  }
}

// ---------------------------------------------------------------------------
// Jar read / write
// ---------------------------------------------------------------------------

/** Merge cookies, de-duplicating on (name, domain, path). Later wins. */
export function mergeCookies(...sets: PwCookie[][]): PwCookie[] {
  const merged = new Map<string, PwCookie>();
  for (const set of sets) {
    for (const c of set) {
      merged.set(`${c.name}\u0000${c.domain ?? c.url ?? ''}\u0000${c.path ?? '/'}`, c);
    }
  }
  return [...merged.values()];
}

/** Read (and decrypt) a jar. Returns [] when missing or undecryptable. */
export function readJar(jarPath: string): PwCookie[] {
  if (!fs.existsSync(jarPath)) return [];
  try {
    const raw = fs.readFileSync(jarPath, 'utf-8');
    const isEnc = ENC_MARKERS.some((m) => raw.startsWith(m));
    const json = isEnc ? decrypt(raw) : raw;
    if (!json) return []; // wrong machine/user → treat as "no session"
    const parsed = JSON.parse(json);
    return Array.isArray(parsed) ? (parsed as PwCookie[]) : [];
  } catch {
    return [];
  }
}

/** Write a jar, encrypted at rest. */
export function writeJar(jarPath: string, cookies: PwCookie[]): void {
  fs.writeFileSync(jarPath, encrypt(JSON.stringify(cookies)), 'utf-8');
}

/**
 * Import cookie files into a profile jar. Existing cookies are kept unless
 * `replace` is set; imported files override on (name, domain, path) collision.
 */
export function importCookieFiles(
  filePaths: string[],
  jarPath: string,
  opts: { replace?: boolean; domain?: string } = {},
): { count: number; files: number; domains: string[]; authHints: string[] } {
  const sets: PwCookie[][] = opts.replace ? [] : [readJar(jarPath)];
  let filesOk = 0;

  for (const fp of filePaths) {
    try {
      const cookies = parseCookieText(fs.readFileSync(fp, 'utf-8'), opts.domain);
      if (!cookies.length) continue;
      filesOk++;
      sets.push(cookies);
    } catch {
      // Unreadable or malformed file: skip it, keep importing the rest.
    }
  }

  const all = mergeCookies(...sets);
  writeJar(jarPath, all);

  const domains = [...new Set(all.map((c) => (c.domain ?? '').replace(/^\./, '')).filter(Boolean))];
  const authHints = detectAuthServices(new Set(all.map((c) => c.name)), domains);
  return { count: all.length, files: filesOk, domains, authHints };
}

/** Import cookies from a pasted string rather than a file. */
export function importCookieText(
  text: string,
  jarPath: string,
  opts: { replace?: boolean; domain?: string } = {},
): { count: number; domains: string[]; authHints: string[] } {
  const parsed = parseCookieText(text, opts.domain);
  const sets: PwCookie[][] = opts.replace ? [parsed] : [readJar(jarPath), parsed];
  const all = mergeCookies(...sets);
  writeJar(jarPath, all);
  const domains = [...new Set(all.map((c) => (c.domain ?? '').replace(/^\./, '')).filter(Boolean))];
  return { count: all.length, domains, authHints: detectAuthServices(new Set(all.map((c) => c.name)), domains) };
}

// ---------------------------------------------------------------------------
// Injection into a live BrowserContext
// ---------------------------------------------------------------------------

/**
 * Load a jar into a context.
 *
 * Batch-first for speed, then one-by-one on failure so a single bad cookie
 * can't drop the whole session. Emits diagnostics on how many landed and
 * whether any critical session cookie went missing.
 */
export async function loadCookiesInto(
  ctx: BrowserContext,
  jarPath: string,
  log?: CookieLogger,
): Promise<{ parsed: number; accepted: number; missing: string[] }> {
  const parsed = readJar(jarPath);
  if (!parsed.length) {
    log?.('[cookies] no saved cookies for this profile');
    return { parsed: 0, accepted: 0, missing: [] };
  }

  const defaultHost = fallbackHost(parsed);
  const sanitized: PwCookie[] = [];
  for (const c of parsed) {
    const s = sanitizeCookie(c, defaultHost);
    if (s) sanitized.push(s);
  }

  let accepted = 0;
  try {
    await ctx.addCookies(sanitized as Parameters<BrowserContext['addCookies']>[0]);
    accepted = sanitized.length;
  } catch (batchErr) {
    const first = String((batchErr as Error)?.message ?? batchErr).split('\n')[0];
    log?.(`[cookies] batch import failed (${first}); retrying one by one`);
    for (const c of sanitized) {
      try {
        await ctx.addCookies([c] as Parameters<BrowserContext['addCookies']>[0]);
        accepted++;
      } catch (e) {
        const msg = String((e as Error)?.message ?? e).split('\n')[0];
        log?.(`[cookies] dropped ${c.name} @ ${c.domain ?? c.url ?? '?'}: ${msg}`);
      }
    }
  }

  // Verify what actually landed. Querying by explicit origins matters: a bare
  // ctx.cookies() can omit host-only (__Host-*) cookies, which are exactly the
  // ones most often lost.
  const missing: string[] = [];
  try {
    const origins = originsFor(parsed);
    const inCtx = origins.length ? await ctx.cookies(origins) : await ctx.cookies();
    const names = new Set(inCtx.map((c) => c.name));
    const parsedNames = new Set(parsed.map((c) => c.name));

    for (const [service, critical] of Object.entries(CRITICAL_COOKIES)) {
      // Only judge a service whose session was actually present in the file.
      const relevant = critical.filter((n) => parsedNames.has(n));
      if (!relevant.length) continue;
      const gone = relevant.filter((n) => !names.has(n));
      if (gone.length) missing.push(...gone.map((n) => `${service}:${n}`));
    }

    log?.(`[cookies] parsed=${parsed.length} sanitized=${sanitized.length} accepted=${accepted} inContext=${inCtx.length}`);
    if (missing.length) {
      log?.(`[cookies] WARNING missing session cookies after import: ${missing.join(', ')} — the site may ask to verify the login.`);
    } else {
      log?.('[cookies] all known session cookies present in the browser context');
    }
  } catch {
    // Diagnostics are best-effort; never fail an import over them.
  }

  return { parsed: parsed.length, accepted, missing };
}

/** Build the origin list used to verify an import (https + http per domain). */
function originsFor(cookies: PwCookie[]): string[] {
  const hosts = new Set<string>();
  for (const c of cookies) {
    if (c.domain) {
      hosts.add(c.domain.replace(/^\./, ''));
    } else if (c.url) {
      try {
        hosts.add(new URL(c.url).hostname);
      } catch {
        /* ignore */
      }
    }
  }
  // Cap the list: ctx.cookies() with hundreds of origins is slow and the
  // diagnostic value plateaus quickly.
  return [...hosts].slice(0, 60).map((h) => `https://${h}/`);
}

/** Persist the context's current cookies back into the encrypted jar. */
export async function saveCookiesFrom(
  ctx: BrowserContext,
  jarPath: string,
  log?: CookieLogger,
): Promise<number> {
  try {
    const cookies = (await ctx.cookies()) as PwCookie[];
    writeJar(jarPath, cookies);
    log?.(`[cookies] saved ${cookies.length} cookies`);
    return cookies.length;
  } catch (e) {
    log?.(`[cookies] save failed: ${String((e as Error)?.message ?? e).split('\n')[0]}`);
    return 0;
  }
}

/** Export a jar to a plain file for the user (JSON or Netscape). */
export function exportJar(jarPath: string, destPath: string, format: 'json' | 'netscape'): number {
  const cookies = readJar(jarPath);
  if (format === 'json') {
    fs.writeFileSync(destPath, JSON.stringify(cookies, null, 2), 'utf-8');
    return cookies.length;
  }
  const lines = [
    '# Netscape HTTP Cookie File',
    '# Exported by CloakBrowser Hub',
    '',
  ];
  for (const c of cookies) {
    const domain = c.domain ?? (c.url ? safeHost(c.url) : '');
    if (!domain) continue;
    const includeSub = domain.startsWith('.') ? 'TRUE' : 'FALSE';
    const line = [
      domain,
      includeSub,
      c.path ?? '/',
      c.secure ? 'TRUE' : 'FALSE',
      String(c.expires && c.expires > 0 ? Math.floor(c.expires) : 0),
      c.name,
      c.value,
    ].join('\t');
    lines.push(c.httpOnly ? `#HttpOnly_${line}` : line);
  }
  fs.writeFileSync(destPath, lines.join('\n') + '\n', 'utf-8');
  return cookies.length;
}

function safeHost(url: string): string {
  try {
    return new URL(url).hostname;
  } catch {
    return '';
  }
}
