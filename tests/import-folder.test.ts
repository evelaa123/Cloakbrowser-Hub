/**
 * Folder-scan and archive-extraction tests.
 *
 * Requested as: import profiles "из архива или произвольной папки, выбранной на
 * диске, чтобы он сканировал и импортировал нормально". The two halves have very
 * different risk profiles, and the tests reflect that:
 *
 *  - The scanner must not assume a layout. A user will drop the profile folder,
 *    its "User Data" parent, or an archive with a wrapper directory, and all
 *    three have to work.
 *  - The extractor takes an untrusted file. `safeEntryPath` is the zip-slip
 *    guard, so it gets adversarial input directly rather than only through a
 *    happy-path extraction.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { guessBrowser, isSupportedArchive, scanFolderForProfiles } from '../src/main/importers/scan-folder';
import { isInside, safeEntryPath } from '../src/main/importers/extract-archive';

let tmp: string;

beforeEach(() => {
  tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'hub-scan-'));
});

afterEach(() => {
  fs.rmSync(tmp, { recursive: true, force: true });
});

/** Create a minimal but realistic Chromium profile folder. */
function makeChromiumProfile(dir: string, opts: { name?: string; email?: string; cookies?: boolean } = {}): string {
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    path.join(dir, 'Preferences'),
    JSON.stringify({
      profile: { name: opts.name ?? 'Person 1' },
      ...(opts.email ? { account_info: [{ email: opts.email }] } : {}),
    }),
  );
  if (opts.cookies) fs.writeFileSync(path.join(dir, 'Cookies'), 'sqlite-ish');
  return dir;
}

describe('scanFolderForProfiles — layouts a user will actually pick', () => {
  it('finds a profile when the picked folder IS the profile', () => {
    makeChromiumProfile(path.join(tmp, 'Default'), { cookies: true });
    const res = scanFolderForProfiles(path.join(tmp, 'Default'));
    expect(res.profiles).toHaveLength(1);
    expect(res.profiles[0]!.hasCookies).toBe(true);
  });

  it('finds profiles when the picked folder is a User Data root', () => {
    makeChromiumProfile(path.join(tmp, 'Default'));
    makeChromiumProfile(path.join(tmp, 'Profile 1'));
    expect(scanFolderForProfiles(tmp).profiles).toHaveLength(2);
  });

  it('finds a profile nested under a wrapper directory, as an unpacked archive is', () => {
    makeChromiumProfile(path.join(tmp, 'backup', 'User Data', 'Profile 3'));
    const res = scanFolderForProfiles(tmp);
    expect(res.profiles).toHaveLength(1);
    expect(res.profiles[0]!.path).toContain('Profile 3');
  });

  it('finds a Firefox profile by prefs.js', () => {
    const dir = path.join(tmp, 'abc123.default-release');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'prefs.js'), 'user_pref("x", 1);');
    const res = scanFolderForProfiles(tmp);
    expect(res.profiles).toHaveLength(1);
    expect(res.profiles[0]!.browser).toBe('Firefox');
  });

  it('reads the account email into the label, so two profiles are tellable apart', () => {
    makeChromiumProfile(path.join(tmp, 'Default'), { name: 'Work', email: 'a@b.com' });
    expect(scanFolderForProfiles(tmp).profiles[0]!.name).toContain('a@b.com');
  });

  it('falls back to the folder name when Preferences is corrupt', () => {
    // Exactly what a half-finished archive extraction leaves behind. The profile
    // is still importable, so it must not be dropped.
    const dir = path.join(tmp, 'Profile 9');
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, 'Preferences'), '{ this is not json');
    const res = scanFolderForProfiles(tmp);
    expect(res.profiles).toHaveLength(1);
    expect(res.profiles[0]!.name).toContain('Profile 9');
  });
});

describe('scanFolderForProfiles — not descending into profiles', () => {
  it('does not report a profile’s own subfolders as profiles', () => {
    const p = makeChromiumProfile(path.join(tmp, 'Default'));
    // A nested Preferences inside a cache dir would otherwise be a false hit.
    makeChromiumProfile(path.join(p, 'Cache', 'weird'));
    expect(scanFolderForProfiles(tmp).profiles).toHaveLength(1);
  });

  it('skips known cache directories entirely', () => {
    makeChromiumProfile(path.join(tmp, 'Code Cache', 'nope'));
    makeChromiumProfile(path.join(tmp, 'Service Worker', 'nope'));
    expect(scanFolderForProfiles(tmp).profiles).toHaveLength(0);
  });
});

describe('scanFolderForProfiles — telling the user what went wrong', () => {
  it('explains what it looked for when nothing is found', () => {
    const res = scanFolderForProfiles(tmp);
    expect(res.profiles).toHaveLength(0);
    // A bare empty list gives the user nothing to act on; naming the marker file
    // tells them whether they picked the wrong level.
    expect(res.note).toMatch(/Preferences/);
    expect(res.note).toMatch(/prefs\.js/);
  });

  it('reports a missing folder rather than an empty result', () => {
    const res = scanFolderForProfiles(path.join(tmp, 'nope'));
    expect(res.note).toMatch(/does not exist|not readable/i);
  });

  it('reports a file picked where a folder was expected', () => {
    const f = path.join(tmp, 'a.txt');
    fs.writeFileSync(f, 'x');
    expect(scanFolderForProfiles(f).note).toMatch(/file, not a folder/i);
  });

  it('handles an empty path without throwing', () => {
    expect(scanFolderForProfiles('').profiles).toEqual([]);
  });
});

describe('guessBrowser', () => {
  it('recognises the common install paths', () => {
    expect(guessBrowser('/home/u/.config/google-chrome/Default')).toBe('Chrome');
    expect(guessBrowser('C:\\Users\\u\\AppData\\Local\\BraveSoftware\\Brave-Browser\\User Data')).toBe('Brave');
    expect(guessBrowser('/x/Microsoft Edge/Default')).toBe('Edge');
    expect(guessBrowser('/x/chromium/Default')).toBe('Chromium');
  });

  it('falls back to a neutral label instead of guessing wrong', () => {
    expect(guessBrowser('/mnt/usb/some-backup/Default')).toBe('Imported');
  });
});

describe('isSupportedArchive', () => {
  it('accepts .zip in any case', () => {
    expect(isSupportedArchive('a.zip')).toBe(true);
    expect(isSupportedArchive('A.ZIP')).toBe(true);
  });

  it('rejects formats that would need a native dependency', () => {
    for (const f of ['a.rar', 'a.7z', 'a.tar.gz', 'a.tgz', 'a']) {
      expect(isSupportedArchive(f)).toBe(false);
    }
  });
});

describe('safeEntryPath — zip-slip guard', () => {
  const root = '/tmp/extract-root';

  it('accepts an ordinary nested entry', () => {
    expect(safeEntryPath(root, 'User Data/Default/Preferences')).toBe(
      path.join(root, 'User Data', 'Default', 'Preferences'),
    );
  });

  it('rejects parent-directory traversal', () => {
    expect(safeEntryPath(root, '../../../../etc/passwd')).toBeNull();
    expect(safeEntryPath(root, 'a/../../b')).toBeNull();
  });

  it('rejects traversal written with backslashes', () => {
    // Archives produced by broken Windows tooling use backslashes; treating them
    // as literal filename characters would let the guard be bypassed.
    expect(safeEntryPath(root, '..\\..\\evil.txt')).toBeNull();
  });

  it('rejects absolute paths and drive letters', () => {
    expect(safeEntryPath(root, '/etc/shadow')).toBeNull();
    expect(safeEntryPath(root, 'C:/Windows/System32/x.dll')).toBeNull();
  });

  it('strips redundant current-directory segments', () => {
    expect(safeEntryPath(root, './a/./b.txt')).toBe(path.join(root, 'a', 'b.txt'));
  });
});

describe('isInside', () => {
  it('accepts the root itself and its descendants', () => {
    expect(isInside('/tmp/x', '/tmp/x')).toBe(true);
    expect(isInside('/tmp/x', '/tmp/x/y/z')).toBe(true);
  });

  it('rejects a sibling with a shared prefix', () => {
    // The classic off-by-one: string prefix matching alone would accept this.
    expect(isInside('/tmp/x', '/tmp/x-evil')).toBe(false);
  });

  it('rejects an unrelated path', () => {
    expect(isInside('/tmp/x', '/etc/passwd')).toBe(false);
  });
});
