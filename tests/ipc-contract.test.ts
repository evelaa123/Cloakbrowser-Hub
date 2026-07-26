/**
 * IPC contract tests.
 *
 * The renderer, the preload bridge and the main process each name channels
 * independently. A typo in any one of them produces a channel that silently does
 * nothing at runtime and is invisible to `tsc`, because channel names are just
 * strings. These tests close that gap by registering the real handlers against a
 * fake `ipcMain` and asserting the three sides agree.
 */

import { beforeAll, describe, expect, it, vi } from 'vitest';

/** Channels registered by `registerIpcHandlers()` during this test run. */
const registered = new Map<string, (...args: unknown[]) => unknown>();
const sentEvents: string[] = [];

vi.mock('electron', () => {
  const fakeWindow = {
    isDestroyed: () => false,
    webContents: {
      send: (channel: string) => {
        sentEvents.push(channel);
      },
    },
  };
  return {
    ipcMain: {
      handle: (channel: string, fn: (...args: unknown[]) => unknown) => {
        if (registered.has(channel)) {
          throw new Error(`Duplicate IPC handler registered for "${channel}"`);
        }
        registered.set(channel, fn);
      },
    },
    BrowserWindow: { getAllWindows: () => [fakeWindow] },
    app: {
      getVersion: () => '0.0.0-test',
      getPath: () => '/tmp/cloakbrowser-hub-test',
    },
    dialog: {
      showOpenDialog: async () => ({ canceled: true, filePaths: [] }),
      showSaveDialog: async () => ({ canceled: true, filePath: undefined }),
    },
    shell: {
      openExternal: async () => undefined,
      openPath: async () => '',
    },
    safeStorage: {
      isEncryptionAvailable: () => false,
      encryptString: (s: string) => Buffer.from(s),
      decryptString: (b: Buffer) => b.toString(),
    },
  };
});

// The stealth browser is never launched in tests; only its module shape matters.
vi.mock('cloakbrowser', () => ({
  binaryInfo: () => ({
    version: '0.0.0',
    platform: 'linux',
    tier: 'free' as const,
    binaryPath: '/tmp/none',
    installed: false,
    cacheDir: '/tmp/none',
  }),
  ensureBinary: async () => '/tmp/none',
  launchPersistentContext: async () => {
    throw new Error('not used in tests');
  },
}));

import { IPC } from '../src/shared/ipc';
import { registerIpcHandlers } from '../src/main/ipc/handlers';
import { setRootOverride } from '../src/main/services/paths';

/** Channel constants that are main→renderer events, not invoke handlers. */
const EVENT_CHANNELS = new Set<string>([
  IPC.EVT_SESSIONS,
  IPC.EVT_LOG,
  IPC.EVT_PROFILES_CHANGED,
  IPC.EVT_BINARY_PROGRESS,
]);

beforeAll(() => {
  // Keep every repo write inside a throwaway directory.
  setRootOverride('/tmp/cloakbrowser-hub-test');
  registerIpcHandlers();
});

describe('IPC channel registration', () => {
  it('registers a handler for every invoke channel declared in shared/ipc', () => {
    const invokeChannels = Object.values(IPC).filter((c) => !EVENT_CHANNELS.has(c));
    const missing = invokeChannels.filter((c) => !registered.has(c));
    expect(missing).toEqual([]);
  });

  it('does not register invoke handlers for event-only channels', () => {
    // An event channel with an invoke handler means the two roles were confused.
    const wrong = [...EVENT_CHANNELS].filter((c) => registered.has(c));
    expect(wrong).toEqual([]);
  });

  it('registers no handler that is absent from shared/ipc', () => {
    const declared = new Set<string>(Object.values(IPC));
    const undeclared = [...registered.keys()].filter((c) => !declared.has(c));
    expect(undeclared).toEqual([]);
  });

  it('uses unique channel names', () => {
    const values = Object.values(IPC);
    expect(new Set(values).size).toBe(values.length);
  });
});

describe('handler error contract', () => {
  it('returns { ok: false, error } instead of throwing across the boundary', async () => {
    const handler = registered.get(IPC.PROFILES_UPDATE)!;
    // Updating a profile that does not exist is a thrown error inside the
    // handler; the renderer must receive a readable Result instead.
    const res = (await handler({}, 'no-such-profile-id', { name: 'x' })) as {
      ok: boolean;
      error?: string;
    };
    expect(res.ok).toBe(false);
    expect(res.error).toBeTruthy();
    // A raw stack trace would be useless in a toast; only a first line is sent.
    expect(res.error).not.toContain('\n');
  });

  it('returns { ok: true, data } on success', async () => {
    const handler = registered.get(IPC.SETTINGS_GET)!;
    const res = (await handler({})) as { ok: boolean; data?: { theme: string } };
    expect(res.ok).toBe(true);
    expect(res.data?.theme).toBeTruthy();
  });

  it('rejects non-http(s) URLs from the renderer', async () => {
    const handler = registered.get(IPC.APP_OPEN_EXTERNAL)!;
    // A file:// or custom scheme reaching shell.openExternal would be an
    // arbitrary-execution hole, so it must be refused, not opened.
    const res = (await handler({}, 'file:///etc/passwd')) as { ok: boolean; error?: string };
    expect(res.ok).toBe(false);
    expect(res.error).toMatch(/http/i);
  });

  it('accepts https URLs', async () => {
    const handler = registered.get(IPC.APP_OPEN_EXTERNAL)!;
    const res = (await handler({}, 'https://cloakbrowser.dev/')) as { ok: boolean };
    expect(res.ok).toBe(true);
  });
});
