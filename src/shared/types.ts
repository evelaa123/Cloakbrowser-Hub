/**
 * Shared domain model for CloakBrowser Hub.
 *
 * Everything in this file is transferred over IPC, so it must stay
 * JSON-serialisable (no class instances, no Dates, no functions).
 */

// ---------------------------------------------------------------------------
// Fingerprint
// ---------------------------------------------------------------------------

/** Target OS the profile presents to websites. */
export type FingerprintPlatform = 'windows' | 'macos' | 'linux';

/** Browser brand reported in UA + Client Hints. */
export type BrowserBrand = 'Chrome' | 'Edge' | 'Opera' | 'Vivaldi';

/**
 * A value that is either explicitly pinned by the user or derived
 * deterministically from the fingerprint seed by the stealth binary.
 * `mode: 'auto'` means "let the seed decide" — the recommended default,
 * because the binary keeps all auto values mutually coherent.
 */
export interface AutoOr<T> {
  mode: 'auto' | 'manual';
  value?: T;
}

export interface ScreenConfig {
  mode: 'auto' | 'manual';
  width?: number;
  height?: number;
}

export interface GpuConfig {
  mode: 'auto' | 'manual';
  vendor?: string;
  renderer?: string;
}

export interface GeoConfig {
  /** off = do not touch geolocation, ip = follow proxy exit IP, manual = pinned coords. */
  mode: 'off' | 'ip' | 'manual';
  latitude?: number;
  longitude?: number;
  accuracy?: number;
}

export interface FingerprintConfig {
  /**
   * Master seed (10000-99999 by convention, any positive int works).
   * A fixed seed = a stable device identity across launches, which is what
   * makes a profile look like a returning visitor. Empty/undefined lets the
   * binary roll a fresh random seed on every launch (not recommended for
   * account work).
   */
  seed?: number;
  platform: FingerprintPlatform;
  /** Client Hints platform version, e.g. "10.0.0" for Windows 10. */
  platformVersion?: string;
  brand?: BrowserBrand;
  brandVersion?: string;
  screen: ScreenConfig;
  gpu: GpuConfig;
  /** navigator.hardwareConcurrency */
  cpuCores: AutoOr<number>;
  /** navigator.deviceMemory (GB) */
  deviceMemory: AutoOr<number>;
  /** Storage quota in MB — raise above the normalised default to defeat incognito heuristics. */
  storageQuotaMb?: number;
  /** Disable canvas/WebGL/audio noise while keeping the deterministic seed. */
  noise: boolean;
  /** Windows font metrics alignment (Chromium 148+ binary, Linux host spoofing Windows). */
  windowsFontMetrics: boolean;
  /** Directory with target-platform fonts (Windows fonts on Linux etc.). */
  fontsDir?: string;
  /** Re-enable third-party cookies (needed for some SSO / reCAPTCHA / payment flows). */
  allowThirdPartyCookies: boolean;
  /** WebRTC handling: off = untouched, auto = spoof to proxy exit IP, manual = pinned IP. */
  webrtc: { mode: 'off' | 'auto' | 'manual'; ip?: string };
  /** Taskbar height override (affects window.screen.availHeight coherence). */
  taskbarHeight?: number;
}

// ---------------------------------------------------------------------------
// Proxy
// ---------------------------------------------------------------------------

export type ProxyKind = 'none' | 'http' | 'https' | 'socks5';

export interface ProxyConfig {
  kind: ProxyKind;
  host?: string;
  port?: number;
  username?: string;
  password?: string;
  /** Comma separated hosts that bypass the proxy, e.g. ".google.com". */
  bypass?: string;
  /** Optional URL that rotates the IP for this proxy (GET request). */
  rotationUrl?: string;
  /** Reference to a saved proxy in the proxy library, if this profile uses one. */
  savedProxyId?: string;
}

/** Reusable proxy entry stored in the proxy library. */
export interface SavedProxy extends ProxyConfig {
  id: string;
  name: string;
  createdAt: number;
  lastCheck?: ProxyCheckResult;
}

export interface ProxyCheckResult {
  ok: boolean;
  checkedAt: number;
  ip?: string;
  country?: string;
  countryCode?: string;
  city?: string;
  region?: string;
  timezone?: string;
  latitude?: number;
  longitude?: number;
  latencyMs?: number;
  error?: string;
}

// ---------------------------------------------------------------------------
// Locale / timezone
// ---------------------------------------------------------------------------

export interface LocaleConfig {
  /** ip = derive from proxy exit IP (geoip), manual = pinned value. */
  mode: 'ip' | 'manual';
  /** BCP 47, e.g. "en-US". */
  locale?: string;
  /** IANA zone, e.g. "America/New_York". */
  timezone?: string;
}

// ---------------------------------------------------------------------------
// Behaviour / automation
// ---------------------------------------------------------------------------

export type HumanPreset = 'default' | 'careful';

export interface BehaviourConfig {
  /** Human-like mouse curves, per-character typing, natural scrolling. */
  humanize: boolean;
  preset: HumanPreset;
  /** 0..1 chance of a typo with self-correction. */
  mistypeChance?: number;
  /** ms per character. */
  typingDelay?: number;
  idleBetweenActions?: boolean;
}

export interface StartupConfig {
  /** Pages opened when the session starts. */
  startUrls: string[];
  /** Run without a visible window. Headed is strongly recommended for account work. */
  headless: boolean;
  /** Absolute paths of unpacked Chrome extensions to load. */
  extensionPaths: string[];
  /** Extra raw Chromium CLI flags, one per entry. */
  extraArgs: string[];
}

// ---------------------------------------------------------------------------
// Profile
// ---------------------------------------------------------------------------

export type ProfileStatus = 'idle' | 'starting' | 'running' | 'stopping' | 'error';

export interface Profile {
  id: string;
  name: string;
  /** Free-form notes shown in the profiles table tooltip. */
  notes?: string;
  tags: string[];
  /** Hex colour used for the row accent / avatar. */
  color?: string;
  createdAt: number;
  updatedAt: number;
  lastRunAt?: number;
  fingerprint: FingerprintConfig;
  proxy: ProxyConfig;
  locale: LocaleConfig;
  geo: GeoConfig;
  behaviour: BehaviourConfig;
  startup: StartupConfig;
  /** Cookie jar metadata, updated on import and on session close. */
  cookies?: CookieJarMeta;
  /** Custom user agent override. Empty = let the binary build a coherent UA. */
  userAgent?: string;
}

export interface CookieJarMeta {
  count: number;
  domains: number;
  updatedAt: number;
  /** Where the cookies came from most recently. */
  source: 'import' | 'session' | 'manual';
}

/** Row shape shown in the profiles table (profile + live runtime state). */
export interface ProfileRow extends Profile {
  status: ProfileStatus;
  statusMessage?: string;
}

// ---------------------------------------------------------------------------
// Cookies
// ---------------------------------------------------------------------------

export interface CookieValidation {
  ok: boolean;
  count: number;
  format: 'netscape' | 'json' | 'header' | 'unknown';
  domains: string[];
  /** Cookie names that look like an authenticated session for a known service. */
  authHints: string[];
  suggestedName: string;
  error?: string;
}

// ---------------------------------------------------------------------------
// License
// ---------------------------------------------------------------------------

export type LicenseTier = 'none' | 'free' | 'pro';

export interface LicenseState {
  tier: LicenseTier;
  /** Present but masked for display, e.g. "cb_1234…cdef". */
  maskedKey?: string;
  plan?: string;
  valid: boolean;
  expires?: string | null;
  /** Concurrent sessions currently held by this key (server-reported). */
  activeSessions?: number | null;
  /** Sessions this app currently has open. */
  localSessions: number;
  checkedAt?: number;
  error?: string;
}

export interface BinaryState {
  installed: boolean;
  version?: string;
  platform?: string;
  tier?: 'free' | 'pro';
  path?: string;
  cacheDir?: string;
  /** Newer version available on the server, if known. */
  latest?: string | null;
  error?: string;
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

export interface AppSettings {
  /** Where profile user-data dirs live. Defaults to <userData>/profiles. */
  profilesDir?: string;
  /** stable | preview binary channel. */
  releaseChannel: 'stable' | 'preview';
  /** Pin an exact Chromium version (rollback). */
  browserVersion?: string;
  /** Max sessions the app will start at once (soft guard, mirrors plan seats). */
  maxConcurrentSessions: number;
  /** Save cookies back into the encrypted jar when a session closes. */
  saveCookiesOnClose: boolean;
  /** Close every running session when the app quits. */
  closeSessionsOnQuit: boolean;
  theme: 'dark' | 'light';
  /** Default fingerprint template applied to brand-new profiles. */
  defaultPlatform: FingerprintPlatform;
  /** Local automation HTTP API (Puppeteer/Selenium control). */
  automation: AutomationSettings;
}

// ---------------------------------------------------------------------------
// Automation API
// ---------------------------------------------------------------------------

export interface AutomationSettings {
  enabled: boolean;
  /** TCP port for the local REST API. */
  port: number;
  /**
   * Bearer token required on every request.
   *
   * Generated, never empty while enabled: an unauthenticated endpoint that can
   * launch browsers and hand out CDP URLs is a local privilege-escalation
   * vector for any other process on the machine (including a page's own
   * JavaScript, which can reach 127.0.0.1).
   */
  token: string;
}

/** Settings plus whether the server is actually listening right now. */
export interface AutomationState {
  settings: AutomationSettings;
  /** True only when the HTTP server is bound; false after a failed start. */
  listening: boolean;
  /** Base URL for scripts, e.g. http://127.0.0.1:3777 */
  baseUrl: string;
}

/** What a caller needs to attach Puppeteer/Playwright/Selenium to a session. */
export interface AutomationEndpoint {
  profileId: string;
  profileName: string;
  /** CDP WebSocket URL — `puppeteer.connect({ browserWSEndpoint })`. */
  wsEndpoint: string;
  /** CDP HTTP origin — `http://127.0.0.1:<port>`, for Selenium's debuggerAddress. */
  httpEndpoint: string;
  /** The devtools port Chromium actually bound. */
  port: number;
}

// ---------------------------------------------------------------------------
// Sessions
// ---------------------------------------------------------------------------

export interface SessionInfo {
  profileId: string;
  profileName: string;
  status: ProfileStatus;
  startedAt?: number;
  pages: number;
  message?: string;
}

export interface SessionLogEntry {
  profileId: string;
  at: number;
  level: 'info' | 'warn' | 'error';
  message: string;
}

// ---------------------------------------------------------------------------
// Import
// ---------------------------------------------------------------------------

/** A Chromium/Firefox profile discovered on this machine. */
export interface DiscoveredBrowserProfile {
  browser: string;
  /** Display name, e.g. "Person 1 (Chrome)". */
  name: string;
  path: string;
  /** Whether a Cookies DB was found (encrypted cookies can't always be read). */
  hasCookies: boolean;
  sizeMb?: number;
}

export interface ImportResult {
  ok: boolean;
  profileId?: string;
  cookies?: number;
  message?: string;
}

// ---------------------------------------------------------------------------
// Generic IPC result
// ---------------------------------------------------------------------------

export type Result<T = void> = { ok: true; data: T } | { ok: false; error: string };
