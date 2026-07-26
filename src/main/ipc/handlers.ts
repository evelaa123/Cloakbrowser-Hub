/**
 * IPC handlers — the only bridge between the renderer and the machine.
 *
 * Every handler returns a `Result<T>` so the renderer never has to deal with a
 * thrown exception across the IPC boundary: an unhandled rejection there
 * surfaces as an opaque "Error invoking remote method", which tells the user
 * nothing. `wrap()` converts throws into a readable message instead.
 */

import fs from 'node:fs';
import path from 'node:path';
import { BrowserWindow, app, dialog, ipcMain, shell } from 'electron';
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
} from '../../shared/types';
import { IPC } from '../../shared/ipc';
import {
  LOCALE_PRESETS,
  automationToken,
  defaultAutomation,
  defaultFingerprint,
  randomFingerprint,
} from '../../shared/defaults';
import { buildFingerprintArgs, proxyLabel, resolveLaunch } from '../../shared/fingerprint-args';
import { Profiles, Proxies, Settings, profileDataDir } from '../services/repos';
import { paths } from '../services/paths';
import {
  exportJar,
  importCookieFiles,
  importCookieText,
  readJar,
  validateCookieFile,
  validateCookieText,
  writeJar,
} from '../services/cookies';
import {
  binaryState,
  ensureBinaryDownloaded,
  licenseState,
  openGithubSignIn,
  openPricing,
  clearKey,
  saveKey,
  validateKey,
  planSeatHint,
} from '../services/license';
import { checkProxy, parseProxyList, rotateProxy } from '../services/proxy';
import { AutomationServer } from '../services/automation';
import { Sessions } from '../browser/session-manager';
import { COOKIE_FILE_FILTERS, cloneProfileData, discoverProfiles, readChromiumHints } from '../importers/browser-profiles';

type Handler<A extends unknown[], R> = (...args: A) => Promise<R> | R;

/** Register a handler whose throws become `{ ok: false, error }`. */
function handle<A extends unknown[], R>(channel: string, fn: Handler<A, R>): void {
  ipcMain.handle(channel, async (_e, ...args: unknown[]): Promise<Result<R>> => {
    try {
      const data = await fn(...(args as A));
      return { ok: true, data };
    } catch (e) {
      const message = String((e as Error)?.message ?? e).split('\n')[0] ?? 'Unexpected error';
      console.error(`[ipc] ${channel} failed:`, e);
      return { ok: false, error: message };
    }
  });
}

function mainWindow(): BrowserWindow | undefined {
  return BrowserWindow.getAllWindows()[0];
}

function broadcast(channel: string, payload?: unknown): void {
  for (const win of BrowserWindow.getAllWindows()) {
    if (!win.isDestroyed()) win.webContents.send(channel, payload);
  }
}

/** Attach live runtime status to each stored profile. */
function rows(): ProfileRow[] {
  return Profiles.all().map((p) => ({
    ...p,
    status: Sessions.status(p.id),
    statusMessage: Sessions.statusMessage(p.id),
  }));
}

function notifyProfiles(): void {
  broadcast(IPC.EVT_PROFILES_CHANGED, rows());
}

/**
 * The automation server, wired to the same repositories and session manager the
 * UI uses. Sharing them (rather than duplicating logic) is what makes a scripted
 * start behave identically to a clicked one - same fingerprint resolution, same
 * cookie injection, same concurrency guard.
 */
export const automation = new AutomationServer({
  listProfiles: () => Profiles.all(),
  getProfile: (id) => Profiles.get(id),
  createProfile: (partial) => Profiles.create(partial),
  updateProfile: (id, patch) => Profiles.update(id, patch),
  deleteProfile: (id, deleteData) => Profiles.remove(id, { deleteData: deleteData ?? true }),
  startSession: (id) => Sessions.start(id),
  stopSession: async (id) => {
    await Sessions.stop(id);
  },
  endpoint: (id) => Sessions.endpoint(id),
  isRunning: (id) => Sessions.isRunning(id),
  log: (message) => console.log(`[automation] ${message}`),
});

function automationState(): AutomationState {
  const settings = Settings.get().automation ?? defaultAutomation();
  return {
    settings,
    listening: automation.running,
    baseUrl: `http://127.0.0.1:${automation.port ?? settings.port}`,
  };
}

/**
 * In-flight binary download, shared by every caller.
 *
 * There are now three ways to trigger a download — the License button, the
 * auto-fetch after activating a key, and launching a profile with no binary
 * yet. Two concurrent `ensureBinary()` calls would unpack into the same cache
 * directory at once and can corrupt the install, and starting several profiles
 * at once is exactly the case where that happens. Collapsing them onto one
 * promise makes the extra callers wait for the first download instead.
 */
let binaryDownload: Promise<BinaryState> | null = null;

function downloadBinaryOnce(): Promise<BinaryState> {
  if (binaryDownload) return binaryDownload;

  const settings = Settings.get();
  broadcast(IPC.EVT_BINARY_PROGRESS, { state: 'downloading' });

  binaryDownload = (async () => {
    try {
      await ensureBinaryDownloaded(settings.browserVersion, settings.releaseChannel);
      const state = await binaryState(settings.browserVersion, settings.releaseChannel);
      broadcast(IPC.EVT_BINARY_PROGRESS, { state: 'done' });
      return state;
    } catch (e) {
      broadcast(IPC.EVT_BINARY_PROGRESS, { state: 'error' });
      throw e;
    } finally {
      // Cleared on failure too, so a network blip does not wedge every later
      // attempt onto the same rejected promise.
      binaryDownload = null;
    }
  })();

  return binaryDownload;
}

/** Refresh a profile's cookie metadata from the jar on disk. */
function refreshCookieMeta(profileId: string, source: 'import' | 'session' | 'manual'): Profile | undefined {
  const cookies = readJar(paths.cookieJar(profileId));
  const domains = new Set(cookies.map((c) => (c.domain ?? '').replace(/^\./, '')).filter(Boolean));
  return Profiles.update(profileId, {
    cookies: { count: cookies.length, domains: domains.size, updatedAt: Date.now(), source },
  });
}

export function registerIpcHandlers(): void {
  // -------------------------------------------------------------------------
  // Profiles
  // -------------------------------------------------------------------------

  handle<[], ProfileRow[]>(IPC.PROFILES_LIST, () => rows());

  handle<[string], Profile | undefined>(IPC.PROFILES_GET, (id) => Profiles.get(id));

  handle<[Partial<Profile> | undefined], Profile>(IPC.PROFILES_CREATE, (partial) => {
    // The renderer may send nothing at all; the default platform is a setting,
    // so it is resolved here rather than duplicated in the UI.
    const platform = partial?.fingerprint?.platform ?? Settings.get().defaultPlatform;
    const created = Profiles.create({
      ...partial,
      fingerprint: partial?.fingerprint ?? defaultFingerprint(platform),
    });
    notifyProfiles();
    return created;
  });

  handle<[string, Partial<Profile>], Profile>(IPC.PROFILES_UPDATE, (id, patch) => {
    const updated = Profiles.update(id, patch);
    if (!updated) throw new Error('Profile not found.');
    notifyProfiles();
    return updated;
  });

  handle<[string, boolean | undefined], boolean>(IPC.PROFILES_DELETE, async (id, deleteData) => {
    // Deleting a profile whose browser is open would leave an orphan process
    // holding the folder, so the session is closed first.
    if (Sessions.isRunning(id)) await Sessions.stop(id);
    const removed = Profiles.remove(id, { deleteData: deleteData ?? true });
    if (!removed) throw new Error('Profile not found.');
    notifyProfiles();
    return removed;
  });

  handle<[string, { newSeed?: boolean; copyCookies?: boolean } | undefined], Profile>(
    IPC.PROFILES_DUPLICATE,
    (id, opts) => {
      const clone = Profiles.duplicate(id, opts ?? {});
      if (!clone) throw new Error('Profile not found.');
      notifyProfiles();
      return clone;
    },
  );

  handle<[string[] | undefined], { file: string; count: number } | null>(IPC.PROFILES_EXPORT, async (ids) => {
    const payload = Profiles.export(ids);
    const win = mainWindow();
    const res = await dialog.showSaveDialog(win!, {
      title: 'Export profiles',
      defaultPath: `cloakbrowser-profiles-${new Date().toISOString().slice(0, 10)}.json`,
      filters: [{ name: 'JSON', extensions: ['json'] }],
    });
    if (res.canceled || !res.filePath) return null;
    fs.writeFileSync(res.filePath, JSON.stringify(payload, null, 2), 'utf-8');
    return { file: res.filePath, count: payload.profiles.length };
  });

  handle<[], { imported: number; skipped: number } | null>(IPC.PROFILES_IMPORT, async () => {
    const win = mainWindow();
    const res = await dialog.showOpenDialog(win!, {
      title: 'Import profiles',
      properties: ['openFile'],
      filters: [{ name: 'JSON', extensions: ['json'] }],
    });
    if (res.canceled || !res.filePaths.length) return null;
    const data = JSON.parse(fs.readFileSync(res.filePaths[0]!, 'utf-8'));
    const out = Profiles.import(data);
    notifyProfiles();
    return out;
  });

  handle<[string], Profile>(IPC.PROFILES_RANDOMIZE_FP, (id) => {
    const profile = Profiles.get(id);
    if (!profile) throw new Error('Profile not found.');
    const updated = Profiles.update(id, { fingerprint: randomFingerprint(profile.fingerprint.platform) });
    notifyProfiles();
    return updated!;
  });

  handle<[string], void>(IPC.PROFILES_OPEN_DIR, async (id) => {
    const dir = profileDataDir(id);
    const err = await shell.openPath(dir);
    if (err) throw new Error(err);
  });

  /** Preview the exact flags and options a profile will launch with. */
  handle<[Profile], { args: string[]; proxy: string; geoip: boolean; headless: boolean }>(
    IPC.PROFILES_PREVIEW_ARGS,
    (profile) => {
      const resolved = resolveLaunch(profile);
      return {
        args: buildFingerprintArgs(profile),
        proxy: proxyLabel(profile),
        geoip: resolved.geoip,
        headless: resolved.headless,
      };
    },
  );

  // -------------------------------------------------------------------------
  // Sessions
  // -------------------------------------------------------------------------

  handle<[string], void>(IPC.SESSION_START, async (id) => {
    // First launch on a fresh install has no binary yet. Fetch it here rather
    // than failing with "go to the License tab": the user asked to start a
    // profile, and the download is a precondition of that, not a separate
    // chore. The renderer shows a modal while EVT_BINARY_PROGRESS is
    // 'downloading', so this is not a silent multi-minute hang.
    const settings = Settings.get();
    const binary = await binaryState(settings.browserVersion, settings.releaseChannel);
    if (!binary.installed) {
      await downloadBinaryOnce();
    }

    const res = await Sessions.start(id);
    if (!res.ok) throw new Error(res.error);
    notifyProfiles();
  });

  handle<[string], void>(IPC.SESSION_STOP, async (id) => {
    const res = await Sessions.stop(id);
    if (!res.ok) throw new Error(res.error);
    notifyProfiles();
  });

  handle<[], void>(IPC.SESSION_STOP_ALL, async () => {
    await Sessions.stopAll();
    notifyProfiles();
  });

  handle<[], SessionInfo[]>(IPC.SESSION_LIST, () => Sessions.list());

  handle<[string], SessionLogEntry[]>(IPC.SESSION_LOGS, (id) => Sessions.logsFor(id));

  // -------------------------------------------------------------------------
  // Cookies
  // -------------------------------------------------------------------------

  handle<[], string[]>(IPC.COOKIES_PICK_FILES, async () => {
    const win = mainWindow();
    const res = await dialog.showOpenDialog(win!, {
      title: 'Select cookie files',
      properties: ['openFile', 'multiSelections'],
      filters: COOKIE_FILE_FILTERS,
    });
    return res.canceled ? [] : res.filePaths;
  });

  handle<[string], CookieValidation>(IPC.COOKIES_VALIDATE_FILE, (filePath) => {
    if (!filePath) throw new Error('No file selected.');
    return validateCookieFile(filePath);
  });

  handle<[string], CookieValidation>(IPC.COOKIES_VALIDATE_TEXT, (text) => validateCookieText(text ?? ''));

  handle<[string, string[], { replace?: boolean; domain?: string } | undefined], { count: number; files: number; authHints: string[] }>(
    IPC.COOKIES_IMPORT_FILES,
    (profileId, filePaths, opts) => {
      if (!Profiles.get(profileId)) throw new Error('Profile not found.');
      if (!filePaths?.length) throw new Error('No files selected.');
      const out = importCookieFiles(filePaths, paths.cookieJar(profileId), opts ?? {});
      if (out.count === 0) {
        throw new Error('No cookies could be read from those files. Check the export format.');
      }
      refreshCookieMeta(profileId, 'import');
      notifyProfiles();
      return { count: out.count, files: out.files, authHints: out.authHints };
    },
  );

  handle<[string, string, { replace?: boolean; domain?: string } | undefined], { count: number; authHints: string[] }>(
    IPC.COOKIES_IMPORT_TEXT,
    (profileId, text, opts) => {
      if (!Profiles.get(profileId)) throw new Error('Profile not found.');
      const out = importCookieText(text ?? '', paths.cookieJar(profileId), opts ?? {});
      if (out.count === 0) {
        throw new Error('No cookies could be read from that text. Paste a JSON export, a cookies.txt file, or a Cookie: header.');
      }
      refreshCookieMeta(profileId, 'import');
      notifyProfiles();
      return { count: out.count, authHints: out.authHints };
    },
  );

  handle<[string, 'json' | 'netscape'], { file: string; count: number } | null>(
    IPC.COOKIES_EXPORT,
    async (profileId, format) => {
      const profile = Profiles.get(profileId);
      if (!profile) throw new Error('Profile not found.');
      const ext = format === 'json' ? 'json' : 'txt';
      const safeName = profile.name.replace(/[^a-z0-9_-]+/gi, '-').toLowerCase();
      const win = mainWindow();
      const res = await dialog.showSaveDialog(win!, {
        title: 'Export cookies',
        defaultPath: `cookies-${safeName}.${ext}`,
        filters: [{ name: format === 'json' ? 'JSON' : 'Netscape cookies.txt', extensions: [ext] }],
      });
      if (res.canceled || !res.filePath) return null;
      const count = exportJar(paths.cookieJar(profileId), res.filePath, format);
      return { file: res.filePath, count };
    },
  );

  handle<[string], void>(IPC.COOKIES_CLEAR, (profileId) => {
    writeJar(paths.cookieJar(profileId), []);
    Profiles.update(profileId, { cookies: { count: 0, domains: 0, updatedAt: Date.now(), source: 'manual' } });
    notifyProfiles();
  });

  handle<[string], { count: number; domains: string[] }>(IPC.COOKIES_SUMMARY, (profileId) => {
    const cookies = readJar(paths.cookieJar(profileId));
    const domains = [...new Set(cookies.map((c) => (c.domain ?? '').replace(/^\./, '')).filter(Boolean))].sort();
    return { count: cookies.length, domains };
  });

  // -------------------------------------------------------------------------
  // Proxies
  // -------------------------------------------------------------------------

  handle<[], SavedProxy[]>(IPC.PROXY_LIST, () => Proxies.all());

  handle<[ProxyConfig, string | undefined], SavedProxy>(IPC.PROXY_ADD, (config, name) => Proxies.add(config, name));

  handle<[string], { added: number; failed: Array<{ line: number; text: string }> }>(IPC.PROXY_ADD_BULK, (text) => {
    const { proxies, failed } = parseProxyList(text ?? '');
    Proxies.addMany(proxies);
    return { added: proxies.length, failed };
  });

  handle<[string, Partial<SavedProxy>], SavedProxy>(IPC.PROXY_UPDATE, (id, patch) => {
    const updated = Proxies.update(id, patch);
    if (!updated) throw new Error('Proxy not found.');
    return updated;
  });

  handle<[string], boolean>(IPC.PROXY_DELETE, (id) => Proxies.remove(id));

  handle<[ProxyConfig], ProxyCheckResult>(IPC.PROXY_CHECK, (config) => checkProxy(config));

  handle<[string], ProxyCheckResult>(IPC.PROXY_CHECK_SAVED, async (id) => {
    const saved = Proxies.get(id);
    if (!saved) throw new Error('Proxy not found.');
    const result = await checkProxy(saved);
    Proxies.update(id, { lastCheck: result });
    return result;
  });

  handle<[string], { proxies: ProxyConfig[]; failed: Array<{ line: number; text: string }> }>(
    IPC.PROXY_PARSE,
    (text) => parseProxyList(text ?? ''),
  );

  handle<[string], { ok: boolean; status?: number; error?: string }>(IPC.PROXY_ROTATE, (url) => rotateProxy(url));

  // -------------------------------------------------------------------------
  // License / binary
  // -------------------------------------------------------------------------

  handle<[boolean | undefined], LicenseState & { seatHint: number | null }>(IPC.LICENSE_STATE, async (refresh) => {
    const state = await licenseState(Sessions.runningCount(), { refresh: refresh ?? true });
    return { ...state, seatHint: planSeatHint(state.plan) };
  });

  handle<[string], LicenseState & { seatHint: number | null }>(IPC.LICENSE_ACTIVATE, async (key) => {
    const trimmed = (key ?? '').trim();
    if (!trimmed) throw new Error('Paste a license key first.');
    const info = await validateKey(trimmed);
    if (info === null) {
      throw new Error('Could not reach the license server. Check your connection and try again.');
    }
    if (!info.valid) throw new Error('That license key is invalid or expired. Nothing was saved.');

    saveKey(trimmed);
    // Align the app's session guard with what the plan actually allows, so the
    // user is not blocked below their entitlement (or allowed far above it).
    const seats = planSeatHint(info.plan);
    if (seats && seats !== Settings.get().maxConcurrentSessions) {
      Settings.update({ maxConcurrentSessions: seats });
    }
    const state = await licenseState(Sessions.runningCount());

    // Start fetching the tier's binary straight away: the key is what decides
    // which build the user gets, so activating it is the natural moment to pull
    // it. Deliberately not awaited — activation should return immediately and
    // the download reports itself through EVT_BINARY_PROGRESS. A failure here
    // must not fail activation (the key is already saved and valid), so the
    // rejection is swallowed; the user can retry from the License tab, and a
    // launch would fetch it anyway.
    void downloadBinaryOnce().catch(() => undefined);

    return { ...state, seatHint: seats };
  });

  handle<[], void>(IPC.LICENSE_SIGN_IN_GITHUB, () => openGithubSignIn());
  handle<[], void>(IPC.LICENSE_OPEN_PRICING, () => openPricing());

  handle<[], void>(IPC.LICENSE_LOGOUT, () => {
    clearKey();
  });

  handle<[], BinaryState>(IPC.BINARY_STATE, async () => {
    const settings = Settings.get();
    return binaryState(settings.browserVersion, settings.releaseChannel);
  });

  handle<[], BinaryState>(IPC.BINARY_DOWNLOAD, () => downloadBinaryOnce());

  // -------------------------------------------------------------------------
  // Import from local browsers
  // -------------------------------------------------------------------------

  handle<[], DiscoveredBrowserProfile[]>(IPC.IMPORT_DISCOVER, () => discoverProfiles());

  handle<
    [{ sourcePath: string; browser: string; name?: string; copyData: boolean }],
    { profileId: string; copied: number; skipped: number; warning?: string }
  >(IPC.IMPORT_BROWSER_PROFILE, (req) => {
    if (!req?.sourcePath || !fs.existsSync(req.sourcePath)) {
      throw new Error('That browser profile folder could not be found.');
    }

    const hints = readChromiumHints(req.sourcePath);
    const profile = Profiles.create({
      name: req.name?.trim() || `${req.browser} import`,
      notes: `Imported from ${req.browser} (${req.sourcePath})`,
      tags: ['imported', req.browser.toLowerCase()],
      ...(hints.locale ? { locale: { mode: 'manual' as const, locale: hints.locale } } : {}),
    });

    if (!req.copyData) {
      notifyProfiles();
      return { profileId: profile.id, copied: 0, skipped: 0, warning: 'Settings imported. Cookies were not copied.' };
    }

    const result = cloneProfileData(req.sourcePath, profileDataDir(profile.id));
    if (!result.ok) {
      // Roll back the empty profile so a failed import leaves no debris.
      Profiles.remove(profile.id, { deleteData: true });
      throw new Error(result.error ?? 'The profile data could not be copied.');
    }

    notifyProfiles();
    return {
      profileId: profile.id,
      copied: result.copied.length,
      skipped: result.skipped.length,
      warning: result.copied.includes('Cookies')
        ? undefined
        : 'The cookie database was not present in that profile, so no session was carried over.',
    };
  });

  // -------------------------------------------------------------------------
  // Settings / app
  // -------------------------------------------------------------------------

  handle<[], AppSettings>(IPC.SETTINGS_GET, () => Settings.get());

  handle<[Partial<AppSettings>], AppSettings>(IPC.SETTINGS_UPDATE, (patch) => Settings.update(patch));

  // -------------------------------------------------------------------------
  // Automation API
  // -------------------------------------------------------------------------

  handle<[], AutomationState>(IPC.AUTOMATION_STATE, () => automationState());

  handle<[Partial<AutomationSettings>], AutomationState>(IPC.AUTOMATION_SET, async (patch) => {
    const current = Settings.get().automation ?? defaultAutomation();
    const next: AutomationSettings = { ...current, ...patch };

    if (next.enabled) {
      if (!Number.isInteger(next.port) || next.port < 1024 || next.port > 65535) {
        // Ports below 1024 need root on Unix, so rejecting them here turns a
        // confusing EACCES into a clear message.
        throw new Error('Pick a port between 1024 and 65535.');
      }
      // Never let the API come up without a token, even if a caller sent one.
      if (!next.token) next.token = automationToken();
    }

    // Apply before persisting: if the port is taken, the stored settings should
    // not claim the API is enabled when it is not listening.
    await automation.start(next);
    Settings.update({ automation: next });
    return automationState();
  });

  handle<[], AutomationState>(IPC.AUTOMATION_ROTATE_TOKEN, async () => {
    const current = Settings.get().automation ?? defaultAutomation();
    const next: AutomationSettings = { ...current, token: automationToken() };
    // Restart so in-flight clients holding the old token are cut off at once,
    // which is the point of rotating it.
    await automation.start(next);
    Settings.update({ automation: next });
    return automationState();
  });

  handle<[string], AutomationEndpoint | undefined>(IPC.AUTOMATION_ENDPOINT, (id) =>
    Sessions.endpoint(id),
  );

  handle<
    [],
    {
      version: string;
      platform: string;
      arch: string;
      electron: string;
      chrome: string;
      node: string;
      userData: string;
      profilesDir: string;
      localePresets: typeof LOCALE_PRESETS;
    }
  >(IPC.APP_INFO, () => ({
    version: app.getVersion(),
    platform: process.platform,
    arch: process.arch,
    electron: process.versions.electron ?? '',
    chrome: process.versions.chrome ?? '',
    node: process.versions.node,
    userData: paths.root(),
    profilesDir: Settings.get().profilesDir ?? path.join(paths.root(), 'profiles'),
    localePresets: LOCALE_PRESETS,
  }));

  handle<[], string | null>(IPC.APP_PICK_DIR, async () => {
    const win = mainWindow();
    const res = await dialog.showOpenDialog(win!, { properties: ['openDirectory', 'createDirectory'] });
    return res.canceled || !res.filePaths.length ? null : res.filePaths[0]!;
  });

  handle<[string], void>(IPC.APP_OPEN_EXTERNAL, async (url) => {
    // Only ever hand http(s) to the OS: a file:// or custom scheme from the
    // renderer would be an arbitrary-execution hole.
    const parsed = new URL(url);
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') {
      throw new Error('Only http and https links can be opened.');
    }
    await shell.openExternal(parsed.toString());
  });

  handle<[string], void>(IPC.APP_OPEN_PATH, async (target) => {
    const err = await shell.openPath(target);
    if (err) throw new Error(err);
  });

  // -------------------------------------------------------------------------
  // Live events
  // -------------------------------------------------------------------------

  Sessions.on('sessions', (sessions: SessionInfo[]) => {
    broadcast(IPC.EVT_SESSIONS, sessions);
    notifyProfiles();
  });
  Sessions.on('log', (entry: SessionLogEntry) => broadcast(IPC.EVT_LOG, entry));
}
