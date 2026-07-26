/**
 * Tiny atomic JSON store.
 *
 * Writes go to a temp file then rename() — an interrupted write can never
 * truncate the previous good copy, which matters because profiles.json is the
 * only record of the user's work. A corrupted file is moved aside (.corrupt)
 * rather than deleted, so the user can recover it manually.
 */

import fs from 'node:fs';
import path from 'node:path';

export function readJson<T>(file: string, fallback: T): T {
  try {
    const raw = fs.readFileSync(file, 'utf-8');
    if (!raw.trim()) return fallback;
    return JSON.parse(raw) as T;
  } catch (e) {
    const err = e as NodeJS.ErrnoException;
    if (err?.code === 'ENOENT') return fallback;
    // Malformed JSON: preserve the file for forensics, then start clean.
    try {
      const backup = `${file}.corrupt-${Date.now()}`;
      fs.renameSync(file, backup);
      console.error(`[store] ${path.basename(file)} was unreadable, moved to ${backup}`);
    } catch {
      console.error(`[store] ${path.basename(file)} unreadable and could not be moved aside`);
    }
    return fallback;
  }
}

export function writeJson(file: string, data: unknown): void {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp-${process.pid}`;
  fs.writeFileSync(tmp, JSON.stringify(data, null, 2), 'utf-8');
  fs.renameSync(tmp, file);
}
