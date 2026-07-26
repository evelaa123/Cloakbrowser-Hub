/**
 * At-rest encryption for cookie jars and proxy credentials.
 *
 * Uses Electron `safeStorage` (DPAPI on Windows, Keychain on macOS,
 * libsecret/kwallet on Linux). When the OS keyring is unavailable — common on
 * headless Linux — we fall back to a machine-local AES-256-GCM key so the app
 * keeps working; the fallback is clearly marked in the payload so a later
 * upgrade path is possible, and the failure is logged exactly once.
 */

import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { safeStorage } from 'electron';
import { paths } from './paths';

const ENC_PREFIX = 'ENC1:'; // safeStorage-encrypted, base64
const FALLBACK_PREFIX = 'ENC2:'; // local AES-256-GCM, base64
let warned = false;

function keyFile(): string {
  return path.join(paths.root(), '.local-key');
}

/** Lazily create / read the fallback key (0600). */
function localKey(): Buffer {
  const file = keyFile();
  try {
    const raw = fs.readFileSync(file);
    if (raw.length >= 32) return raw.subarray(0, 32);
  } catch {
    /* create below */
  }
  const key = crypto.randomBytes(32);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, key, { mode: 0o600 });
  try {
    fs.chmodSync(file, 0o600);
  } catch {
    /* best effort on Windows */
  }
  return key;
}

function available(): boolean {
  try {
    return safeStorage.isEncryptionAvailable();
  } catch {
    return false;
  }
}

function warnOnce(reason: string): void {
  if (warned) return;
  warned = true;
  console.warn(
    `[secrets] OS keyring unavailable (${reason}); falling back to a machine-local key file. ` +
      'Secrets remain encrypted at rest but are only as strong as file permissions.',
  );
}

/** Encrypt a UTF-8 string. Returns a prefixed, storable string. */
export function encrypt(plain: string): string {
  if (!plain) return '';
  if (available()) {
    try {
      return ENC_PREFIX + safeStorage.encryptString(plain).toString('base64');
    } catch (e) {
      warnOnce(String((e as Error)?.message ?? e));
    }
  } else {
    warnOnce('isEncryptionAvailable() === false');
  }

  const iv = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv('aes-256-gcm', localKey(), iv);
  const body = Buffer.concat([cipher.update(plain, 'utf-8'), cipher.final()]);
  const tag = cipher.getAuthTag();
  return FALLBACK_PREFIX + Buffer.concat([iv, tag, body]).toString('base64');
}

/**
 * Decrypt a value produced by `encrypt`. Plain (unprefixed) input is returned
 * verbatim so pre-encryption files keep working. Returns null when the payload
 * cannot be decrypted (e.g. copied to a different machine/user) — callers treat
 * that as "no data" and re-import rather than crashing.
 */
export function decrypt(stored: string): string | null {
  if (!stored) return null;
  try {
    if (stored.startsWith(ENC_PREFIX)) {
      return safeStorage.decryptString(Buffer.from(stored.slice(ENC_PREFIX.length), 'base64'));
    }
    if (stored.startsWith(FALLBACK_PREFIX)) {
      const buf = Buffer.from(stored.slice(FALLBACK_PREFIX.length), 'base64');
      const iv = buf.subarray(0, 12);
      const tag = buf.subarray(12, 28);
      const body = buf.subarray(28);
      const decipher = crypto.createDecipheriv('aes-256-gcm', localKey(), iv);
      decipher.setAuthTag(tag);
      return Buffer.concat([decipher.update(body), decipher.final()]).toString('utf-8');
    }
    return stored; // legacy plaintext
  } catch {
    return null;
  }
}

/** Mask a secret for display: keeps enough to recognise, hides the rest. */
export function mask(secret: string | undefined, keep = 4): string {
  if (!secret) return '';
  if (secret.length <= keep * 2) return '•'.repeat(secret.length);
  return `${secret.slice(0, keep)}…${secret.slice(-keep)}`;
}
