/**
 * Factory defaults and realistic fingerprint pools.
 *
 * The stealth binary derives a coherent identity from `--fingerprint=<seed>`
 * on its own, so the pools here are only used when the user explicitly pins a
 * value (Manual mode) or asks the app to randomise a plausible device.
 */

import type {
  AppSettings,
  BehaviourConfig,
  FingerprintConfig,
  FingerprintPlatform,
  GeoConfig,
  LocaleConfig,
  Profile,
  ProxyConfig,
  StartupConfig,
} from './types';

// ---------------------------------------------------------------------------
// Pools
// ---------------------------------------------------------------------------

/** Common desktop resolutions, weighted towards what real users actually run. */
export const SCREEN_POOL: Record<FingerprintPlatform, Array<[number, number]>> = {
  windows: [
    [1920, 1080],
    [1920, 1080],
    [1920, 1080],
    [1536, 864],
    [1366, 768],
    [2560, 1440],
    [1440, 900],
    [1600, 900],
    [3840, 2160],
  ],
  macos: [
    [1440, 900],
    [1512, 982],
    [1728, 1117],
    [1680, 1050],
    [2560, 1440],
    [1920, 1080],
  ],
  linux: [
    [1920, 1080],
    [1920, 1080],
    [1366, 768],
    [2560, 1440],
    [1600, 900],
  ],
};

/** GPU vendor/renderer pairs that actually ship together. */
export const GPU_POOL: Record<FingerprintPlatform, Array<{ vendor: string; renderer: string }>> = {
  windows: [
    {
      vendor: 'Google Inc. (NVIDIA)',
      renderer:
        'ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)',
    },
    {
      vendor: 'Google Inc. (NVIDIA)',
      renderer:
        'ANGLE (NVIDIA, NVIDIA GeForce GTX 1650 Direct3D11 vs_5_0 ps_5_0, D3D11)',
    },
    {
      vendor: 'Google Inc. (Intel)',
      renderer: 'ANGLE (Intel, Intel(R) UHD Graphics 630 Direct3D11 vs_5_0 ps_5_0, D3D11)',
    },
    {
      vendor: 'Google Inc. (Intel)',
      renderer: 'ANGLE (Intel, Intel(R) Iris(R) Xe Graphics Direct3D11 vs_5_0 ps_5_0, D3D11)',
    },
    {
      vendor: 'Google Inc. (AMD)',
      renderer: 'ANGLE (AMD, AMD Radeon RX 6600 Direct3D11 vs_5_0 ps_5_0, D3D11)',
    },
  ],
  macos: [
    { vendor: 'Apple Inc.', renderer: 'Apple M1' },
    { vendor: 'Apple Inc.', renderer: 'Apple M2' },
    { vendor: 'Apple Inc.', renderer: 'Apple M3' },
    { vendor: 'Apple Inc.', renderer: 'ANGLE (Apple, Apple M1 Pro, OpenGL 4.1)' },
    { vendor: 'Intel Inc.', renderer: 'Intel(R) Iris(TM) Plus Graphics 640' },
  ],
  linux: [
    {
      vendor: 'Google Inc. (NVIDIA Corporation)',
      renderer: 'ANGLE (NVIDIA Corporation, NVIDIA GeForce RTX 3060/PCIe/SSE2, OpenGL 4.5.0)',
    },
    {
      vendor: 'Google Inc. (Intel)',
      renderer: 'ANGLE (Intel, Mesa Intel(R) UHD Graphics 620 (KBL GT2), OpenGL 4.6)',
    },
    {
      vendor: 'Google Inc. (AMD)',
      renderer: 'ANGLE (AMD, AMD Radeon Graphics (radeonsi), OpenGL 4.6)',
    },
  ],
};

export const CPU_POOL = [4, 6, 8, 8, 8, 12, 16];
export const MEMORY_POOL = [4, 8, 8, 8, 16, 16, 32];

/** Client Hints platform versions per target OS. */
export const PLATFORM_VERSION_POOL: Record<FingerprintPlatform, string[]> = {
  // Windows 10 reports 10.0.0; Windows 11 reports 13.0.0+ in Client Hints.
  windows: ['10.0.0', '15.0.0', '19.0.0'],
  macos: ['14.5.0', '15.1.0', '15.3.0'],
  linux: ['6.6.0', '6.8.0'],
};

/** A small, curated locale/timezone set for quick manual pinning. */
export const LOCALE_PRESETS: Array<{ label: string; locale: string; timezone: string }> = [
  { label: 'United States (New York)', locale: 'en-US', timezone: 'America/New_York' },
  { label: 'United States (Los Angeles)', locale: 'en-US', timezone: 'America/Los_Angeles' },
  { label: 'United States (Chicago)', locale: 'en-US', timezone: 'America/Chicago' },
  { label: 'United Kingdom (London)', locale: 'en-GB', timezone: 'Europe/London' },
  { label: 'Germany (Berlin)', locale: 'de-DE', timezone: 'Europe/Berlin' },
  { label: 'France (Paris)', locale: 'fr-FR', timezone: 'Europe/Paris' },
  { label: 'Netherlands (Amsterdam)', locale: 'nl-NL', timezone: 'Europe/Amsterdam' },
  { label: 'Spain (Madrid)', locale: 'es-ES', timezone: 'Europe/Madrid' },
  { label: 'Italy (Rome)', locale: 'it-IT', timezone: 'Europe/Rome' },
  { label: 'Poland (Warsaw)', locale: 'pl-PL', timezone: 'Europe/Warsaw' },
  { label: 'Türkiye (Istanbul)', locale: 'tr-TR', timezone: 'Europe/Istanbul' },
  { label: 'Brazil (São Paulo)', locale: 'pt-BR', timezone: 'America/Sao_Paulo' },
  { label: 'Canada (Toronto)', locale: 'en-CA', timezone: 'America/Toronto' },
  { label: 'Australia (Sydney)', locale: 'en-AU', timezone: 'Australia/Sydney' },
  { label: 'India (Kolkata)', locale: 'en-IN', timezone: 'Asia/Kolkata' },
  { label: 'Singapore', locale: 'en-SG', timezone: 'Asia/Singapore' },
  { label: 'Japan (Tokyo)', locale: 'ja-JP', timezone: 'Asia/Tokyo' },
  { label: 'UAE (Dubai)', locale: 'ar-AE', timezone: 'Asia/Dubai' },
];

/** Profile row accent colours. */
export const PROFILE_COLORS = [
  '#6366f1',
  '#8b5cf6',
  '#ec4899',
  '#f43f5e',
  '#f97316',
  '#eab308',
  '#22c55e',
  '#14b8a6',
  '#0ea5e9',
  '#64748b',
];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function pick<T>(arr: readonly T[]): T {
  return arr[Math.floor(Math.random() * arr.length)]!;
}

/** Fingerprint seeds stay in the 5-digit range the wrapper itself uses. */
export function randomSeed(): number {
  return Math.floor(Math.random() * 90000) + 10000;
}

// ---------------------------------------------------------------------------
// Factory defaults
// ---------------------------------------------------------------------------

export function defaultFingerprint(platform: FingerprintPlatform = 'windows'): FingerprintConfig {
  return {
    seed: randomSeed(),
    platform,
    platformVersion: undefined,
    brand: 'Chrome',
    brandVersion: undefined,
    // Auto everywhere: the binary keeps seed-derived values mutually coherent,
    // which is far safer than a human guessing a combination that doesn't exist.
    screen: { mode: 'auto' },
    gpu: { mode: 'auto' },
    cpuCores: { mode: 'auto' },
    deviceMemory: { mode: 'auto' },
    // 5000 MB presents as a regular (non-incognito) profile; the binary's
    // normalised default reads as incognito to quota-based detectors.
    storageQuotaMb: 5000,
    noise: true,
    windowsFontMetrics: false,
    fontsDir: undefined,
    allowThirdPartyCookies: false,
    webrtc: { mode: 'auto' },
    taskbarHeight: undefined,
  };
}

/** Randomise a plausible, internally coherent device for the given platform. */
export function randomFingerprint(platform: FingerprintPlatform = 'windows'): FingerprintConfig {
  const base = defaultFingerprint(platform);
  const [w, h] = pick(SCREEN_POOL[platform]);
  const gpu = pick(GPU_POOL[platform]);
  return {
    ...base,
    seed: randomSeed(),
    platformVersion: pick(PLATFORM_VERSION_POOL[platform]),
    screen: { mode: 'manual', width: w, height: h },
    gpu: { mode: 'manual', vendor: gpu.vendor, renderer: gpu.renderer },
    cpuCores: { mode: 'manual', value: pick(CPU_POOL) },
    deviceMemory: { mode: 'manual', value: pick(MEMORY_POOL) },
  };
}

export function defaultProxy(): ProxyConfig {
  return { kind: 'none' };
}

export function defaultLocale(): LocaleConfig {
  // Following the proxy exit IP is the only setting that can never contradict
  // the network layer, so it is the default.
  return { mode: 'ip' };
}

export function defaultGeo(): GeoConfig {
  return { mode: 'ip' };
}

export function defaultBehaviour(): BehaviourConfig {
  return { humanize: true, preset: 'default', idleBetweenActions: false };
}

export function defaultStartup(): StartupConfig {
  return { startUrls: [], headless: false, extensionPaths: [], extraArgs: [] };
}

export function defaultSettings(): AppSettings {
  return {
    releaseChannel: 'stable',
    maxConcurrentSessions: 5,
    saveCookiesOnClose: true,
    closeSessionsOnQuit: true,
    theme: 'dark',
    defaultPlatform: 'windows',
  };
}

export function newProfile(
  name: string,
  platform: FingerprintPlatform = 'windows',
  id: string = cryptoId(),
): Profile {
  const now = Date.now();
  return {
    id,
    name,
    tags: [],
    color: pick(PROFILE_COLORS),
    createdAt: now,
    updatedAt: now,
    fingerprint: defaultFingerprint(platform),
    proxy: defaultProxy(),
    locale: defaultLocale(),
    geo: defaultGeo(),
    behaviour: defaultBehaviour(),
    startup: defaultStartup(),
  };
}

/**
 * URL-safe random id. Uses WebCrypto when available (main + renderer both have
 * it on Node 20 / Electron), else falls back to Math.random.
 */
export function cryptoId(): string {
  const g = globalThis as { crypto?: { randomUUID?: () => string } };
  if (g.crypto?.randomUUID) return g.crypto.randomUUID().replace(/-/g, '').slice(0, 16);
  return Math.random().toString(36).slice(2, 10) + Math.random().toString(36).slice(2, 10);
}
