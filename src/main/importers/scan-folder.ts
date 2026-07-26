/**
 * Import browser profiles from an arbitrary folder or an archive.
 *
 * `discoverProfiles()` only looks in the standard install locations, which misses
 * every case where the profile did not come from a browser installed on this
 * machine: a backup, a profile copied off another PC, a `.zip` from a teammate,
 * an external drive.
 *
 * The hard part is not reading the folder, it is *not* assuming what the user
 * picked. All of these are things a person will reasonably drop here:
 *
 *   <picked>/                        ← the profile itself (has Preferences)
 *   <picked>/Default/                ← a Chromium user-data root
 *   <picked>/User Data/Default/      ← a copy of the whole browser data dir
 *   <picked>/backup/User Data/Profile 1/   ← an unpacked archive with a wrapper dir
 *   <picked>/<random>.default-release/     ← a Firefox profile
 *
 * So this walks a bounded depth looking for profile *markers* rather than
 * expecting a fixed layout. Depth and breadth are capped because the folder could
 * be `C:\` and an unbounded walk over a network drive would hang the app with no
 * way to cancel.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import type { DiscoveredBrowserProfile } from '../../shared/types';

/**
 * How deep to look below the picked folder.
 *
 * 4 covers the deepest realistic nesting (`archive/backup/User Data/Profile 1`)
 * without turning a mis-click on a drive root into a filesystem-wide scan.
 */
const MAX_DEPTH = 4;

/** Cap on directories visited, as a hard stop regardless of depth. */
const MAX_DIRS = 4000;

/** Cap on results, so a user-data root with 200 profiles cannot flood the UI. */
const MAX_RESULTS = 60;

/** Directories that are never profiles and are expensive to walk. */
const SKIP_DIRS = new Set([
  'Cache',
  'Code Cache',
  'GPUCache',
  'ShaderCache',
  'GrShaderCache',
  'DawnCache',
  'DawnWebGPUCache',
  'Service Worker',
  'IndexedDB',
  'Local Storage',
  'Session Storage',
  'blob_storage',
  'component_crx_cache',
  'extensions_crx_cache',
  'CertificateRevocation',
  'SafetyTips',
  'OptimizationHints',
  'node_modules',
  '.git',
  'System Volume Information',
  '$RECYCLE.BIN',
]);

export interface ScanResult {
  profiles: DiscoveredBrowserProfile[];
  /** True when a cap stopped the walk early, so the UI can say so. */
  truncated: boolean;
  /** Human-readable note when nothing was found, explaining what was looked for. */
  note?: string;
}

/** A Chromium profile always has a Preferences file. */
function isChromiumProfile(dir: string): boolean {
  return fs.existsSync(path.join(dir, 'Preferences'));
}

/** A Firefox profile always has prefs.js. */
function isFirefoxProfile(dir: string): boolean {
  return fs.existsSync(path.join(dir, 'prefs.js'));
}

/**
 * Guess which browser a profile came from, from the folder path.
 *
 * Best-effort labelling only — the import itself does not depend on it, so an
 * unrecognised path is reported as a generic Chromium profile rather than
 * rejected.
 */
export function guessBrowser(dir: string): string {
  const p = dir.replace(/\\/g, '/').toLowerCase();
  if (/bravesoftware|brave-browser/.test(p)) return 'Brave';
  if (/microsoft\/edge|microsoft edge/.test(p)) return 'Edge';
  if (/google\/chrome|google-chrome/.test(p)) return 'Chrome';
  if (/chromium/.test(p)) return 'Chromium';
  if (/opera/.test(p)) return 'Opera';
  if (/vivaldi/.test(p)) return 'Vivaldi';
  if (/yandex/.test(p)) return 'Yandex';
  if (/firefox|mozilla/.test(p)) return 'Firefox';
  return 'Imported';
}

/** Friendly name from Chromium's own Preferences, falling back to the folder. */
function chromiumLabel(dir: string): string {
  try {
    const prefs = JSON.parse(fs.readFileSync(path.join(dir, 'Preferences'), 'utf-8')) as {
      profile?: { name?: string };
      account_info?: Array<{ email?: string }>;
    };
    const email = prefs.account_info?.find((a) => a.email)?.email;
    const name = prefs.profile?.name;
    if (name && email) return `${name} (${email})`;
    return email ?? name ?? path.basename(dir);
  } catch {
    // A truncated or non-JSON Preferences file is exactly what a bad archive
    // extraction produces; the folder name is still a usable label.
    return path.basename(dir);
  }
}

function hasCookies(dir: string): boolean {
  return (
    fs.existsSync(path.join(dir, 'Cookies')) ||
    fs.existsSync(path.join(dir, 'Network', 'Cookies')) ||
    fs.existsSync(path.join(dir, 'cookies.sqlite'))
  );
}

/**
 * Scan a folder for importable browser profiles.
 *
 * Breadth-first so shallow, likely matches are reported before deep ones, and so
 * hitting a cap still returns the most plausible results rather than whatever a
 * depth-first walk happened to reach.
 */
export function scanFolderForProfiles(root: string): ScanResult {
  if (!root || !fs.existsSync(root)) {
    return { profiles: [], truncated: false, note: 'That folder does not exist or is not readable.' };
  }
  let stat: fs.Stats;
  try {
    stat = fs.statSync(root);
  } catch {
    return { profiles: [], truncated: false, note: 'That folder could not be read.' };
  }
  if (!stat.isDirectory()) {
    return { profiles: [], truncated: false, note: 'That path is a file, not a folder.' };
  }

  const found: DiscoveredBrowserProfile[] = [];
  const seen = new Set<string>();
  let visited = 0;
  let truncated = false;

  const queue: Array<{ dir: string; depth: number }> = [{ dir: root, depth: 0 }];

  while (queue.length) {
    const { dir, depth } = queue.shift()!;
    if (visited++ >= MAX_DIRS) {
      truncated = true;
      break;
    }

    const isChromium = isChromiumProfile(dir);
    const isFirefox = !isChromium && isFirefoxProfile(dir);

    if (isChromium || isFirefox) {
      const real = safeRealpath(dir);
      if (!seen.has(real)) {
        seen.add(real);
        const browser = guessBrowser(dir);
        found.push({
          browser: isFirefox ? 'Firefox' : browser,
          name: isFirefox ? `${path.basename(dir)} (Firefox)` : `${chromiumLabel(dir)} — ${browser}`,
          path: dir,
          hasCookies: hasCookies(dir),
          sizeMb: undefined,
        });
        if (found.length >= MAX_RESULTS) {
          truncated = true;
          break;
        }
      }
      // Do not descend into a profile: its own subfolders are never profiles, and
      // walking Cache/IndexedDB is where a scan goes to die.
      continue;
    }

    if (depth >= MAX_DEPTH) continue;

    let entries: fs.Dirent[];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      // Permission denied on one subfolder must not abort the whole scan.
      continue;
    }

    for (const e of entries) {
      if (!e.isDirectory()) continue;
      if (SKIP_DIRS.has(e.name)) continue;
      // Symlinks can form cycles; realpath dedup below covers the rest.
      if (e.isSymbolicLink()) continue;
      queue.push({ dir: path.join(dir, e.name), depth: depth + 1 });
    }
  }

  const profiles = found.sort((a, b) => a.name.localeCompare(b.name));
  if (!profiles.length) {
    return {
      profiles,
      truncated,
      note:
        'No browser profiles found in that folder. A Chromium profile folder contains a ' +
        '"Preferences" file (Firefox: "prefs.js") — try picking the folder that holds it, ' +
        'or its "User Data" parent.',
    };
  }
  return { profiles, truncated };
}

function safeRealpath(p: string): string {
  try {
    return fs.realpathSync.native ? fs.realpathSync.native(p) : fs.realpathSync(p);
  } catch {
    return path.resolve(p);
  }
}

// ---------------------------------------------------------------------------
// Archives
// ---------------------------------------------------------------------------

/** Archive extensions this build can extract without a native dependency. */
export const SUPPORTED_ARCHIVES = ['.zip'] as const;

export function isSupportedArchive(file: string): boolean {
  return SUPPORTED_ARCHIVES.some((ext) => file.toLowerCase().endsWith(ext));
}

/** Where an archive is unpacked before scanning. */
export function extractionDir(): string {
  return path.join(os.tmpdir(), `cloakbrowser-hub-import-${Date.now().toString(36)}`);
}
