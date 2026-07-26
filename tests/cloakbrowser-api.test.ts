/**
 * Upstream API contract tests for the `cloakbrowser` wrapper.
 *
 * We call the wrapper through `await import('cloakbrowser')` with a locally
 * declared structural type. That cast is a blind spot: it silences `tsc`
 * regardless of what the real signature is, so an upstream argument-order
 * change (or our own misreading of it) compiles cleanly and only fails at
 * runtime, inside a try/catch that reports "binary missing".
 *
 * The bug these tests lock down: `binaryInfo` is
 * `(browserVersion?, releaseChannel?)` — it takes NO license key — while
 * `ensureBinary` is `(licenseKey?, browserVersion?, releaseChannel?)` and does.
 * Passing a saved key as binaryInfo's first argument makes the wrapper parse it
 * as a version pin and throw "Invalid browser version pin", so every user who
 * had activated a license would see the binary reported as permanently missing.
 *
 * These assert against the real installed package, not a mock, so a breaking
 * upstream change fails the suite at the next `npm install`.
 */

import { describe, expect, it } from 'vitest';
import { binaryState } from '../src/main/services/license';

describe('cloakbrowser binaryInfo signature', () => {
  it('rejects a license key in the first position (proves it is a version pin)', async () => {
    const { binaryInfo } = (await import('cloakbrowser')) as unknown as {
      binaryInfo: (...a: unknown[]) => unknown;
    };

    // A realistic license key is not a valid Chromium version pin.
    expect(() => binaryInfo('CB-PRO-EXAMPLE-KEY-1234')).toThrow(/version pin/i);
  });

  it('accepts (browserVersion, releaseChannel) and reports install state', async () => {
    const { binaryInfo } = (await import('cloakbrowser')) as unknown as {
      binaryInfo: (v?: string, c?: string) => { installed: boolean; tier: string; binaryPath: string };
    };

    const info = binaryInfo(undefined, 'stable');
    expect(typeof info.installed).toBe('boolean');
    expect(['free', 'pro']).toContain(info.tier);
    expect(info.binaryPath).toBeTruthy();
  });

  it('accepts an explicit valid version pin', async () => {
    const { binaryInfo } = (await import('cloakbrowser')) as unknown as {
      binaryInfo: (v?: string, c?: string) => { version: string };
    };

    const info = binaryInfo('148.0.7778.215.2', 'stable');
    expect(info.version).toBe('148.0.7778.215.2');
  });

  it('still takes a license key as ensureBinary\'s first argument', async () => {
    const mod = (await import('cloakbrowser')) as unknown as {
      ensureBinary: (k?: string, v?: string, c?: string) => Promise<string>;
    };

    // Arity is part of the contract: if upstream drops the key parameter the
    // Pro download would silently fetch the free binary instead.
    expect(typeof mod.ensureBinary).toBe('function');
    expect(mod.ensureBinary.length).toBeGreaterThanOrEqual(1);
  });
});

describe('binaryState()', () => {
  it('does not report a version-pin error (regression: key passed as pin)', async () => {
    // binaryState swallows throws into { installed: false, error }. Before the
    // fix this returned the "Invalid browser version pin" message whenever a
    // license key was saved, which the UI showed as "binary not installed".
    const state = await binaryState(undefined, 'stable');
    expect(state.error ?? '').not.toMatch(/version pin/i);
  });

  it('surfaces a real pin error when the caller passes a bad version', async () => {
    const state = await binaryState('not-a-version', 'stable');
    expect(state.installed).toBe(false);
    expect(state.error ?? '').toMatch(/version pin/i);
  });
});
