/**
 * Whether a launch needs `--no-sandbox`, and the flags that hide the infobar.
 *
 * The reported symptom was cosmetic — Chromium showing "You are using an
 * unsupported command-line flag: --no-sandbox" on every launch. The cause is
 * not: the wrapper hardcodes `--no-sandbox` in `getDefaultStealthArgs()`, so
 * every session ran with Chromium's renderer sandbox switched off.
 *
 * That matters beyond the yellow bar:
 *
 *  - The sandbox is the boundary that stops a compromised renderer reading the
 *    rest of the profile directory — cookies, saved passwords, tokens. Anti-detect
 *    profiles exist precisely to hold valuable logged-in sessions, so turning it
 *    off is a poor default for this workload specifically.
 *  - The infobar is itself a fingerprinting signal. It changes the window's inner
 *    height by ~40px, so `innerHeight` no longer matches what a real maximized
 *    Chrome on the spoofed screen size would report. A profile that carefully
 *    spoofs a 1920x1080 desktop and then reports an off-by-40 viewport is more
 *    identifiable than one that doesn't bother.
 *
 * So the fix is to stop passing the flag where it isn't needed, and to suppress
 * the bar where it is.
 *
 * Where it is genuinely needed: Linux without user namespaces. Chromium's
 * sandbox needs either unprivileged `CLONE_NEWUSER` or a setuid helper. Inside
 * a container, or on a kernel with `kernel.unprivileged_userns_clone=0`,
 * neither exists and Chromium exits immediately with
 * "Failed to move to new namespace". Refusing to launch there in the name of
 * security would just be a broken app, so the flag is kept — and paired with
 * `--test-type`, which is what actually removes the infobar.
 */

import fs from 'node:fs';

export interface SandboxDecision {
  /** Flags to add to the launch. */
  args: string[];
  /** True when the renderer sandbox is disabled. */
  disabled: boolean;
  /** Explanation for the session log — a disabled sandbox should never be silent. */
  reason: string;
}

/**
 * Read whether this kernel permits the unprivileged user namespaces Chromium's
 * sandbox depends on.
 *
 * Returns `undefined` when the answer cannot be determined (the sysctl is absent
 * on kernels that always allow it, e.g. mainline >= 4.9 without the Debian
 * patch), which the caller treats as "allowed".
 */
export function unprivilegedUsernsAllowed(): boolean | undefined {
  // Debian/Ubuntu-specific knob; absent on kernels where the feature is
  // unconditionally on.
  const knob = '/proc/sys/kernel/unprivileged_userns_clone';
  try {
    if (fs.existsSync(knob)) {
      return fs.readFileSync(knob, 'utf-8').trim() !== '0';
    }
  } catch {
    /* unreadable — fall through */
  }
  // Present on all modern kernels; 0 means user namespaces are unavailable.
  try {
    const max = '/proc/sys/user/max_user_namespaces';
    if (fs.existsSync(max)) {
      const n = Number.parseInt(fs.readFileSync(max, 'utf-8').trim(), 10);
      if (Number.isFinite(n)) return n > 0;
    }
  } catch {
    /* unreadable — fall through */
  }
  return undefined;
}

/** True when the process looks like it is running inside a container. */
export function looksContainerised(): boolean {
  try {
    if (fs.existsSync('/.dockerenv')) return true;
  } catch {
    /* ignore */
  }
  try {
    // A container runtime leaves its name in the cgroup path.
    const cgroup = fs.readFileSync('/proc/1/cgroup', 'utf-8');
    if (/docker|kubepods|containerd|lxc|podman/i.test(cgroup)) return true;
  } catch {
    /* not Linux, or no permission */
  }
  return false;
}

/**
 * Decide the sandbox flags for this machine.
 *
 * @param platform  `process.platform`, injectable for tests.
 * @param probe     Environment probes, injectable for tests.
 */
export function resolveSandboxArgs(
  platform: NodeJS.Platform = process.platform,
  probe: {
    usernsAllowed?: () => boolean | undefined;
    containerised?: () => boolean;
    forceNoSandbox?: boolean;
  } = {},
): SandboxDecision {
  // An explicit escape hatch, because no amount of probing beats a user who
  // knows their own machine and just needs the browser to start.
  if (probe.forceNoSandbox) {
    return {
      args: ['--no-sandbox', '--test-type'],
      disabled: true,
      reason:
        'Sandbox disabled by CLOAKBROWSER_HUB_NO_SANDBOX=1. The infobar is suppressed, but the ' +
        'renderer runs unsandboxed — unset the variable once the launch problem is resolved.',
    };
  }

  // Windows and macOS ship a working sandbox with no kernel prerequisites, so
  // there is never a reason to disable it there.
  if (platform !== 'linux') {
    return { args: [], disabled: false, reason: 'Renderer sandbox enabled.' };
  }

  const userns = (probe.usernsAllowed ?? unprivilegedUsernsAllowed)();
  const contained = (probe.containerised ?? looksContainerised)();

  // Only `false` forces the flag. `undefined` means the sysctl is absent, which
  // on a modern kernel means user namespaces are simply always available.
  if (userns === false) {
    return {
      args: ['--no-sandbox', '--test-type'],
      disabled: true,
      reason:
        'This kernel does not allow unprivileged user namespaces, which Chromium’s sandbox requires, ' +
        'so the session runs with --no-sandbox. The infobar is suppressed via --test-type.',
    };
  }

  if (contained) {
    // Containers usually mask the sandbox even when the sysctl looks permissive
    // (seccomp profile, missing CAP_SYS_ADMIN). Chromium failing to start is a
    // worse outcome than a documented downgrade.
    return {
      args: ['--no-sandbox', '--test-type'],
      disabled: true,
      reason:
        'Running inside a container, where Chromium’s sandbox is usually unavailable, so the session ' +
        'runs with --no-sandbox. The infobar is suppressed via --test-type.',
    };
  }

  return {
    args: [],
    disabled: false,
    reason: 'Renderer sandbox enabled (no --no-sandbox needed on this machine).',
  };
}

/** Read the override from the environment. */
export function noSandboxOverride(env: NodeJS.ProcessEnv = process.env): boolean {
  const v = (env.CLOAKBROWSER_HUB_NO_SANDBOX ?? '').trim().toLowerCase();
  return v === '1' || v === 'true' || v === 'yes';
}
