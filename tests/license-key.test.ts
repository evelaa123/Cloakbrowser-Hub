/**
 * License-key file handling.
 *
 * These tests exist because of a real, reported failure: a user had a valid key
 * saved at ~/.cloakbrowser/license.key and the app showed
 * "This license key is invalid or expired", with the masked key rendered as
 * `��c b _9` — a replacement char followed by NUL-separated characters. That is
 * the signature of UTF-16 text decoded as UTF-8, which is exactly what
 * PowerShell 5.1 produces for `"KEY" > license.key` (Set-Content defaults to
 * UTF-16LE with a BOM).
 *
 * The bug is worth pinning because it is *silent*: the mojibake key is a
 * non-empty string, so it passes every `if (key)` check and gets sent to the
 * license server, which correctly rejects it. Nothing in the failure points at
 * an encoding.
 *
 * electron is mocked because license.ts imports `shell` for the sign-in links.
 */

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('electron', () => ({ shell: { openExternal: vi.fn() } }));

let tmpDir: string;

async function loadLicense() {
  // Re-imported per test so CLOAKBROWSER_CACHE_DIR is read fresh.
  vi.resetModules();
  return import('../src/main/services/license');
}

function writeKeyFile(bytes: Buffer | string): void {
  fs.writeFileSync(path.join(tmpDir, 'license.key'), bytes);
}

const KEY = 'cb_live_9f3a2b1c8d4e5f60';

beforeEach(() => {
  tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'hub-license-'));
  process.env.CLOAKBROWSER_CACHE_DIR = tmpDir;
  delete process.env.CLOAKBROWSER_LICENSE_KEY;
});

afterEach(() => {
  delete process.env.CLOAKBROWSER_CACHE_DIR;
  delete process.env.CLOAKBROWSER_LICENSE_KEY;
  fs.rmSync(tmpDir, { recursive: true, force: true });
});

describe('readSavedKey — encodings', () => {
  it('reads plain UTF-8', async () => {
    const { readSavedKey } = await loadLicense();
    writeKeyFile(`${KEY}\n`);
    expect(readSavedKey()).toBe(KEY);
  });

  it('reads UTF-16LE with BOM (PowerShell `>` / Set-Content)', async () => {
    const { readSavedKey } = await loadLicense();
    // This is the exact byte layout from the bug report.
    writeKeyFile(Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(`${KEY}\r\n`, 'utf16le')]));
    expect(readSavedKey()).toBe(KEY);
  });

  it('reads UTF-16BE with BOM', async () => {
    const { readSavedKey } = await loadLicense();
    const be = Buffer.from(KEY, 'utf16le').swap16();
    writeKeyFile(Buffer.concat([Buffer.from([0xfe, 0xff]), be]));
    expect(readSavedKey()).toBe(KEY);
  });

  it('reads UTF-16LE with no BOM (iconv, some editors)', async () => {
    const { readSavedKey } = await loadLicense();
    writeKeyFile(Buffer.from(KEY, 'utf16le'));
    expect(readSavedKey()).toBe(KEY);
  });

  it('strips a UTF-8 BOM, which would otherwise survive trim()', async () => {
    const { readSavedKey } = await loadLicense();
    writeKeyFile(Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), Buffer.from(KEY, 'utf-8')]));
    expect(readSavedKey()).toBe(KEY);
  });

  it('does not return mojibake for a UTF-16 file (regression)', async () => {
    const { readSavedKey } = await loadLicense();
    writeKeyFile(Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(KEY, 'utf16le')]));
    const got = readSavedKey()!;
    // The old behaviour produced both of these; either one means the key is
    // being sent to the server corrupted.
    expect(got).not.toContain('\u0000');
    expect(got).not.toContain('\uFFFD');
  });
});

describe('normaliseKey — what people actually put in the file', () => {
  it('trims CRLF', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey(`${KEY}\r\n`)).toBe(KEY);
  });

  it('strips paired double and single quotes', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey(`"${KEY}"`)).toBe(KEY);
    expect(normaliseKey(`'${KEY}'`)).toBe(KEY);
  });

  it('keeps an unpaired quote, which is more likely a typo than quoting', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey(`"${KEY}`)).toBe(`"${KEY}`);
  });

  it('accepts a pasted env-var line', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey(`CLOAKBROWSER_LICENSE_KEY=${KEY}`)).toBe(KEY);
    expect(normaliseKey(`LICENSE_KEY = ${KEY}`)).toBe(KEY);
  });

  it('takes the first non-empty, non-comment line', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey(`# my key\n\n${KEY}\ntrailing junk`)).toBe(KEY);
  });

  it('returns empty for blank input rather than whitespace', async () => {
    const { normaliseKey } = await loadLicense();
    expect(normaliseKey('   \n\n')).toBe('');
    expect(normaliseKey('')).toBe('');
  });
});

describe('repairKeyFileEncoding', () => {
  it('rewrites a UTF-16 file as UTF-8 so the CLI and binary can read it too', async () => {
    const { repairKeyFileEncoding } = await loadLicense();
    const file = path.join(tmpDir, 'license.key');
    writeKeyFile(Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(KEY, 'utf16le')]));

    expect(repairKeyFileEncoding()).toBe(true);

    // The contract that matters: a plain utf-8 read — exactly what the upstream
    // wrapper does — must now yield the key.
    expect(fs.readFileSync(file, 'utf-8').trim()).toBe(KEY);
  });

  it('is a no-op for an already-correct file, so it cannot churn the disk', async () => {
    const { repairKeyFileEncoding } = await loadLicense();
    writeKeyFile(`${KEY}\n`);
    expect(repairKeyFileEncoding()).toBe(false);
  });

  it('reports false when there is no key file at all', async () => {
    const { repairKeyFileEncoding } = await loadLicense();
    expect(repairKeyFileEncoding()).toBe(false);
  });

  it('normalises quotes and CRLF on disk as well', async () => {
    const { repairKeyFileEncoding } = await loadLicense();
    writeKeyFile(`"${KEY}"\r\n`);
    expect(repairKeyFileEncoding()).toBe(true);
    expect(fs.readFileSync(path.join(tmpDir, 'license.key'), 'utf-8').trim()).toBe(KEY);
  });
});

describe('saveKey', () => {
  it('writes UTF-8 that a plain utf-8 reader can parse', async () => {
    const { saveKey } = await loadLicense();
    saveKey(`  ${KEY}  `);
    const buf = fs.readFileSync(path.join(tmpDir, 'license.key'));
    expect(buf[0]).not.toBe(0xff); // no BOM
    expect(buf.includes(0x00)).toBe(false); // no UTF-16 NULs
    expect(buf.toString('utf-8')).toBe(`${KEY}\n`);
  });

  it('round-trips through readSavedKey', async () => {
    const { saveKey, readSavedKey } = await loadLicense();
    saveKey(`"${KEY}"\r\n`);
    expect(readSavedKey()).toBe(KEY);
  });
});

describe('env var precedence', () => {
  it('prefers CLOAKBROWSER_LICENSE_KEY, since the wrapper resolves it first', async () => {
    const { readSavedKey } = await loadLicense();
    writeKeyFile(`${KEY}\n`);
    process.env.CLOAKBROWSER_LICENSE_KEY = 'cb_env_key';
    expect(readSavedKey()).toBe('cb_env_key');
  });

  it('normalises the env var too', async () => {
    const { readSavedKey } = await loadLicense();
    process.env.CLOAKBROWSER_LICENSE_KEY = `"cb_env_key"`;
    expect(readSavedKey()).toBe('cb_env_key');
  });
});
