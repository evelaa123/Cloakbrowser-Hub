/**
 * Discover local browser profiles and import their cookies.
 *
 * Reading a Chromium `Cookies` SQLite file directly would need a native sqlite3
 * build *plus* the OS-specific value decryption (DPAPI on Windows, Keychain on
 * macOS, a libsecret-derived AES key on Linux) — three fragile platform paths
 * for a feature the user can accomplish reliably with a one-click cookie export.
 *
 * So the import is split into what is dependable on all three platforms:
 *   1. Discover the installed browsers and their profile folders.
 *   2. Copy the profile's *settings* into a Hub profile (locale, timezone).
 *   3. Copy the whole user-data dir when the user wants a true clone — cookies
 *      included, because Chromium can read its own encrypted DB.
 *   4. For cookies alone, guide the user to an export file (any format) which
 *      the cookie engine already handles losslessly.
 *
 * Option 3 is the one that "keeps the session" without touching encryption at
 * all: the encrypted values travel with the profile and the stealth binary
 * decrypts them the same way the original browser did.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import type { DiscoveredBrowserProfile } from '../../shared/types';

interface BrowserDef {
  name: string;
  /** User-data roots per platform; the first existing one wins. */
  roots: () => string[];
  kind: 'chromium' | 'firefox';
}

const home = () => os.homedir();

/** Chromium-family user-data roots per platform. */
function chromiumRoots(win: string[], mac: string[], linux: string[]): string[] {
  if (process.platform === 'win32') {
    const local = process.env.LOCALAPPDATA || path.join(home(), 'AppData', 'Local');
    const roaming = process.env.APPDATA || path.join(home(), 'AppData', 'Roaming');
    return win.map((p) => p.replace('%LOCALAPPDATA%', local).replace('%APPDATA%', roaming));
  }
  if (process.platform === 'darwin') {
    return mac.map((p) => path.join(home(), p));
  }
  return linux.map((p) => path.join(home(), p));
}

const BROWSERS: BrowserDef[] = [
  {
    name: 'Chrome',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\Google\\Chrome\\User Data'],
        ['Library/Application Support/Google/Chrome'],
        ['.config/google-chrome', 'snap/chromium/common/chromium'],
      ),
  },
  {
    name: 'Edge',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\Microsoft\\Edge\\User Data'],
        ['Library/Application Support/Microsoft Edge'],
        ['.config/microsoft-edge'],
      ),
  },
  {
    name: 'Brave',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\BraveSoftware\\Brave-Browser\\User Data'],
        ['Library/Application Support/BraveSoftware/Brave-Browser'],
        ['.config/BraveSoftware/Brave-Browser'],
      ),
  },
  {
    name: 'Chromium',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\Chromium\\User Data'],
        ['Library/Application Support/Chromium'],
        ['.config/chromium'],
      ),
  },
  {
    name: 'Opera',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%APPDATA%\\Opera Software\\Opera Stable'],
        ['Library/Application Support/com.operasoftware.Opera'],
        ['.config/opera'],
      ),
  },
  {
    name: 'Vivaldi',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\Vivaldi\\User Data'],
        ['Library/Application Support/Vivaldi'],
        ['.config/vivaldi'],
      ),
  },
  {
    name: 'Yandex',
    kind: 'chromium',
    roots: () =>
      chromiumRoots(
        ['%LOCALAPPDATA%\\Yandex\\YandexBrowser\\User Data'],
        ['Library/Application Support/Yandex/YandexBrowser'],
        ['.config/yandex-browser'],
      ),
  },
  {
    name: 'Firefox',
    kind: 'firefox',
    roots: () =>
      chromiumRoots(
        ['%APPDATA%\\Mozilla\\Firefox\\Profiles'],
        ['Library/Application Support/Firefox/Profiles'],
        ['.mozilla/firefox'],
      ),
  },
];

/** Folders inside a Chromium user-data dir that are not profiles. */
const NON_PROFILE_DIRS = new Set([
  'System Profile',
  'Crashpad',
  'GrShaderCache',
  'ShaderCache',
  'GraphiteDawnCache',
  'component_crx_cache',
  'extensions_crx_cache',
  'SwReporter',
  'Safe Browsing',
  'Subresource Filter',
  'WidevineCdm',
  'BrowserMetrics',
  'OptimizationGuidePredictionModels',
  'segmentation_platform',
  'Webstore Downloads',
  'CertificateRevocation',
  'FileTypePolicies',
  'OriginTrials',
  'PKIMetadata',
  'TpcdMetadata',
  'ZxcvbnData',
  'hyphen-data',
]);

function isChromiumProfileDir(dir: string): boolean {
  // A real profile always has a Preferences file; that is the cheapest reliable
  // signal and it also filters out the many cache/component folders.
  return fs.existsSync(path.join(dir, 'Preferences'));
}

/** Friendly profile name from Chromium's own Preferences file. */
function chromiumProfileLabel(dir: string): string | undefined {
  try {
    const prefs = JSON.parse(fs.readFileSync(path.join(dir, 'Preferences'), 'utf-8')) as {
      profile?: { name?: string };
      account_info?: Array<{ email?: string }>;
    };
    const email = prefs.account_info?.find((a) => a.email)?.email;
    const name = prefs.profile?.name;
    if (name && email) return `${name} (${email})`;
    return email ?? name;
  } catch {
    return undefined;
  }
}

/** Rough directory size in MB, capped so a huge profile can't stall the scan. */
function dirSizeMb(dir: string, budgetFiles = 4000): number | undefined {
  let bytes = 0;
  let seen = 0;
  const walk = (d: string): void => {
    if (seen >= budgetFiles) return;
    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const e of entries) {
      if (seen >= budgetFiles) return;
      const full = path.join(d, e.name);
      if (e.isDirectory()) {
        walk(full);
      } else if (e.isFile()) {
        seen++;
        try {
          bytes += fs.statSync(full).size;
        } catch {
          /* skip */
        }
      }
    }
  };
  walk(dir);
  if (!seen) return undefined;
  return Math.round((bytes / (1024 * 1024)) * 10) / 10;
}

/** Scan the machine for browser profiles that can be imported. */
export function discoverProfiles(): DiscoveredBrowserProfile[] {
  const found: DiscoveredBrowserProfile[] = [];

  for (const browser of BROWSERS) {
    for (const rootDir of browser.roots()) {
      if (!fs.existsSync(rootDir)) continue;

      if (browser.kind === 'firefox') {
        // Firefox profiles are <root>/<random>.<name>
        let entries: fs.Dirent[] = [];
        try {
          entries = fs.readdirSync(rootDir, { withFileTypes: true });
        } catch {
          continue;
        }
        for (const e of entries) {
          if (!e.isDirectory()) continue;
          const dir = path.join(rootDir, e.name);
          if (!fs.existsSync(path.join(dir, 'prefs.js'))) continue;
          found.push({
            browser: browser.name,
            name: `${e.name.replace(/^[a-z0-9]+\./i, '')} (${browser.name})`,
            path: dir,
            hasCookies: fs.existsSync(path.join(dir, 'cookies.sqlite')),
            sizeMb: dirSizeMb(dir),
          });
        }
        continue;
      }

      // Chromium family: profiles are direct children (Default, Profile 1, …).
      // Opera Stable is itself the profile dir, so check the root too.
      const candidates: string[] = [];
      if (isChromiumProfileDir(rootDir)) candidates.push(rootDir);
      try {
        for (const e of fs.readdirSync(rootDir, { withFileTypes: true })) {
          if (!e.isDirectory() || NON_PROFILE_DIRS.has(e.name)) continue;
          const dir = path.join(rootDir, e.name);
          if (isChromiumProfileDir(dir)) candidates.push(dir);
        }
      } catch {
        /* unreadable root */
      }

      for (const dir of candidates) {
        const label = chromiumProfileLabel(dir);
        const folder = path.basename(dir);
        found.push({
          browser: browser.name,
          name: `${label ?? folder} — ${browser.name}`,
          path: dir,
          hasCookies:
            fs.existsSync(path.join(dir, 'Cookies')) ||
            fs.existsSync(path.join(dir, 'Network', 'Cookies')),
          sizeMb: dirSizeMb(dir),
        });
      }
    }
  }

  // Stable, predictable order in the picker.
  return found.sort((a, b) => a.browser.localeCompare(b.browser) || a.name.localeCompare(b.name));
}

/** Locale/timezone hints read out of a Chromium profile's Preferences. */
export function readChromiumHints(profileDir: string): { locale?: string; timezone?: string } {
  try {
    const prefs = JSON.parse(fs.readFileSync(path.join(profileDir, 'Preferences'), 'utf-8')) as {
      intl?: { accept_languages?: string; app_locale?: string };
    };
    const accept = prefs.intl?.accept_languages?.split(',')[0]?.trim();
    const locale = accept || prefs.intl?.app_locale?.replace('_', '-');
    return locale ? { locale } : {};
  } catch {
    return {};
  }
}

/**
 * Files and folders worth copying when cloning a browser profile.
 *
 * A full recursive copy would drag in gigabytes of cache and, worse, the
 * `SingletonLock`/`LOCK` files that stop Chromium starting. This list is the
 * session-bearing state only.
 */
const CLONE_ENTRIES = [
  'Cookies',
  'Cookies-journal',
  'Login Data',
  'Login Data For Account',
  'Web Data',
  'Preferences',
  'Secure Preferences',
  'Local Storage',
  'Session Storage',
  'IndexedDB',
  'Local Extension Settings',
  'Extension State',
  'Extension Rules',
  'databases',
  'Service Worker',
  'Bookmarks',
  'History',
  'Favicons',
  'Network',
  'Sync Data',
  'Trust Tokens',
  'shared_proto_db',
];

export interface CloneResult {
  ok: boolean;
  copied: string[];
  skipped: string[];
  bytes: number;
  error?: string;
}

/**
 * Clone the session-bearing parts of a browser profile into a Hub profile dir.
 *
 * The source browser must be closed: Chromium holds an exclusive lock on the
 * Cookies DB while running, and copying it live yields a truncated file.
 */
export function cloneProfileData(sourceDir: string, targetDir: string): CloneResult {
  const copied: string[] = [];
  const skipped: string[] = [];
  let bytes = 0;

  if (!fs.existsSync(sourceDir)) {
    return { ok: false, copied, skipped, bytes, error: 'The source profile folder no longer exists.' };
  }
  if (isBrowserLocked(sourceDir)) {
    return {
      ok: false,
      copied,
      skipped,
      bytes,
      error:
        'That browser profile is currently in use. Close the browser completely and try again — copying a live profile corrupts its cookie database.',
    };
  }

  fs.mkdirSync(targetDir, { recursive: true });

  for (const entry of CLONE_ENTRIES) {
    const from = path.join(sourceDir, entry);
    const to = path.join(targetDir, entry);
    if (!fs.existsSync(from)) {
      skipped.push(entry);
      continue;
    }
    try {
      const stat = fs.statSync(from);
      if (stat.isDirectory()) {
        fs.cpSync(from, to, { recursive: true, force: true, errorOnExist: false });
      } else {
        fs.mkdirSync(path.dirname(to), { recursive: true });
        fs.copyFileSync(from, to);
        bytes += stat.size;
      }
      copied.push(entry);
    } catch (e) {
      skipped.push(`${entry} (${(e as Error).message})`);
    }
  }

  // A copied lock file would make the cloned profile unstartable.
  for (const lock of ['SingletonLock', 'SingletonCookie', 'SingletonSocket', 'LOCK']) {
    try {
      fs.rmSync(path.join(targetDir, lock), { force: true, recursive: true });
    } catch {
      /* ignore */
    }
  }

  if (!copied.length) {
    return { ok: false, copied, skipped, bytes, error: 'Nothing could be copied from that profile.' };
  }
  return { ok: true, copied, skipped, bytes };
}

/** Heuristic: is the source browser still running on this profile? */
function isBrowserLocked(profileDir: string): boolean {
  // Chromium keeps SingletonLock in the *user-data* root, one level above the
  // profile folder (except for single-profile layouts like Opera Stable).
  for (const dir of [profileDir, path.dirname(profileDir)]) {
    for (const lock of ['SingletonLock', 'SingletonSocket']) {
      const p = path.join(dir, lock);
      try {
        // lstat, not existsSync: SingletonLock is a symlink whose target
        // (hostname-pid) usually does not resolve, so existsSync says false.
        fs.lstatSync(p);
        return true;
      } catch {
        /* not locked via this path */
      }
    }
  }
  return false;
}

/** Cookie export files a user is likely to have, for the file-picker filter. */
export const COOKIE_FILE_FILTERS = [
  { name: 'Cookie files', extensions: ['txt', 'json'] },
  { name: 'All files', extensions: ['*'] },
];
