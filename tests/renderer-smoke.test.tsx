/**
 * @vitest-environment jsdom
 *
 * Renderer smoke tests.
 *
 * Typecheck and bundling both pass on a component that throws the moment it
 * mounts, so this file actually renders the shell against a stubbed bridge. It is
 * the cheapest guard against the class of bug that would otherwise only appear as
 * a blank window on a machine that can run Electron.
 */

import { render } from 'preact';
import { act } from 'preact/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AppSettings, ProfileRow } from '../src/shared/types';
import { newProfile } from '../src/shared/defaults';
import { App } from '../src/renderer/App';
import { HubProvider } from '../src/renderer/state';
import { ToastProvider } from '../src/renderer/components/toast';

const settings: AppSettings = {
  releaseChannel: 'stable',
  maxConcurrentSessions: 5,
  saveCookiesOnClose: true,
  closeSessionsOnQuit: true,
  theme: 'dark',
  defaultPlatform: 'windows',
  automation: { enabled: false, port: 3777, token: 'test-token' },
};

function row(name: string): ProfileRow {
  return { ...newProfile(name, 'windows'), status: 'idle' };
}

/** Minimal stand-in for the preload bridge, recording what the UI asked for. */
function makeBridge(profiles: ProfileRow[]) {
  return {
    profiles: {
      list: vi.fn(async () => profiles),
      get: vi.fn(async (id: string) => profiles.find((p) => p.id === id)),
      create: vi.fn(async () => row('New')),
      update: vi.fn(async () => profiles[0]!),
      remove: vi.fn(async () => true),
      duplicate: vi.fn(async () => row('Copy')),
      exportToFile: vi.fn(async () => null),
      importFromFile: vi.fn(async () => null),
      randomizeFingerprint: vi.fn(async () => profiles[0]!),
      openDir: vi.fn(async () => undefined),
      previewArgs: vi.fn(async () => ({ args: [], proxy: '', geoip: false, headless: false })),
    },
    sessions: {
      start: vi.fn(async () => undefined),
      stop: vi.fn(async () => undefined),
      stopAll: vi.fn(async () => undefined),
      list: vi.fn(async () => []),
      logs: vi.fn(async () => []),
    },
    cookies: {
      pickFiles: vi.fn(async () => []),
      validateFile: vi.fn(),
      validateText: vi.fn(),
      importFiles: vi.fn(),
      importText: vi.fn(),
      exportToFile: vi.fn(),
      clear: vi.fn(),
      summary: vi.fn(async () => ({ count: 0, domains: [] })),
    },
    proxies: {
      list: vi.fn(async () => []),
      add: vi.fn(),
      addBulk: vi.fn(),
      update: vi.fn(),
      remove: vi.fn(),
      check: vi.fn(),
      checkSaved: vi.fn(),
      parse: vi.fn(),
      rotate: vi.fn(),
    },
    license: {
      state: vi.fn(async () => ({
        tier: 'free' as const,
        valid: true,
        localSessions: 0,
        seatHint: 1,
        maskedKey: 'cb_12…ef',
      })),
      activate: vi.fn(),
      signInWithGithub: vi.fn(),
      logout: vi.fn(),
      openPricing: vi.fn(),
    },
    binary: {
      state: vi.fn(async () => ({ installed: true, version: '150.0.1', tier: 'free' as const })),
      download: vi.fn(),
    },
    importer: { discover: vi.fn(async () => []), importProfile: vi.fn() },
    settings: { get: vi.fn(async () => settings), update: vi.fn(async () => settings) },
    automation: {
      // `listening: false` while `enabled: false` is the consistent pair; the
      // enabled-but-not-bound disagreement is asserted separately below.
      state: vi.fn(async () => ({
        settings: settings.automation,
        listening: false,
        baseUrl: `http://127.0.0.1:${settings.automation.port}`,
      })),
      set: vi.fn(async (patch: Partial<typeof settings.automation>) => ({
        settings: { ...settings.automation, ...patch },
        listening: patch.enabled ?? false,
        baseUrl: `http://127.0.0.1:${patch.port ?? settings.automation.port}`,
      })),
      rotateToken: vi.fn(async () => ({
        settings: { ...settings.automation, token: 'rotated-token' },
        listening: false,
        baseUrl: `http://127.0.0.1:${settings.automation.port}`,
      })),
      endpoint: vi.fn(async () => undefined),
    },
    app: {
      info: vi.fn(async () => ({
        version: '0.1.0',
        platform: 'linux',
        arch: 'x64',
        electron: '33.0.0',
        chrome: '130',
        node: '20',
        userData: '/tmp/u',
        profilesDir: '/tmp/u/profiles',
        localePresets: [],
      })),
      pickDir: vi.fn(async () => null),
      openExternal: vi.fn(async () => undefined),
      openPath: vi.fn(async () => undefined),
    },
    events: {
      onSessions: vi.fn(() => () => undefined),
      onLog: vi.fn(() => () => undefined),
      onProfilesChanged: vi.fn(() => () => undefined),
      onBinaryProgress: vi.fn(() => () => undefined),
    },
  };
}

let host: HTMLDivElement;
let bridge: ReturnType<typeof makeBridge>;
let rows: ProfileRow[];

/**
 * Flush everything Preact has queued.
 *
 * Effects are not run during `render()`: Preact defers them behind
 * `options.requestAnimationFrame`, which in jsdom is a real ~16 ms timer, so
 * awaiting microtasks alone would leave the provider's mount effect unexecuted
 * and the shell stuck on "Loading…". `act` swaps both the rAF hook and the
 * render debounce for queues it drains itself, which makes the flush
 * deterministic instead of timing-dependent.
 *
 * Several passes are needed because the work is a chain, not a single tick: the
 * mount effect awaits four bridge calls, the resulting state change re-renders
 * the shell, and only then do the page components mount and run their own
 * effects.
 */
async function settle(passes = 5): Promise<void> {
  for (let i = 0; i < passes; i++) {
    await act(async () => {
      await Promise.resolve();
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

async function mount(): Promise<void> {
  await act(() => {
    render(
      <ToastProvider>
        <HubProvider>
          <App />
        </HubProvider>
      </ToastProvider>,
      host,
    );
  });
  await settle();
}

/** Click through `act` so the resulting state updates and effects are applied. */
async function click(el: Element): Promise<void> {
  await act(() => {
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
  });
  await settle(3);
}

function button(label: string): HTMLButtonElement {
  const found = [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === label);
  if (!found) {
    throw new Error(
      `No button labelled “${label}”. Buttons present: ${[...host.querySelectorAll('button')]
        .map((b) => `“${b.textContent?.trim()}”`)
        .join(', ')}`,
    );
  }
  return found;
}

beforeEach(() => {
  host = document.createElement('div');
  document.body.appendChild(host);
  rows = [row('Alpha'), row('Beta')];
  bridge = makeBridge(rows);
  (globalThis as unknown as { window: { hub: unknown } }).window.hub = bridge;
});

afterEach(() => {
  render(null, host);
  host.remove();
  delete document.documentElement.dataset['theme'];
});

describe('App shell', () => {
  it('mounts without throwing and shows the navigation', async () => {
    await mount();
    expect(host.textContent).toContain('CloakBrowser');
    for (const label of ['Profiles', 'Proxies', 'Import', 'License', 'Settings']) {
      expect(host.textContent).toContain(label);
    }
  });

  it('leaves the loading state once the initial fetches resolve', async () => {
    await mount();
    // "Loading…" hiding is the signal that the provider's mount effect ran to
    // completion; every other assertion in this file depends on it.
    expect(host.textContent).not.toContain('Loading…');
    expect(host.querySelector('.topbar h1')?.textContent).toBe('Profiles');
  });

  it('loads profiles, settings and binary state on startup', async () => {
    await mount();
    expect(bridge.profiles.list).toHaveBeenCalled();
    expect(bridge.settings.get).toHaveBeenCalled();
    expect(bridge.app.info).toHaveBeenCalled();
    expect(bridge.binary.state).toHaveBeenCalled();
    // The license lookup is deliberately fired after first paint, not awaited
    // with the rest, so it must still have happened by now.
    expect(bridge.license.state).toHaveBeenCalledWith(true);
  });

  it('applies the stored theme to the document', async () => {
    await mount();
    expect(document.documentElement.dataset['theme']).toBe('dark');
  });

  it('renders the profile rows returned by the bridge', async () => {
    await mount();
    expect(host.textContent).toContain('Alpha');
    expect(host.textContent).toContain('Beta');
    expect(host.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('subscribes to every live event channel', async () => {
    await mount();
    expect(bridge.events.onProfilesChanged).toHaveBeenCalled();
    expect(bridge.events.onSessions).toHaveBeenCalled();
    expect(bridge.events.onLog).toHaveBeenCalled();
  });

  it('shows an empty state when there are no profiles', async () => {
    bridge = makeBridge([]);
    (globalThis as unknown as { window: { hub: unknown } }).window.hub = bridge;
    await mount();
    expect(host.textContent).toContain('No profiles yet');
  });

  it('starts a session when Start is clicked', async () => {
    await mount();
    await click(button('Start'));
    expect(bridge.sessions.start).toHaveBeenCalledTimes(1);
    // The first table row is the first profile the bridge returned, so the id
    // passed through proves the row-to-handler wiring, not just that a click fired.
    expect(bridge.sessions.start).toHaveBeenCalledWith(rows[0]!.id);
  });

  it('navigates to other pages without throwing', async () => {
    await mount();
    for (const label of ['Proxies', 'Import', 'License', 'Settings', 'Profiles']) {
      const nav = [...host.querySelectorAll('button.nav-item')].find((b) =>
        b.textContent?.includes(label),
      );
      expect(nav, `nav item ${label}`).toBeTruthy();
      await click(nav!);
      // Each page renders its own <h1>; a crash would leave the region empty.
      expect(host.querySelector('.topbar h1')?.textContent).toBe(label);
    }
  });

  it('surfaces a failed bridge call as a toast instead of crashing', async () => {
    bridge.sessions.start = vi.fn(async () => {
      throw new Error('Session limit reached for your plan.');
    });
    await mount();
    await click(button('Start'));
    // The toast layer lives outside `host`, hence the body-level assertion.
    expect(document.body.textContent).toContain('Session limit reached');
    // A rejected call must not tear the tree down.
    expect(host.querySelector('.topbar h1')?.textContent).toBe('Profiles');
  });
});

/**
 * The automation card is tested through the shell rather than in isolation
 * because its whole reason to exist is showing when the stored setting and the
 * actual listening state disagree — which only makes sense against the real
 * bridge contract.
 */
describe('Automation settings card', () => {
  async function openSettings(): Promise<void> {
    await mount();
    const nav = [...host.querySelectorAll('button.nav-item')].find((b) =>
      b.textContent?.includes('Settings'),
    );
    await click(nav!);
  }

  it('renders the card and asks the bridge for its state', async () => {
    await openSettings();
    expect(host.textContent).toContain('Automation API');
    expect(bridge.automation.state).toHaveBeenCalled();
    // Left in "Loading…" would mean the effect never resolved.
    expect(host.textContent).not.toContain('Loading…');
  });

  it('hides the token and the quick start while the API is disabled', async () => {
    await openSettings();
    // Disabled means nothing is listening, so a snippet would be a lie.
    expect(host.textContent).not.toContain('Quick start');
    const token = host.querySelector<HTMLInputElement>('input[type="password"]');
    expect(token, 'token input').toBeTruthy();
    expect(token!.value).toBe('test-token');
  });

  it('enables the API through the bridge when the box is ticked', async () => {
    await openSettings();
    const box = [...host.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')].find((el) =>
      el.closest('label')?.textContent?.includes('Enable the local automation API'),
    );
    expect(box, 'automation checkbox').toBeTruthy();
    await act(() => {
      box!.checked = true;
      box!.dispatchEvent(new Event('change', { bubbles: true }));
    });
    await settle(3);
    expect(bridge.automation.set).toHaveBeenCalledWith({ enabled: true });
    // The mock reports it bound, so the card must now offer the snippet.
    expect(host.textContent).toContain('Quick start');
    expect(host.textContent).toContain('127.0.0.1:3777');
  });

  it('warns when the API is enabled but nothing is listening', async () => {
    // The port-in-use case: stored as on, server never bound.
    bridge.automation.state = vi.fn(async () => ({
      settings: { enabled: true, port: 3777, token: 'test-token' },
      listening: false,
      baseUrl: 'http://127.0.0.1:3777',
    }));
    await openSettings();
    expect(host.textContent).toContain('enabled but not listening');
    expect(host.querySelector('.callout.warn')).toBeTruthy();
  });

  it('rejects an out-of-range port without calling the bridge', async () => {
    await openSettings();
    const portInput = [...host.querySelectorAll<HTMLInputElement>('input[type="text"]')].find(
      (el) => el.value === '3777',
    );
    expect(portInput, 'port input').toBeTruthy();
    await act(() => {
      portInput!.value = '80';
      portInput!.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await act(() => {
      portInput!.dispatchEvent(new FocusEvent('blur', { bubbles: true }));
    });
    await settle(3);
    // Privileged ports are refused locally; the field snaps back.
    expect(bridge.automation.set).not.toHaveBeenCalled();
    expect(portInput!.value).toBe('3777');
    expect(document.body.textContent).toContain('between 1024 and 65535');
  });

  it('rotates the token on request', async () => {
    await openSettings();
    await click(button('Rotate'));
    expect(bridge.automation.rotateToken).toHaveBeenCalledTimes(1);
    const token = host.querySelector<HTMLInputElement>('input[type="password"]');
    expect(token!.value).toBe('rotated-token');
  });
});
