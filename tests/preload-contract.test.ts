/**
 * Preload bridge contract test.
 *
 * The bridge is the renderer's only way to reach the main process. If a channel
 * exists in `shared/ipc` and has a handler but no bridge method, the feature is
 * simply unreachable from the UI — and nothing in the type system says so,
 * because the bridge is an object literal, not an exhaustive mapping.
 *
 * This test loads the real preload module against a fake `contextBridge`,
 * inspects the API it exposes, and asserts every invoke channel is used exactly
 * once and every event channel has a subscription method.
 */

import { describe, expect, it, vi } from 'vitest';

/**
 * `vi.mock` factories are hoisted above ordinary `const`/`let` declarations, so
 * the recording state has to be created with `vi.hoisted` to exist by the time
 * the factory runs.
 */
const spy = vi.hoisted(() => ({
  invoked: [] as string[],
  subscribed: [] as string[],
  exposed: undefined as Record<string, unknown> | undefined,
}));

vi.mock('electron', () => ({
  contextBridge: {
    exposeInMainWorld: (_key: string, api: Record<string, unknown>) => {
      spy.exposed = api;
    },
  },
  ipcRenderer: {
    invoke: async (channel: string) => {
      spy.invoked.push(channel);
      // Shape the bridge expects; the value is irrelevant here.
      return { ok: true, data: undefined };
    },
    on: (channel: string) => {
      spy.subscribed.push(channel);
    },
    removeListener: () => undefined,
  },
}));

import { IPC } from '../src/shared/ipc';
import '../src/preload/index';

const EVENT_CHANNELS = [
  IPC.EVT_SESSIONS,
  IPC.EVT_LOG,
  IPC.EVT_PROFILES_CHANGED,
  IPC.EVT_BINARY_PROGRESS,
] as const;

type AnyFn = (...args: unknown[]) => unknown;

/** Call every function on the exposed API so each channel name is recorded. */
async function callEveryMethod(api: Record<string, unknown>): Promise<void> {
  for (const group of Object.values(api)) {
    if (!group || typeof group !== 'object') continue;
    for (const member of Object.values(group as Record<string, unknown>)) {
      if (typeof member !== 'function') continue;
      try {
        // Event subscribers take a callback; invoke methods take plain values.
        // Passing a function satisfies both without either throwing.
        await (member as AnyFn)(() => undefined);
      } catch {
        // A method that rejects still recorded its channel, which is all we test.
      }
    }
  }
}

describe('preload bridge', () => {
  it('exposes an API on the main world', () => {
    expect(spy.exposed).toBeDefined();
    expect(Object.keys(spy.exposed!).length).toBeGreaterThan(0);
  });

  it('covers every invoke channel declared in shared/ipc', async () => {
    await callEveryMethod(spy.exposed!);
    const invokeChannels = Object.values(IPC).filter(
      (c) => !(EVENT_CHANNELS as readonly string[]).includes(c),
    );
    const uncovered = invokeChannels.filter((c) => !spy.invoked.includes(c));
    expect(uncovered).toEqual([]);
  });

  it('subscribes to every event channel', () => {
    const uncovered = EVENT_CHANNELS.filter((c) => !spy.subscribed.includes(c));
    expect(uncovered).toEqual([]);
  });

  it('does not invoke any channel absent from shared/ipc', () => {
    const declared = new Set<string>(Object.values(IPC));
    const undeclared = spy.invoked.filter((c) => !declared.has(c));
    expect(undeclared).toEqual([]);
  });

  it('exposes each invoke channel through exactly one method', () => {
    // Two methods hitting the same channel usually means a copy-paste slip where
    // one of them was meant to point somewhere else.
    const counts = new Map<string, number>();
    for (const c of spy.invoked) counts.set(c, (counts.get(c) ?? 0) + 1);
    const duplicated = [...counts.entries()].filter(([, n]) => n > 1).map(([c]) => c);
    expect(duplicated).toEqual([]);
  });

  it('returns unsubscribe functions from event listeners', () => {
    const events = spy.exposed!['events'] as Record<string, AnyFn>;
    for (const [name, fn] of Object.entries(events)) {
      const off = fn(() => undefined);
      expect(typeof off, `${name} must return an unsubscribe function`).toBe('function');
    }
  });

  it('throws a readable Error when main reports failure', async () => {
    // The whole point of the bridge's unwrap step: renderer code uses try/catch
    // rather than checking .ok at every call site.
    const { ipcRenderer } = await import('electron');
    const stub = vi
      .spyOn(ipcRenderer, 'invoke')
      .mockResolvedValue({ ok: false, error: 'Proxy did not respond.' });

    const settings = spy.exposed!['settings'] as Record<string, AnyFn>;
    await expect(settings['get']!()).rejects.toThrow('Proxy did not respond.');
    stub.mockRestore();
  });
});
