/**
 * Preload bridge.
 *
 * The renderer runs with `contextIsolation: true` and `nodeIntegration: false`,
 * so this file is the entire surface it can reach. Every method is an explicit
 * named call rather than a generic `invoke(channel, ...args)` passthrough — a
 * generic bridge would let any injected script in a rendered page reach every
 * channel, which defeats the point of isolation.
 *
 * `unwrap()` turns the `Result<T>` that main returns into either a value or a
 * thrown `Error`, so renderer code can use ordinary try/catch instead of
 * checking `.ok` at fifty call sites.
 */

import { contextBridge, ipcRenderer } from 'electron';
import { IPC } from '../shared/ipc';
import type {
  AppSettings,
  AutomationEndpoint,
  AutomationSettings,
  AutomationState,
  BinaryState,
  CookieValidation,
  DiscoveredBrowserProfile,
  LicenseState,
  Profile,
  ProfileRow,
  ProxyCheckResult,
  ProxyConfig,
  Result,
  SavedProxy,
  SessionInfo,
  SessionLogEntry,
} from '../shared/types';

async function call<T>(channel: string, ...args: unknown[]): Promise<T> {
  const res = (await ipcRenderer.invoke(channel, ...args)) as Result<T>;
  if (!res || typeof res !== 'object' || !('ok' in res)) {
    throw new Error('The application backend returned an unexpected response.');
  }
  if (!res.ok) throw new Error(res.error);
  return res.data;
}

/** Subscribe to a main→renderer event; returns an unsubscribe function. */
function on<T>(channel: string, cb: (payload: T) => void): () => void {
  const listener = (_e: unknown, payload: T): void => cb(payload);
  ipcRenderer.on(channel, listener as never);
  return () => ipcRenderer.removeListener(channel, listener as never);
}

export interface AppInfo {
  version: string;
  platform: string;
  arch: string;
  electron: string;
  chrome: string;
  node: string;
  userData: string;
  profilesDir: string;
  localePresets: Array<{ label: string; locale: string; timezone: string }>;
}

export interface LaunchPreview {
  args: string[];
  proxy: string;
  geoip: boolean;
  headless: boolean;
}

export type LicenseView = LicenseState & { seatHint: number | null };

export interface ParsedProxyList {
  proxies: ProxyConfig[];
  failed: Array<{ line: number; text: string }>;
}

export interface BinaryProgress {
  state: 'downloading' | 'done' | 'error';
}

const api = {
  profiles: {
    list: (): Promise<ProfileRow[]> => call(IPC.PROFILES_LIST),
    get: (id: string): Promise<Profile | undefined> => call(IPC.PROFILES_GET, id),
    create: (partial?: Partial<Profile>): Promise<Profile> => call(IPC.PROFILES_CREATE, partial),
    update: (id: string, patch: Partial<Profile>): Promise<Profile> => call(IPC.PROFILES_UPDATE, id, patch),
    remove: (id: string, deleteData = true): Promise<boolean> => call(IPC.PROFILES_DELETE, id, deleteData),
    duplicate: (id: string, opts?: { newSeed?: boolean; copyCookies?: boolean }): Promise<Profile> =>
      call(IPC.PROFILES_DUPLICATE, id, opts),
    exportToFile: (ids?: string[]): Promise<{ file: string; count: number } | null> =>
      call(IPC.PROFILES_EXPORT, ids),
    importFromFile: (): Promise<{ imported: number; skipped: number } | null> => call(IPC.PROFILES_IMPORT),
    randomizeFingerprint: (id: string): Promise<Profile> => call(IPC.PROFILES_RANDOMIZE_FP, id),
    openDir: (id: string): Promise<void> => call(IPC.PROFILES_OPEN_DIR, id),
    previewArgs: (profile: Profile): Promise<LaunchPreview> => call(IPC.PROFILES_PREVIEW_ARGS, profile),
  },

  sessions: {
    start: (id: string): Promise<void> => call(IPC.SESSION_START, id),
    stop: (id: string): Promise<void> => call(IPC.SESSION_STOP, id),
    stopAll: (): Promise<void> => call(IPC.SESSION_STOP_ALL),
    list: (): Promise<SessionInfo[]> => call(IPC.SESSION_LIST),
    logs: (id: string): Promise<SessionLogEntry[]> => call(IPC.SESSION_LOGS, id),
  },

  cookies: {
    pickFiles: (): Promise<string[]> => call(IPC.COOKIES_PICK_FILES),
    validateFile: (filePath: string): Promise<CookieValidation> => call(IPC.COOKIES_VALIDATE_FILE, filePath),
    validateText: (text: string): Promise<CookieValidation> => call(IPC.COOKIES_VALIDATE_TEXT, text),
    importFiles: (
      profileId: string,
      filePaths: string[],
      opts?: { replace?: boolean; domain?: string },
    ): Promise<{ count: number; files: number; authHints: string[] }> =>
      call(IPC.COOKIES_IMPORT_FILES, profileId, filePaths, opts),
    importText: (
      profileId: string,
      text: string,
      opts?: { replace?: boolean; domain?: string },
    ): Promise<{ count: number; authHints: string[] }> => call(IPC.COOKIES_IMPORT_TEXT, profileId, text, opts),
    exportToFile: (profileId: string, format: 'json' | 'netscape'): Promise<{ file: string; count: number } | null> =>
      call(IPC.COOKIES_EXPORT, profileId, format),
    clear: (profileId: string): Promise<void> => call(IPC.COOKIES_CLEAR, profileId),
    summary: (profileId: string): Promise<{ count: number; domains: string[] }> =>
      call(IPC.COOKIES_SUMMARY, profileId),
  },

  proxies: {
    list: (): Promise<SavedProxy[]> => call(IPC.PROXY_LIST),
    add: (config: ProxyConfig, name?: string): Promise<SavedProxy> => call(IPC.PROXY_ADD, config, name),
    addBulk: (text: string): Promise<{ added: number; failed: Array<{ line: number; text: string }> }> =>
      call(IPC.PROXY_ADD_BULK, text),
    update: (id: string, patch: Partial<SavedProxy>): Promise<SavedProxy> => call(IPC.PROXY_UPDATE, id, patch),
    remove: (id: string): Promise<boolean> => call(IPC.PROXY_DELETE, id),
    check: (config: ProxyConfig): Promise<ProxyCheckResult> => call(IPC.PROXY_CHECK, config),
    checkSaved: (id: string): Promise<ProxyCheckResult> => call(IPC.PROXY_CHECK_SAVED, id),
    parse: (text: string): Promise<ParsedProxyList> => call(IPC.PROXY_PARSE, text),
    rotate: (url: string): Promise<{ ok: boolean; status?: number; error?: string }> => call(IPC.PROXY_ROTATE, url),
  },

  license: {
    state: (refresh = true): Promise<LicenseView> => call(IPC.LICENSE_STATE, refresh),
    activate: (key: string): Promise<LicenseView> => call(IPC.LICENSE_ACTIVATE, key),
    signInWithGithub: (): Promise<void> => call(IPC.LICENSE_SIGN_IN_GITHUB),
    logout: (): Promise<void> => call(IPC.LICENSE_LOGOUT),
    openPricing: (): Promise<void> => call(IPC.LICENSE_OPEN_PRICING),
  },

  binary: {
    state: (): Promise<BinaryState> => call(IPC.BINARY_STATE),
    download: (): Promise<BinaryState> => call(IPC.BINARY_DOWNLOAD),
  },

  importer: {
    discover: (): Promise<DiscoveredBrowserProfile[]> => call(IPC.IMPORT_DISCOVER),
    importProfile: (req: {
      sourcePath: string;
      browser: string;
      name?: string;
      copyData: boolean;
    }): Promise<{ profileId: string; copied: number; skipped: number; warning?: string }> =>
      call(IPC.IMPORT_BROWSER_PROFILE, req),
  },

  settings: {
    get: (): Promise<AppSettings> => call(IPC.SETTINGS_GET),
    update: (patch: Partial<AppSettings>): Promise<AppSettings> => call(IPC.SETTINGS_UPDATE, patch),
  },

  automation: {
    state: (): Promise<AutomationState> => call(IPC.AUTOMATION_STATE),
    set: (patch: Partial<AutomationSettings>): Promise<AutomationState> =>
      call(IPC.AUTOMATION_SET, patch),
    rotateToken: (): Promise<AutomationState> => call(IPC.AUTOMATION_ROTATE_TOKEN),
    endpoint: (profileId: string): Promise<AutomationEndpoint | undefined> =>
      call(IPC.AUTOMATION_ENDPOINT, profileId),
  },

  app: {
    info: (): Promise<AppInfo> => call(IPC.APP_INFO),
    pickDir: (): Promise<string | null> => call(IPC.APP_PICK_DIR),
    openExternal: (url: string): Promise<void> => call(IPC.APP_OPEN_EXTERNAL, url),
    openPath: (target: string): Promise<void> => call(IPC.APP_OPEN_PATH, target),
  },

  events: {
    onSessions: (cb: (sessions: SessionInfo[]) => void) => on<SessionInfo[]>(IPC.EVT_SESSIONS, cb),
    onLog: (cb: (entry: SessionLogEntry) => void) => on<SessionLogEntry>(IPC.EVT_LOG, cb),
    onProfilesChanged: (cb: (profiles: ProfileRow[]) => void) => on<ProfileRow[]>(IPC.EVT_PROFILES_CHANGED, cb),
    onBinaryProgress: (cb: (p: BinaryProgress) => void) => on<BinaryProgress>(IPC.EVT_BINARY_PROGRESS, cb),
  },
} as const;

export type HubApi = typeof api;

contextBridge.exposeInMainWorld('hub', api);
