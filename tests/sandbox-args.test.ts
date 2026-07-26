/**
 * Sandbox-flag tests.
 *
 * The reported symptom was the yellow "Вы используете неподдерживаемый флаг
 * командной строки: --no-sandbox" bar on every launch. The flag came from the
 * wrapper's own `getDefaultStealthArgs()`, so every session was also running with
 * Chromium's renderer sandbox switched off — on a tool whose whole purpose is
 * holding valuable logged-in sessions in a profile directory.
 *
 * So these tests assert two separate things, and the second is the one that
 * actually protects the user:
 *
 *   1. Where the sandbox works, no --no-sandbox is passed at all.
 *   2. Where it genuinely cannot work, the flag is passed *with* --test-type, so
 *      the browser still starts and the infobar (which also shifts innerHeight
 *      by ~40px and breaks viewport coherence) stays hidden.
 */

import { describe, expect, it } from 'vitest';
import { noSandboxOverride, resolveSandboxArgs } from '../src/main/browser/sandbox-args';

const usable = { usernsAllowed: () => true, containerised: () => false };
const noUserns = { usernsAllowed: () => false, containerised: () => false };
const container = { usernsAllowed: () => true, containerised: () => true };

describe('resolveSandboxArgs — keep the sandbox where it works', () => {
  it('passes no sandbox flags on Windows', () => {
    const r = resolveSandboxArgs('win32', usable);
    expect(r.args).toEqual([]);
    expect(r.disabled).toBe(false);
  });

  it('passes no sandbox flags on macOS', () => {
    expect(resolveSandboxArgs('darwin', usable).args).toEqual([]);
  });

  it('passes no sandbox flags on a Linux host with user namespaces', () => {
    const r = resolveSandboxArgs('linux', usable);
    expect(r.args).toEqual([]);
    expect(r.disabled).toBe(false);
  });

  it('never emits --no-sandbox when the sandbox is usable', () => {
    for (const platform of ['win32', 'darwin', 'linux'] as const) {
      expect(resolveSandboxArgs(platform, usable).args).not.toContain('--no-sandbox');
    }
  });

  it('treats an undetectable kernel setting as permissive, not as broken', () => {
    // The Debian sysctl is absent on kernels where user namespaces are always
    // on. Reading "unknown" as "disable the sandbox" would needlessly downgrade
    // security on most modern distros.
    const r = resolveSandboxArgs('linux', { usernsAllowed: () => undefined, containerised: () => false });
    expect(r.disabled).toBe(false);
    expect(r.args).toEqual([]);
  });
});

describe('resolveSandboxArgs — suppress the infobar where the flag is required', () => {
  it('disables the sandbox when the kernel forbids unprivileged user namespaces', () => {
    const r = resolveSandboxArgs('linux', noUserns);
    expect(r.args).toContain('--no-sandbox');
    expect(r.disabled).toBe(true);
  });

  it('pairs --no-sandbox with --test-type, which is what hides the bar', () => {
    // Without --test-type the user still sees the infobar, which was the whole
    // complaint — and the ~40px it steals from innerHeight is a fingerprint
    // inconsistency on top.
    expect(resolveSandboxArgs('linux', noUserns).args).toContain('--test-type');
  });

  it('disables the sandbox inside a container, where it is usually masked', () => {
    const r = resolveSandboxArgs('linux', container);
    expect(r.args).toEqual(['--no-sandbox', '--test-type']);
    expect(r.disabled).toBe(true);
  });

  it('explains why, so a disabled sandbox is never silent', () => {
    const r = resolveSandboxArgs('linux', noUserns);
    expect(r.reason).toMatch(/user namespace/i);
    expect(r.reason).toMatch(/--no-sandbox/);
  });

  it('does not disable the sandbox on Windows just because the probe says container', () => {
    // The container probe reads Linux-only paths; it must not influence a
    // platform that has a working sandbox unconditionally.
    expect(resolveSandboxArgs('win32', container).disabled).toBe(false);
  });
});

describe('resolveSandboxArgs — explicit override', () => {
  it('honours the escape hatch on any platform', () => {
    for (const platform of ['win32', 'darwin', 'linux'] as const) {
      const r = resolveSandboxArgs(platform, { ...usable, forceNoSandbox: true });
      expect(r.args).toEqual(['--no-sandbox', '--test-type']);
      expect(r.disabled).toBe(true);
    }
  });

  it('names the variable in the reason so the state is reversible', () => {
    const r = resolveSandboxArgs('linux', { ...usable, forceNoSandbox: true });
    expect(r.reason).toMatch(/CLOAKBROWSER_HUB_NO_SANDBOX/);
  });
});

describe('noSandboxOverride', () => {
  it('accepts the usual truthy spellings', () => {
    for (const v of ['1', 'true', 'yes', 'TRUE', ' Yes ']) {
      expect(noSandboxOverride({ CLOAKBROWSER_HUB_NO_SANDBOX: v })).toBe(true);
    }
  });

  it('is off when absent or explicitly falsy', () => {
    expect(noSandboxOverride({})).toBe(false);
    for (const v of ['', '0', 'false', 'no', 'maybe']) {
      expect(noSandboxOverride({ CLOAKBROWSER_HUB_NO_SANDBOX: v })).toBe(false);
    }
  });
});
