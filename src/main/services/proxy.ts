/**
 * Proxy parsing and health checking.
 *
 * Users paste proxies in whatever format their provider gave them, so the
 * parser accepts every common shape rather than demanding one. The checker
 * makes a real request *through* the proxy — the only way to learn the exit IP,
 * which is what every geo/timezone decision downstream depends on.
 */

import { URL } from 'node:url';
import type { ProxyCheckResult, ProxyConfig, ProxyKind } from '../../shared/types';

const CHECK_TIMEOUT_MS = 15_000;

/**
 * Geo lookup endpoints, tried in order. Each returns JSON we can normalise.
 * Multiple providers because a single one being down must not make every proxy
 * in the library look broken.
 */
const GEO_ENDPOINTS = [
  'http://ip-api.com/json/?fields=status,country,countryCode,regionName,city,timezone,lat,lon,query',
  'https://ipwho.is/',
  'https://ipapi.co/json/',
];

// ---------------------------------------------------------------------------
// Parsing
// ---------------------------------------------------------------------------

function normalizeKind(raw: string | undefined): ProxyKind {
  const s = (raw ?? '').toLowerCase().replace(/[^a-z0-9]/g, '');
  if (s === 'socks5' || s === 'socks' || s === 'socks5h' || s === 'socks4') return 'socks5';
  if (s === 'https' || s === 'ssl') return 'https';
  if (s === 'http' || s === 'httpproxy') return 'http';
  return 'http';
}

/** True when a token looks like a host rather than a port or credential. */
function looksLikeHost(token: string): boolean {
  if (!token) return false;
  if (/^\d+$/.test(token)) return false; // pure number = port
  return /^[a-z0-9.\-_]+$/i.test(token) && token.includes('.');
}

/**
 * Parse a single proxy line. Understood formats:
 *
 *   scheme://user:pass@host:port
 *   scheme://host:port
 *   host:port
 *   host:port:user:pass          (the most common provider export)
 *   user:pass@host:port
 *   user:pass:host:port          (some providers invert the order)
 *
 * An optional `scheme://` prefix works with all of them. Returns null when the
 * line cannot be understood, so callers can report the exact bad row.
 */
export function parseProxyLine(line: string): ProxyConfig | null {
  let text = line.trim();
  if (!text || text.startsWith('#')) return null;

  // Strip a leading label like "US-1 | " that some exports include.
  const labelled = text.match(/^[^|]*\|\s*(.+)$/);
  if (labelled?.[1] && (labelled[1].includes(':') || labelled[1].includes('@'))) {
    text = labelled[1].trim();
  }

  let kind: ProxyKind | undefined;
  const schemeMatch = text.match(/^([a-z0-9]+):\/\/(.*)$/i);
  if (schemeMatch) {
    kind = normalizeKind(schemeMatch[1]);
    text = schemeMatch[2]!;
  }

  // Form with @: credentials on the left, host on the right.
  if (text.includes('@')) {
    const at = text.lastIndexOf('@');
    const creds = text.slice(0, at);
    const hostPart = text.slice(at + 1);
    const [host, portStr] = splitHostPort(hostPart);
    if (!host) return null;
    const ci = creds.indexOf(':');
    const username = ci === -1 ? creds : creds.slice(0, ci);
    const password = ci === -1 ? undefined : creds.slice(ci + 1);
    return build(kind, host, portStr, username || undefined, password);
  }

  const parts = text.split(':');
  if (parts.length === 2) {
    return build(kind, parts[0]!, parts[1]);
  }
  if (parts.length === 4) {
    // Disambiguate host:port:user:pass from user:pass:host:port by looking at
    // which side actually resembles a hostname + numeric port.
    const [a, b, c, d] = parts as [string, string, string, string];
    if (looksLikeHost(a) && /^\d+$/.test(b)) return build(kind, a, b, c, d);
    if (looksLikeHost(c) && /^\d+$/.test(d)) return build(kind, c, d, a, b);
    // Ambiguous (e.g. numeric hostname): assume the provider-standard order.
    return build(kind, a, b, c, d);
  }
  if (parts.length === 3) {
    // host:port:user — a password-less authenticated proxy.
    const [a, b, c] = parts as [string, string, string];
    if (/^\d+$/.test(b)) return build(kind, a, b, c);
    return null;
  }
  return null;
}

function splitHostPort(text: string): [string, string | undefined] {
  const idx = text.lastIndexOf(':');
  if (idx === -1) return [text, undefined];
  return [text.slice(0, idx), text.slice(idx + 1)];
}

function build(
  kind: ProxyKind | undefined,
  host: string,
  portStr?: string,
  username?: string,
  password?: string,
): ProxyConfig | null {
  const h = host.trim();
  if (!h) return null;
  const port = portStr ? Number(portStr.trim()) : NaN;
  if (!Number.isInteger(port) || port <= 0 || port > 65535) return null;
  const out: ProxyConfig = { kind: kind ?? 'http', host: h, port };
  if (username) out.username = username;
  if (password) out.password = password;
  return out;
}

/** Parse a multi-line paste. Returns parsed entries plus the failed line numbers. */
export function parseProxyList(text: string): { proxies: ProxyConfig[]; failed: Array<{ line: number; text: string }> } {
  const proxies: ProxyConfig[] = [];
  const failed: Array<{ line: number; text: string }> = [];
  const lines = text.split(/\r?\n/);
  lines.forEach((raw, i) => {
    const trimmed = raw.trim();
    if (!trimmed || trimmed.startsWith('#')) return;
    const parsed = parseProxyLine(trimmed);
    if (parsed) proxies.push(parsed);
    else failed.push({ line: i + 1, text: trimmed });
  });
  return { proxies, failed };
}

/** Full URL including credentials — used only for the proxy agent, never logged. */
export function proxyUrl(p: ProxyConfig): string | undefined {
  if (p.kind === 'none' || !p.host || !p.port) return undefined;
  const scheme = p.kind === 'socks5' ? 'socks5' : p.kind;
  const auth = p.username
    ? `${encodeURIComponent(p.username)}:${encodeURIComponent(p.password ?? '')}@`
    : '';
  return `${scheme}://${auth}${p.host}:${p.port}`;
}

// ---------------------------------------------------------------------------
// Checking
// ---------------------------------------------------------------------------

interface GeoPayload {
  ip?: string;
  query?: string;
  country?: string | { name?: string };
  country_name?: string;
  countryCode?: string;
  country_code?: string;
  city?: string;
  region?: string;
  regionName?: string;
  region_name?: string;
  timezone?: string | { id?: string };
  time_zone?: { name?: string };
  lat?: number;
  latitude?: number;
  lon?: number;
  longitude?: number;
  status?: string;
  success?: boolean;
}

function normaliseGeo(data: GeoPayload): Partial<ProxyCheckResult> {
  const tz =
    typeof data.timezone === 'string'
      ? data.timezone
      : (data.timezone?.id ?? data.time_zone?.name);
  const country = typeof data.country === 'string' ? data.country : data.country?.name ?? data.country_name;
  return {
    ip: data.ip ?? data.query,
    country,
    countryCode: data.countryCode ?? data.country_code,
    city: data.city,
    region: data.regionName ?? data.region_name ?? data.region,
    timezone: tz,
    latitude: data.lat ?? data.latitude,
    longitude: data.lon ?? data.longitude,
  };
}

/**
 * Build a fetch dispatcher that routes through the proxy.
 *
 * Electron/Node 22 ship undici, whose ProxyAgent handles http/https CONNECT.
 * SOCKS5 needs `socks-proxy-agent`, an optional peer of the wrapper; when it is
 * absent we say so plainly instead of silently checking the host's own IP,
 * which would be a dangerously misleading "OK".
 */
async function dispatcherFor(p: ProxyConfig): Promise<{ dispatcher?: unknown; error?: string }> {
  const url = proxyUrl(p);
  if (!url) return { error: 'Incomplete proxy configuration.' };

  if (p.kind === 'socks5') {
    try {
      const { socksDispatcher } = (await import('fetch-socks')) as {
        socksDispatcher: (opts: unknown) => unknown;
      };
      return {
        dispatcher: socksDispatcher({
          type: 5,
          host: p.host!,
          port: p.port!,
          ...(p.username ? { userId: p.username, password: p.password ?? '' } : {}),
        }),
      };
    } catch {
      return {
        error:
          'SOCKS5 checking needs the optional "fetch-socks" package. The proxy will still work for browser sessions — this only affects the IP check.',
      };
    }
  }

  try {
    const { ProxyAgent } = (await import('undici')) as { ProxyAgent: new (url: string) => unknown };
    return { dispatcher: new ProxyAgent(url) };
  } catch (e) {
    return { error: `Could not create a proxy agent: ${(e as Error).message}` };
  }
}

/** Run a real request through the proxy and report the exit IP and geo data. */
export async function checkProxy(p: ProxyConfig): Promise<ProxyCheckResult> {
  const startedAt = Date.now();
  if (p.kind === 'none') {
    return { ok: false, checkedAt: startedAt, error: 'No proxy configured.' };
  }

  const { dispatcher, error } = await dispatcherFor(p);
  if (error) return { ok: false, checkedAt: startedAt, error };

  let lastError = 'All geo lookup services failed.';
  for (const endpoint of GEO_ENDPOINTS) {
    const t0 = Date.now();
    try {
      const resp = await fetch(endpoint, {
        signal: AbortSignal.timeout(CHECK_TIMEOUT_MS),
        // `dispatcher` is a valid undici RequestInit extension that the DOM
        // typings do not model.
        ...({ dispatcher } as Record<string, unknown>),
      });
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
      const data = (await resp.json()) as GeoPayload;
      if (data.status === 'fail' || data.success === false) throw new Error('Lookup service rejected the request.');
      const geo = normaliseGeo(data);
      if (!geo.ip) throw new Error('Lookup returned no IP address.');
      return { ok: true, checkedAt: Date.now(), latencyMs: Date.now() - t0, ...geo };
    } catch (e) {
      lastError = describeProxyError(e);
    }
  }
  return { ok: false, checkedAt: Date.now(), error: lastError };
}

/** Turn low-level network errors into something a user can act on. */
function describeProxyError(e: unknown): string {
  const err = e as { name?: string; code?: string; message?: string; cause?: { code?: string; message?: string } };
  const code = err?.code ?? err?.cause?.code;
  const msg = err?.message ?? String(e);
  if (err?.name === 'TimeoutError' || code === 'UND_ERR_CONNECT_TIMEOUT' || /timeout/i.test(msg)) {
    return 'Timed out — the proxy did not respond in time.';
  }
  if (code === 'ECONNREFUSED') return 'Connection refused — check the host and port.';
  if (code === 'ENOTFOUND' || code === 'EAI_AGAIN') return 'Host not found — check the proxy address.';
  if (code === 'ECONNRESET') return 'Connection reset by the proxy.';
  if (/407|proxy authentication/i.test(msg)) return 'Proxy authentication failed — check the username and password.';
  if (/certificate|self.signed/i.test(msg)) return 'TLS error while connecting through the proxy.';
  return msg.split('\n')[0] || 'Unknown proxy error.';
}

/** Trigger a provider rotation URL (best effort, direct connection). */
export async function rotateProxy(rotationUrl: string): Promise<{ ok: boolean; status?: number; error?: string }> {
  try {
    const u = new URL(rotationUrl);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') {
      return { ok: false, error: 'The rotation link must be an http(s) URL.' };
    }
    const resp = await fetch(u, { signal: AbortSignal.timeout(CHECK_TIMEOUT_MS) });
    return { ok: resp.ok, status: resp.status };
  } catch (e) {
    return { ok: false, error: describeProxyError(e) };
  }
}
