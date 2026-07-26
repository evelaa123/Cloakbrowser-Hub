import { describe, expect, it } from 'vitest';
import { newProfile, randomFingerprint } from '../src/shared/defaults';
import { buildFingerprintArgs, buildProxyOption, proxyLabel, resolveLaunch } from '../src/shared/fingerprint-args';
import type { Profile } from '../src/shared/types';

function profile(mutate: (p: Profile) => void = () => {}): Profile {
  const p = newProfile('Test', 'windows', 'test-id');
  mutate(p);
  return p;
}

/** Read the value of a flag out of the built arg list. */
function flag(args: string[], name: string): string | undefined {
  const hit = args.find((a) => a === name || a.startsWith(`${name}=`));
  if (!hit) return undefined;
  const eq = hit.indexOf('=');
  return eq === -1 ? '' : hit.slice(eq + 1);
}

describe('buildFingerprintArgs', () => {
  it('always pins the seed and platform so the identity is reproducible', () => {
    const p = profile((x) => {
      x.fingerprint.seed = 12345;
    });
    const args = buildFingerprintArgs(p);
    expect(flag(args, '--fingerprint')).toBe('12345');
    expect(flag(args, '--fingerprint-platform')).toBe('windows');
  });

  it('still emits a seed when the field is empty, rather than shipping no fingerprint', () => {
    // The Hub launches with `stealthArgs: false` so it can drop the wrapper's
    // hardcoded --no-sandbox. That also means the wrapper's default
    // `--fingerprint=<random>` no longer applies, so omitting the flag here
    // would start a browser with no spoofing at all while the UI still showed
    // the profile as protected.
    const args = buildFingerprintArgs(profile((x) => { x.fingerprint.seed = undefined; }));
    const seed = flag(args, '--fingerprint');
    expect(seed).toBeDefined();
    expect(Number(seed)).toBeGreaterThan(0);
  });

  it('derives the fallback seed deterministically, so the device identity is stable', () => {
    // A seed that re-rolls per launch would make one account look like a
    // different machine every session — the opposite of what a profile is for.
    const mk = () => profile((x) => { x.fingerprint.seed = undefined; x.id = 'stable-id'; });
    expect(flag(buildFingerprintArgs(mk()), '--fingerprint')).toBe(
      flag(buildFingerprintArgs(mk()), '--fingerprint'),
    );
  });

  it('gives different profiles different fallback seeds', () => {
    const a = buildFingerprintArgs(profile((x) => { x.fingerprint.seed = undefined; x.id = 'aaa'; }));
    const b = buildFingerprintArgs(profile((x) => { x.fingerprint.seed = undefined; x.id = 'bbb'; }));
    expect(flag(a, '--fingerprint')).not.toBe(flag(b, '--fingerprint'));
  });

  it('keeps the fallback seed in the range the wrapper itself uses', () => {
    for (const id of ['x', 'profile-42', 'a'.repeat(64), '☃']) {
      const n = Number(flag(buildFingerprintArgs(profile((x) => { x.fingerprint.seed = undefined; x.id = id; })), '--fingerprint'));
      expect(n).toBeGreaterThanOrEqual(10000);
      expect(n).toBeLessThanOrEqual(99999);
    }
  });

  it('leaves auto values to the binary instead of guessing them', () => {
    const args = buildFingerprintArgs(profile());
    expect(flag(args, '--fingerprint-screen-width')).toBeUndefined();
    expect(flag(args, '--fingerprint-gpu-vendor')).toBeUndefined();
    expect(flag(args, '--fingerprint-hardware-concurrency')).toBeUndefined();
    expect(flag(args, '--fingerprint-device-memory')).toBeUndefined();
  });

  it('emits explicit flags for every manually pinned value', () => {
    const p = profile((x) => {
      x.fingerprint.screen = { mode: 'manual', width: 2560, height: 1440 };
      x.fingerprint.gpu = { mode: 'manual', vendor: 'Google Inc. (Intel)', renderer: 'ANGLE (Intel)' };
      x.fingerprint.cpuCores = { mode: 'manual', value: 12 };
      x.fingerprint.deviceMemory = { mode: 'manual', value: 16 };
      x.fingerprint.platformVersion = '15.0.0';
      x.fingerprint.taskbarHeight = 48;
    });
    const args = buildFingerprintArgs(p);
    expect(flag(args, '--fingerprint-screen-width')).toBe('2560');
    expect(flag(args, '--fingerprint-screen-height')).toBe('1440');
    expect(flag(args, '--fingerprint-gpu-vendor')).toBe('Google Inc. (Intel)');
    expect(flag(args, '--fingerprint-gpu-renderer')).toBe('ANGLE (Intel)');
    expect(flag(args, '--fingerprint-hardware-concurrency')).toBe('12');
    expect(flag(args, '--fingerprint-device-memory')).toBe('16');
    expect(flag(args, '--fingerprint-platform-version')).toBe('15.0.0');
    expect(flag(args, '--fingerprint-taskbar-height')).toBe('48');
  });

  it('raises the storage quota by default so the profile is not read as incognito', () => {
    const quota = Number(flag(buildFingerprintArgs(profile()), '--fingerprint-storage-quota'));
    // Asserted as a range, not an exact number: the point of the flag is to clear
    // BrowserScan's incognito threshold while still describing a plausible disk,
    // and pinning the literal would make tuning it a test edit.
    expect(quota).toBeGreaterThanOrEqual(50000);
  });

  it('only sends the noise flag when noise is switched off', () => {
    expect(flag(buildFingerprintArgs(profile()), '--fingerprint-noise')).toBeUndefined();
    const off = buildFingerprintArgs(profile((x) => { x.fingerprint.noise = false; }));
    expect(flag(off, '--fingerprint-noise')).toBe('false');
  });

  it('omits the brand flag for Chrome and sets it for other brands', () => {
    expect(flag(buildFingerprintArgs(profile()), '--fingerprint-brand')).toBeUndefined();
    const edge = buildFingerprintArgs(profile((x) => { x.fingerprint.brand = 'Edge'; }));
    expect(flag(edge, '--fingerprint-brand')).toBe('Edge');
  });

  it('ignores windows font metrics when the profile is not spoofing Windows', () => {
    const mac = buildFingerprintArgs(
      profile((x) => {
        x.fingerprint.platform = 'macos';
        x.fingerprint.windowsFontMetrics = true;
      }),
    );
    expect(flag(mac, '--fingerprint-windows-font-metrics')).toBeUndefined();
  });

  describe('WebRTC', () => {
    it('skips auto spoofing without a proxy — a spoofed ICE IP on a direct link is itself a tell', () => {
      const args = buildFingerprintArgs(profile((x) => { x.fingerprint.webrtc = { mode: 'auto' }; }));
      expect(flag(args, '--fingerprint-webrtc-ip')).toBeUndefined();
    });

    it('spoofs to the proxy exit IP when a proxy is configured', () => {
      const args = buildFingerprintArgs(
        profile((x) => {
          x.fingerprint.webrtc = { mode: 'auto' };
          x.proxy = { kind: 'http', host: '1.2.3.4', port: 8080 };
        }),
      );
      expect(flag(args, '--fingerprint-webrtc-ip')).toBe('auto');
    });

    it('honours a manually pinned IP with no proxy requirement', () => {
      const args = buildFingerprintArgs(profile((x) => { x.fingerprint.webrtc = { mode: 'manual', ip: '9.9.9.9' }; }));
      expect(flag(args, '--fingerprint-webrtc-ip')).toBe('9.9.9.9');
    });
  });

  describe('locale and timezone', () => {
    it('sets --lang, --fingerprint-locale and --fingerprint-timezone when pinned', () => {
      const args = buildFingerprintArgs(
        profile((x) => { x.locale = { mode: 'manual', locale: 'de-DE', timezone: 'Europe/Berlin' }; }),
      );
      expect(flag(args, '--lang')).toBe('de-DE');
      expect(flag(args, '--fingerprint-locale')).toBe('de-DE');
      expect(flag(args, '--fingerprint-timezone')).toBe('Europe/Berlin');
    });

    it('emits nothing in ip mode — the wrapper resolves it from the exit IP', () => {
      const args = buildFingerprintArgs(profile((x) => { x.locale = { mode: 'ip' }; }));
      expect(flag(args, '--lang')).toBeUndefined();
      expect(flag(args, '--fingerprint-timezone')).toBeUndefined();
    });
  });

  describe('geolocation', () => {
    it('pins coordinates in manual mode', () => {
      const args = buildFingerprintArgs(
        profile((x) => { x.geo = { mode: 'manual', latitude: 52.52, longitude: 13.405 }; }),
      );
      expect(flag(args, '--fingerprint-location')).toBe('52.52,13.405');
    });

    it('emits nothing in ip or off mode', () => {
      expect(flag(buildFingerprintArgs(profile((x) => { x.geo = { mode: 'ip' }; })), '--fingerprint-location')).toBeUndefined();
      expect(flag(buildFingerprintArgs(profile((x) => { x.geo = { mode: 'off' }; })), '--fingerprint-location')).toBeUndefined();
    });
  });

  describe('extra args', () => {
    it('passes through unrelated user flags', () => {
      const args = buildFingerprintArgs(profile((x) => { x.startup.extraArgs = ['--disable-gpu', '--mute-audio']; }));
      expect(args).toContain('--disable-gpu');
      expect(args).toContain('--mute-audio');
    });

    it('refuses to let a user flag hijack the profile identity', () => {
      const args = buildFingerprintArgs(
        profile((x) => {
          x.fingerprint.seed = 111;
          x.startup.extraArgs = ['--fingerprint=999', '--fingerprint-platform=linux'];
        }),
      );
      expect(flag(args, '--fingerprint')).toBe('111');
      expect(flag(args, '--fingerprint-platform')).toBe('windows');
    });

    it('allows an identity flag the profile does not set', () => {
      const args = buildFingerprintArgs(profile((x) => { x.startup.extraArgs = ['--fingerprint-sapi-voices=false']; }));
      expect(flag(args, '--fingerprint-sapi-voices')).toBe('false');
    });

    it('ignores entries that are not flags', () => {
      const args = buildFingerprintArgs(profile((x) => { x.startup.extraArgs = ['not-a-flag', '  ', '--ok']; }));
      expect(args).toContain('--ok');
      expect(args).not.toContain('not-a-flag');
    });

    it('produces no duplicate flag keys', () => {
      const args = buildFingerprintArgs(profile((x) => { x.startup.extraArgs = ['--mute-audio', '--mute-audio']; }));
      const keys = args.map((a) => a.split('=')[0]);
      expect(new Set(keys).size).toBe(keys.length);
    });
  });
});

describe('buildProxyOption', () => {
  it('returns undefined for a direct connection', () => {
    expect(buildProxyOption(profile())).toBeUndefined();
  });

  it('builds a server URL and keeps credentials in separate fields', () => {
    const out = buildProxyOption(
      profile((x) => { x.proxy = { kind: 'http', host: 'p.io', port: 8080, username: 'u', password: 'p@ss:/w' }; }),
    );
    // Credentials stay out of the URL so odd characters cannot corrupt it.
    expect(out).toEqual({ server: 'http://p.io:8080', username: 'u', password: 'p@ss:/w' });
  });

  it('uses the socks5 scheme for SOCKS proxies', () => {
    const out = buildProxyOption(profile((x) => { x.proxy = { kind: 'socks5', host: 'p.io', port: 1080 }; }));
    expect(out).toMatchObject({ server: 'socks5://p.io:1080' });
  });

  it('passes bypass through', () => {
    const out = buildProxyOption(
      profile((x) => { x.proxy = { kind: 'http', host: 'p.io', port: 80, bypass: '.local' }; }),
    );
    expect(out).toMatchObject({ bypass: '.local' });
  });

  it('treats an incomplete proxy as no proxy', () => {
    expect(buildProxyOption(profile((x) => { x.proxy = { kind: 'http', host: 'p.io' }; }))).toBeUndefined();
  });
});

describe('proxyLabel', () => {
  it('describes a direct connection', () => {
    expect(proxyLabel(profile())).toBe('Direct (no proxy)');
  });
  it('masks the password', () => {
    const label = proxyLabel(
      profile((x) => { x.proxy = { kind: 'socks5', host: 'p.io', port: 1080, username: 'u', password: 'secret' }; }),
    );
    expect(label).toBe('socks5://u:••••@p.io:1080');
    expect(label).not.toContain('secret');
  });
});

describe('resolveLaunch', () => {
  it('enables geoip for ip locale mode with OR without a proxy', () => {
    // Deliberately not gated on having a proxy. A user on a system-wide VPN has
    // no per-profile proxy, but their egress IP is still a foreign exit that the
    // timezone has to match — gating this is what left their sessions reporting
    // the local timezone behind a Vienna IP.
    expect(resolveLaunch(profile()).geoip).toBe(true);
    const withProxy = resolveLaunch(
      profile((x) => { x.proxy = { kind: 'http', host: 'p.io', port: 80 }; }),
    );
    expect(withProxy.geoip).toBe(true);
  });

  it('disables geoip when the locale is pinned manually', () => {
    const out = resolveLaunch(profile((x) => { x.locale = { mode: 'manual', locale: 'de-AT' }; }));
    expect(out.geoip).toBe(false);
  });

  it('passes pinned locale and timezone to the wrapper options', () => {
    const out = resolveLaunch(profile((x) => { x.locale = { mode: 'manual', locale: 'fr-FR', timezone: 'Europe/Paris' }; }));
    expect(out.locale).toBe('fr-FR');
    expect(out.timezone).toBe('Europe/Paris');
    expect(out.geoip).toBe(false);
  });

  it('maps behaviour settings onto the humanize config', () => {
    const out = resolveLaunch(
      profile((x) => {
        x.behaviour = { humanize: true, preset: 'careful', mistypeChance: 0.05, typingDelay: 90, idleBetweenActions: true };
      }),
    );
    expect(out.humanize).toBe(true);
    expect(out.humanPreset).toBe('careful');
    expect(out.humanConfig).toEqual({ mistype_chance: 0.05, typing_delay: 90, idle_between_actions: true });
  });

  it('omits humanConfig when nothing is customised', () => {
    expect(resolveLaunch(profile()).humanConfig).toBeUndefined();
  });

  it('defaults to a headed window, which is what account work needs', () => {
    expect(resolveLaunch(profile()).headless).toBe(false);
  });
});

describe('randomFingerprint', () => {
  it('produces a coherent pinned device for each platform', () => {
    for (const platform of ['windows', 'macos', 'linux'] as const) {
      const fp = randomFingerprint(platform);
      expect(fp.platform).toBe(platform);
      expect(fp.seed).toBeGreaterThanOrEqual(10000);
      expect(fp.seed).toBeLessThanOrEqual(99999);
      expect(fp.screen.mode).toBe('manual');
      expect(fp.screen.width).toBeGreaterThan(0);
      expect(fp.gpu.vendor).toBeTruthy();
      expect(fp.gpu.renderer).toBeTruthy();
    }
  });

  it('pairs Apple GPUs with macOS only', () => {
    for (let i = 0; i < 25; i++) {
      const mac = randomFingerprint('macos');
      expect(mac.gpu.renderer).not.toMatch(/Direct3D11/);
    }
    for (let i = 0; i < 25; i++) {
      const win = randomFingerprint('windows');
      expect(win.gpu.renderer).not.toMatch(/Apple M\d/);
    }
  });
});
