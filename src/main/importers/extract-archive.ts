/**
 * Extract a .zip so its contents can be scanned for browser profiles.
 *
 * Uses `yauzl` (a pure-JS, streaming reader) rather than shelling out to `unzip`
 * or `tar`: the app must work identically on a stock Windows install where
 * neither exists, and a packaged Electron build has no shell tooling guaranteed.
 *
 * Two safety properties matter here, because the archive is untrusted input the
 * user may have received from someone else:
 *
 *  1. Path traversal. A zip entry named `../../../../.ssh/authorized_keys` is a
 *     well-known attack ("zip slip"); every destination path is therefore
 *     resolved and verified to stay inside the extraction root.
 *  2. Resource exhaustion. A zip bomb expands to terabytes from a few KB, so
 *     total extracted bytes and entry count are both capped.
 *
 * Symlink entries are skipped entirely rather than recreated — a symlink is the
 * other half of a traversal escape, and a browser profile has no legitimate need
 * for one.
 */

import fs from 'node:fs';
import path from 'node:path';
import { promisify } from 'node:util';
import yauzl from 'yauzl';

/** Total uncompressed bytes allowed. A browser profile is large but not this large. */
const MAX_TOTAL_BYTES = 4 * 1024 * 1024 * 1024; // 4 GB

/** Maximum number of entries, as a guard against millions of tiny files. */
const MAX_ENTRIES = 200_000;

export interface ExtractResult {
  ok: boolean;
  /** Directory the archive was unpacked into. */
  dir: string;
  entries: number;
  bytes: number;
  /** Entries skipped for safety, with the reason — surfaced rather than hidden. */
  skipped: string[];
  error?: string;
}

/**
 * Is `candidate` inside `root`?
 *
 * Compared on resolved paths with a separator suffix, so `/tmp/x-evil` is not
 * accepted as being inside `/tmp/x`.
 */
export function isInside(root: string, candidate: string): boolean {
  const r = path.resolve(root);
  const c = path.resolve(candidate);
  if (c === r) return true;
  return c.startsWith(r.endsWith(path.sep) ? r : r + path.sep);
}

/**
 * Decide the on-disk destination for a zip entry, or null to skip it.
 *
 * Exported for tests: the traversal check is the security-relevant part of this
 * module and deserves direct coverage.
 */
export function safeEntryPath(root: string, entryName: string): string | null {
  // Zip entries use forward slashes by spec, but real-world archives contain
  // backslashes too (created by broken Windows tooling), so both are separators.
  const normalised = entryName.replace(/\\/g, '/');

  // Absolute paths and drive letters are never valid inside an archive.
  if (normalised.startsWith('/') || /^[a-z]:/i.test(normalised)) return null;
  // Any traversal segment is rejected outright rather than normalised away.
  if (normalised.split('/').some((seg) => seg === '..')) return null;

  const dest = path.join(root, ...normalised.split('/').filter((s) => s && s !== '.'));
  return isInside(root, dest) ? dest : null;
}

const openZip = promisify(yauzl.open) as (
  p: string,
  o: yauzl.Options,
) => Promise<yauzl.ZipFile>;

/** Extract `archive` into `dir`, creating it. */
export async function extractZip(archive: string, dir: string): Promise<ExtractResult> {
  const result: ExtractResult = { ok: false, dir, entries: 0, bytes: 0, skipped: [] };

  let zip: yauzl.ZipFile;
  try {
    fs.mkdirSync(dir, { recursive: true });
    zip = await openZip(archive, { lazyEntries: true, autoClose: true });
  } catch (e) {
    result.error = `Could not open the archive: ${(e as Error)?.message ?? String(e)}`;
    return result;
  }

  return new Promise<ExtractResult>((resolve) => {
    const finish = (error?: string): void => {
      result.error = error;
      result.ok = !error;
      resolve(result);
    };

    zip.on('error', (e: Error) => finish(`Archive read failed: ${e.message}`));
    zip.on('end', () => finish());

    zip.on('entry', (entry: yauzl.Entry) => {
      if (result.entries >= MAX_ENTRIES) {
        finish(`Archive has more than ${MAX_ENTRIES} entries; refusing to extract.`);
        return;
      }
      if (result.bytes > MAX_TOTAL_BYTES) {
        finish('Archive expands to more than 4 GB; refusing to extract.');
        return;
      }

      const mode = (entry.externalFileAttributes >>> 16) & 0xffff;
      // S_IFLNK. A symlink is the second half of a path-traversal escape and a
      // profile never needs one.
      const isSymlink = (mode & 0xf000) === 0xa000;
      if (isSymlink) {
        result.skipped.push(`${entry.fileName} (symlink)`);
        zip.readEntry();
        return;
      }

      const dest = safeEntryPath(dir, entry.fileName);
      if (!dest) {
        result.skipped.push(`${entry.fileName} (unsafe path)`);
        zip.readEntry();
        return;
      }

      // Directory entries end in '/' by spec.
      if (/\/$/.test(entry.fileName.replace(/\\/g, '/'))) {
        try {
          fs.mkdirSync(dest, { recursive: true });
        } catch {
          result.skipped.push(`${entry.fileName} (could not create directory)`);
        }
        zip.readEntry();
        return;
      }

      zip.openReadStream(entry, (err, stream) => {
        if (err || !stream) {
          result.skipped.push(`${entry.fileName} (unreadable)`);
          zip.readEntry();
          return;
        }
        try {
          fs.mkdirSync(path.dirname(dest), { recursive: true });
        } catch {
          result.skipped.push(`${entry.fileName} (could not create parent)`);
          stream.resume();
          zip.readEntry();
          return;
        }

        const out = fs.createWriteStream(dest);
        stream.on('data', (chunk: Buffer) => {
          result.bytes += chunk.length;
        });
        out.on('error', () => {
          result.skipped.push(`${entry.fileName} (write failed)`);
          zip.readEntry();
        });
        out.on('close', () => {
          result.entries++;
          zip.readEntry();
        });
        stream.pipe(out);
      });
    });

    zip.readEntry();
  });
}

/** Delete an extraction directory. Best effort — a leftover temp dir is not fatal. */
export function cleanupExtraction(dir: string): void {
  try {
    if (dir && fs.existsSync(dir)) fs.rmSync(dir, { recursive: true, force: true });
  } catch {
    /* the OS will clear tmp eventually */
  }
}
