/**
 * Filesystem layout for the app.
 *
 *   <userData>/
 *     profiles.json          profile list
 *     proxies.json           proxy library
 *     settings.json          app settings
 *     cookies/<id>.jar       encrypted cookie jar per profile
 *     profiles/<id>/         Chromium user-data dir per profile
 *     logs/                  session logs
 */

import fs from 'node:fs';
import path from 'node:path';
import { app } from 'electron';

let overrideRoot: string | undefined;

/** Testing / portable-mode hook. */
export function setRootOverride(dir: string | undefined): void {
  overrideRoot = dir;
}

export function root(): string {
  return overrideRoot ?? app.getPath('userData');
}

function ensure(dir: string): string {
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

export const paths = {
  root,
  profilesFile: () => path.join(root(), 'profiles.json'),
  proxiesFile: () => path.join(root(), 'proxies.json'),
  settingsFile: () => path.join(root(), 'settings.json'),
  cookiesDir: () => ensure(path.join(root(), 'cookies')),
  /** Encrypted cookie jar for a profile. */
  cookieJar: (profileId: string) => path.join(paths.cookiesDir(), `${safeId(profileId)}.jar`),
  logsDir: () => ensure(path.join(root(), 'logs')),
  /** Chromium user-data dir for a profile. Honours a custom profiles root. */
  profileDataDir: (profileId: string, customRoot?: string) =>
    ensure(path.join(customRoot || path.join(root(), 'profiles'), safeId(profileId))),
  profilesRoot: (customRoot?: string) => ensure(customRoot || path.join(root(), 'profiles')),
};

/** Never let an id escape its directory. */
export function safeId(id: string): string {
  const cleaned = id.replace(/[^a-zA-Z0-9_-]/g, '_');
  return cleaned || 'unnamed';
}
