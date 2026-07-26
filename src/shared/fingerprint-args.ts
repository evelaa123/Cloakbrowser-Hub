/**
 * Translate a Hub profile into CloakBrowser launch options.
 *
 * Pure functions only — no Node APIs — so this module is unit-testable and can
 * also be imported by the renderer to *preview* the exact flags a profile will
 * launch with (the "Advanced → resolved flags" panel).
 *
 * Flag reference (CloakBrowser binary):
 *   --fingerprint=<seed>                master seed: canvas/WebGL/audio/fonts/rects
 *   --fingerprint-platform=<os>         navigator.platform, UA OS, GPU pool
 *   --fingerprint-platform-version=     Client Hints platform version
 *   --fingerprint-brand / -brand-version
 *   --fingerprint-screen-width/-height
 *   --fingerprint-gpu-vendor/-renderer
 *   --fingerprint-hardware-concurrency  navigator.hardwareConcurrency
 *   --fingerprint-device-memory         navigator.deviceMemory (GB)
 *   --fingerprint-storage-quota         MB — raise to look non-incognito
 *   --fingerprint-timezone / -locale
 *   --fingerprint-location=<lat,lon>
 *   --fingerprint-noise=false           disable noise, keep deterministic seed
 *   --fingerprint-windows-font-metrics  Chromium 148+
 *   --fingerprint-allow-3p-cookies      Chromium 148+
 *   --fingerprint-fonts-dir=<path>
 *   --fingerprint-webrtc-ip=auto|<ip>
 *   --fingerprint-taskbar-height=<px>
 */

import type { Profile } from './types';

/** Flags the app always owns — a user-supplied duplicate is overridden. */
const OWNED_PREFIXES = ['--fingerprint', '--lang'];

export interface ResolvedLaunch {
  /** Chromium CLI flags (deduplicated, deterministic order). */
  args: string[];
  /** Value for the wrapper's `timezone` option, if pinned. */
  timezone?: string;
  /** Value for the wrapper's `locale` option, if pinned. */
  locale?: string;
  /** Ask the wrapper to resolve timezone/locale from the proxy exit IP. */
  geoip: boolean;
  headless: boolean;
  humanize: boolean;
  humanPreset: 'default' | 'careful';
  humanConfig?: Record<string, unknown>;
  extensionPaths: string[];
  userAgent?: string;
  proxy?: string | { server: string; bypass?: string; username?: string; password?: string };
}

/** Build a proxy value in the shape the CloakBrowser wrapper expects. */
export function buildProxyOption(profile: Profile): ResolvedLaunch['proxy'] {
  const p = profile.proxy;
  if (!p || p.kind === 'none' || !p.host || !p.port) return undefined;
  const scheme = p.kind === 'socks5' ? 'socks5' : p.kind;
  const server = `${scheme}://${p.host}:${p.port}`;
  // Object form keeps credentials out of the server string, which avoids
  // double-encoding problems with passwords containing @ : / etc.
  const out: { server: string; bypass?: string; username?: string; password?: string } = { server };
  if (p.username) out.username = p.username;
  if (p.password) out.password = p.password;
  if (p.bypass) out.bypass = p.bypass;
  return out;
}

/** Human-readable proxy string for the UI (password masked). */
export function proxyLabel(profile: Profile): string {
  const p = profile.proxy;
  if (!p || p.kind === 'none' || !p.host) return 'Direct (no proxy)';
  const auth = p.username ? `${p.username}:••••@` : '';
  return `${p.kind}://${auth}${p.host}:${p.port ?? ''}`;
}

/**
 * Build the fingerprint-related Chromium flags for a profile.
 *
 * Only values the user pinned (`mode: 'manual'`) become explicit flags; auto
 * values are deliberately left to the binary so they stay coherent with the
 * seed. `extraArgs` are applied last and win on conflict, except for flags the
 * app owns (`--fingerprint*`, `--lang`) where the profile wins — otherwise a
 * stray user flag could silently break the identity the profile promises.
 */
export function buildFingerprintArgs(profile: Profile): string[] {
  const fp = profile.fingerprint;
  const flags = new Map<string, string>();
  const set = (flag: string, value?: string | number | boolean) => {
    flags.set(flag, value === undefined ? flag : `${flag}=${value}`);
  };

  if (typeof fp.seed === 'number' && Number.isFinite(fp.seed) && fp.seed > 0) {
    set('--fingerprint', Math.floor(fp.seed));
  }
  set('--fingerprint-platform', fp.platform);
  if (fp.platformVersion) set('--fingerprint-platform-version', fp.platformVersion);
  if (fp.brand && fp.brand !== 'Chrome') set('--fingerprint-brand', fp.brand);
  if (fp.brandVersion) set('--fingerprint-brand-version', fp.brandVersion);

  if (fp.screen.mode === 'manual' && fp.screen.width && fp.screen.height) {
    set('--fingerprint-screen-width', fp.screen.width);
    set('--fingerprint-screen-height', fp.screen.height);
  }
  if (fp.gpu.mode === 'manual') {
    if (fp.gpu.vendor) set('--fingerprint-gpu-vendor', fp.gpu.vendor);
    if (fp.gpu.renderer) set('--fingerprint-gpu-renderer', fp.gpu.renderer);
  }
  if (fp.cpuCores.mode === 'manual' && fp.cpuCores.value) {
    set('--fingerprint-hardware-concurrency', fp.cpuCores.value);
  }
  if (fp.deviceMemory.mode === 'manual' && fp.deviceMemory.value) {
    set('--fingerprint-device-memory', fp.deviceMemory.value);
  }
  if (typeof fp.storageQuotaMb === 'number' && fp.storageQuotaMb > 0) {
    set('--fingerprint-storage-quota', Math.floor(fp.storageQuotaMb));
  }
  if (typeof fp.taskbarHeight === 'number' && fp.taskbarHeight >= 0) {
    set('--fingerprint-taskbar-height', Math.floor(fp.taskbarHeight));
  }
  // Noise is ON by default in the binary; only the opt-out needs a flag.
  if (fp.noise === false) set('--fingerprint-noise', 'false');
  if (fp.windowsFontMetrics && fp.platform === 'windows') {
    set('--fingerprint-windows-font-metrics');
  }
  if (fp.fontsDir) set('--fingerprint-fonts-dir', fp.fontsDir);
  if (fp.allowThirdPartyCookies) set('--fingerprint-allow-3p-cookies');

  // WebRTC: only meaningful behind a proxy — a spoofed ICE IP with a direct
  // connection is itself a mismatch, so 'auto' is skipped without a proxy.
  const hasProxy = profile.proxy?.kind && profile.proxy.kind !== 'none' && !!profile.proxy.host;
  if (fp.webrtc.mode === 'manual' && fp.webrtc.ip) {
    set('--fingerprint-webrtc-ip', fp.webrtc.ip);
  } else if (fp.webrtc.mode === 'auto' && hasProxy) {
    set('--fingerprint-webrtc-ip', 'auto');
  }

  // Geolocation: explicit coordinates only. 'ip' mode is handled by geoip in
  // the wrapper, 'off' leaves the binary default untouched.
  if (profile.geo?.mode === 'manual' && profile.geo.latitude != null && profile.geo.longitude != null) {
    set('--fingerprint-location', `${profile.geo.latitude},${profile.geo.longitude}`);
  }

  // Pinned locale must also reach --lang so Accept-Language matches.
  if (profile.locale?.mode === 'manual' && profile.locale.locale) {
    set('--lang', profile.locale.locale);
    set('--fingerprint-locale', profile.locale.locale);
  }
  if (profile.locale?.mode === 'manual' && profile.locale.timezone) {
    set('--fingerprint-timezone', profile.locale.timezone);
  }

  // User extra args last: they may add flags we don't model, but they may not
  // hijack the identity flags above.
  for (const raw of profile.startup?.extraArgs ?? []) {
    const arg = raw.trim();
    if (!arg.startsWith('--')) continue;
    const key = arg.split('=')[0]!;
    const owned = OWNED_PREFIXES.some((p) => key === p || key.startsWith(p + '-'));
    if (owned && flags.has(key)) continue;
    flags.set(key, arg);
  }

  return [...flags.values()];
}

/** Build the full set of options for `launchPersistentContext()`. */
export function resolveLaunch(profile: Profile): ResolvedLaunch {
  const usesIpLocale = profile.locale?.mode === 'ip';
  const hasProxy = !!(profile.proxy?.kind && profile.proxy.kind !== 'none' && profile.proxy.host);

  const humanConfig: Record<string, unknown> = {};
  if (typeof profile.behaviour?.mistypeChance === 'number') {
    humanConfig.mistype_chance = profile.behaviour.mistypeChance;
  }
  if (typeof profile.behaviour?.typingDelay === 'number') {
    humanConfig.typing_delay = profile.behaviour.typingDelay;
  }
  if (profile.behaviour?.idleBetweenActions) {
    humanConfig.idle_between_actions = true;
  }

  return {
    args: buildFingerprintArgs(profile),
    // geoip only makes sense behind a proxy; without one the wrapper would
    // resolve the host's own IP, which is not what "follow the proxy" means.
    geoip: usesIpLocale && hasProxy,
    timezone: profile.locale?.mode === 'manual' ? profile.locale.timezone : undefined,
    locale: profile.locale?.mode === 'manual' ? profile.locale.locale : undefined,
    headless: profile.startup?.headless ?? false,
    humanize: profile.behaviour?.humanize ?? false,
    humanPreset: profile.behaviour?.preset ?? 'default',
    humanConfig: Object.keys(humanConfig).length ? humanConfig : undefined,
    extensionPaths: profile.startup?.extensionPaths ?? [],
    userAgent: profile.userAgent || undefined,
    proxy: buildProxyOption(profile),
  };
}
