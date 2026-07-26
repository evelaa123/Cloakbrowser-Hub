/**
 * Profile editor.
 *
 * Edits a local draft and only writes it back on Save, so a half-finished
 * fingerprint is never persisted and Cancel is a real cancel. The one exception
 * is the Cookies tab: cookie import writes to the jar immediately, because the
 * jar is a separate file from the profile record and deferring it would mean
 * holding a whole cookie set in renderer memory for no benefit.
 */

import type { JSX } from 'preact';
import { useEffect, useMemo, useState } from 'preact/hooks';
import type {
  BrowserBrand,
  FingerprintPlatform,
  Profile,
  ProxyConfig,
  SavedProxy,
} from '../../shared/types';
import {
  CPU_POOL,
  GPU_POOL,
  LOCALE_PRESETS,
  MEMORY_POOL,
  PLATFORM_VERSION_POOL,
  PROFILE_COLORS,
  SCREEN_POOL,
  randomFingerprint,
  randomSeed,
} from '../../shared/defaults';
import { buildFingerprintArgs, proxyLabel } from '../../shared/fingerprint-args';
import { useToast } from '../components/toast';
import { Callout, Check, Field, Modal, Tabs, timeAgo } from '../components/ui';
import { CookiesTab } from './CookiesTab';

type TabId =
  | 'general'
  | 'fingerprint'
  | 'proxy'
  | 'locale'
  | 'cookies'
  | 'behaviour'
  | 'startup'
  | 'advanced';

const TABS: ReadonlyArray<{ id: TabId; label: string }> = [
  { id: 'general', label: 'General' },
  { id: 'fingerprint', label: 'Fingerprint' },
  { id: 'proxy', label: 'Proxy' },
  { id: 'locale', label: 'Locale & Geo' },
  { id: 'cookies', label: 'Cookies' },
  { id: 'behaviour', label: 'Behaviour' },
  { id: 'startup', label: 'Startup' },
  { id: 'advanced', label: 'Advanced' },
];

const PLATFORMS: Array<{ id: FingerprintPlatform; label: string }> = [
  { id: 'windows', label: 'Windows' },
  { id: 'macos', label: 'macOS' },
  { id: 'linux', label: 'Linux' },
];

const BRANDS: BrowserBrand[] = ['Chrome', 'Edge', 'Opera', 'Vivaldi'];

/** Positive integer from an input, or undefined when blank/invalid. */
function intOrUndef(v: string): number | undefined {
  const n = Number.parseInt(v, 10);
  return Number.isFinite(n) && n >= 0 ? n : undefined;
}

function floatOrUndef(v: string): number | undefined {
  const n = Number.parseFloat(v);
  return Number.isFinite(n) ? n : undefined;
}

export function ProfileEditor(props: { profile: Profile; onClose: () => void }): JSX.Element {
  const toast = useToast();
  const [tab, setTab] = useState<TabId>('general');
  const [draft, setDraft] = useState<Profile>(() => structuredClone(props.profile));
  const [saving, setSaving] = useState(false);
  const [savedProxies, setSavedProxies] = useState<SavedProxy[]>([]);
  const [checking, setChecking] = useState(false);

  // Narrow setters keep the JSX readable instead of repeating deep spreads.
  const patch = (p: Partial<Profile>): void => setDraft((d) => ({ ...d, ...p }));
  const patchFp = (p: Partial<Profile['fingerprint']>): void =>
    setDraft((d) => ({ ...d, fingerprint: { ...d.fingerprint, ...p } }));
  const patchProxy = (p: Partial<ProxyConfig>): void =>
    setDraft((d) => ({ ...d, proxy: { ...d.proxy, ...p } }));
  const patchLocale = (p: Partial<Profile['locale']>): void =>
    setDraft((d) => ({ ...d, locale: { ...d.locale, ...p } }));
  const patchGeo = (p: Partial<Profile['geo']>): void =>
    setDraft((d) => ({ ...d, geo: { ...d.geo, ...p } }));
  const patchBehaviour = (p: Partial<Profile['behaviour']>): void =>
    setDraft((d) => ({ ...d, behaviour: { ...d.behaviour, ...p } }));
  const patchStartup = (p: Partial<Profile['startup']>): void =>
    setDraft((d) => ({ ...d, startup: { ...d.startup, ...p } }));

  useEffect(() => {
    void window.hub.proxies
      .list()
      .then(setSavedProxies)
      .catch(() => undefined);
  }, []);

  const resolvedArgs = useMemo(() => buildFingerprintArgs(draft), [draft]);
  const fp = draft.fingerprint;
  const hasProxy = draft.proxy.kind !== 'none' && !!draft.proxy.host;

  async function save(): Promise<void> {
    if (!draft.name.trim()) {
      toast.warn('Give the profile a name first.');
      setTab('general');
      return;
    }
    setSaving(true);
    const ok = await toast.run(
      () => window.hub.profiles.update(draft.id, { ...draft, name: draft.name.trim() }),
      'Profile saved.',
    );
    setSaving(false);
    if (ok) props.onClose();
  }

  async function checkProxyNow(): Promise<void> {
    if (!hasProxy) {
      toast.warn('Enter a proxy host and port first.');
      return;
    }
    setChecking(true);
    const res = await toast.run(() => window.hub.proxies.check(draft.proxy));
    setChecking(false);
    if (!res) return;
    if (!res.ok) {
      toast.err(res.error ?? 'The proxy did not respond.');
      return;
    }
    toast.ok(
      `Proxy works — ${res.ip}${res.country ? ` (${res.country}${res.city ? `, ${res.city}` : ''})` : ''}${res.latencyMs ? `, ${res.latencyMs} ms` : ''}.`,
    );
    // A verified exit IP is the best source of truth for the timezone, so it is
    // offered rather than making the user retype what the check just found.
    if (res.timezone && draft.locale.mode === 'manual' && !draft.locale.timezone) {
      patchLocale({ timezone: res.timezone });
      toast.info(`Timezone set to ${res.timezone} from the proxy exit IP.`);
    }
  }

  function useSavedProxy(id: string): void {
    if (!id) {
      patchProxy({ kind: 'none', host: undefined, port: undefined, savedProxyId: undefined });
      return;
    }
    const saved = savedProxies.find((p) => p.id === id);
    if (!saved) return;
    patchProxy({
      kind: saved.kind,
      host: saved.host,
      port: saved.port,
      username: saved.username,
      password: saved.password,
      bypass: saved.bypass,
      rotationUrl: saved.rotationUrl,
      savedProxyId: saved.id,
    });
  }

  async function saveToLibrary(): Promise<void> {
    const added = await toast.run(
      () => window.hub.proxies.add(draft.proxy, `${draft.proxy.host}:${draft.proxy.port}`),
      'Added to the proxy library.',
    );
    if (added) setSavedProxies(await window.hub.proxies.list());
  }

  return (
    <Modal
      title={props.profile.name}
      subtitle={`Created ${timeAgo(props.profile.createdAt)} · last run ${timeAgo(props.profile.lastRunAt)}`}
      wide
      onClose={props.onClose}
      footer={
        <>
          <span class="left faint mono">
            {resolvedArgs.length} flag{resolvedArgs.length === 1 ? '' : 's'} · {proxyLabel(draft)}
          </span>
          <button class="btn" onClick={props.onClose}>
            Cancel
          </button>
          <button class="btn primary" onClick={save} disabled={saving}>
            {saving ? 'Saving…' : 'Save'}
          </button>
        </>
      }
    >
      <Tabs tabs={TABS} active={tab} onChange={setTab} />

      <div style={{ paddingTop: 16 }}>
        {/* ------------------------------------------------------------ General */}
        {tab === 'general' ? (
          <>
            <div class="grid2">
              <Field label="Profile name">
                <input
                  type="text"
                  value={draft.name}
                  onInput={(e) => patch({ name: (e.currentTarget as HTMLInputElement).value })}
                />
              </Field>
              <Field label="Tags" hint="Comma separated. Used for search and grouping.">
                <input
                  type="text"
                  value={draft.tags.join(', ')}
                  placeholder="ads, us, warmup"
                  onInput={(e) =>
                    patch({
                      tags: (e.currentTarget as HTMLInputElement).value
                        .split(',')
                        .map((t) => t.trim())
                        .filter(Boolean),
                    })
                  }
                />
              </Field>
            </div>

            <div style={{ marginTop: 14 }}>
              <Field label="Notes">
                <textarea
                  style={{ fontFamily: 'var(--font)', fontSize: '12.5px', minHeight: 64 }}
                  value={draft.notes ?? ''}
                  placeholder="Account, purpose, anything you need to remember."
                  onInput={(e) => patch({ notes: (e.currentTarget as HTMLTextAreaElement).value })}
                />
              </Field>
            </div>

            <div style={{ marginTop: 14 }}>
              <Field label="Colour">
                <div class="row">
                  {PROFILE_COLORS.map((c) => (
                    <button
                      key={c}
                      onClick={() => patch({ color: c })}
                      title={c}
                      aria-label={`Colour ${c}`}
                      style={{
                        width: 22,
                        height: 22,
                        borderRadius: 6,
                        background: c,
                        cursor: 'pointer',
                        border: draft.color === c ? '2px solid var(--text)' : '2px solid transparent',
                      }}
                    />
                  ))}
                </div>
              </Field>
            </div>

            <div class="section-head">Storage</div>
            <div class="stat-grid">
              <div class="stat">
                <div class="k">Cookies</div>
                <div class="v">{draft.cookies?.count ?? 0}</div>
              </div>
              <div class="stat">
                <div class="k">Domains</div>
                <div class="v">{draft.cookies?.domains ?? 0}</div>
              </div>
              <div class="stat">
                <div class="k">Fingerprint seed</div>
                <div class="v small mono">{fp.seed ?? 'random'}</div>
              </div>
            </div>
            <div class="row" style={{ marginTop: 12 }}>
              <button
                class="btn sm"
                onClick={() => void toast.run(() => window.hub.profiles.openDir(draft.id))}
              >
                Open data folder
              </button>
            </div>
          </>
        ) : null}

        {/* -------------------------------------------------------- Fingerprint */}
        {tab === 'fingerprint' ? (
          <>
            <Callout icon="i">
              Values left on <strong>Auto</strong> are derived from the seed by the browser itself and
              stay mutually consistent. Pin a value only when you have a specific reason — a
              hand-picked combination that does not exist in the real world is easier to detect than
              an auto one.
            </Callout>

            <div class="grid2" style={{ marginTop: 16 }}>
              <Field
                label="Fingerprint seed"
                hint="A fixed seed keeps the same device identity across launches. Change it only to become a brand-new device."
              >
                <div class="row" style={{ gap: 6 }}>
                  <input
                    type="number"
                    value={fp.seed ?? ''}
                    placeholder="random each launch"
                    onInput={(e) =>
                      patchFp({ seed: intOrUndef((e.currentTarget as HTMLInputElement).value) })
                    }
                  />
                  <button class="btn sm" onClick={() => patchFp({ seed: randomSeed() })}>
                    New
                  </button>
                </div>
              </Field>

              <Field label="Operating system" hint="What the site sees, regardless of your real OS.">
                <select
                  value={fp.platform}
                  onChange={(e) => {
                    const platform = (e.currentTarget as HTMLSelectElement).value as FingerprintPlatform;
                    // A GPU or resolution pinned for another OS would be an
                    // obvious mismatch, so switching platform resets them.
                    patchFp({
                      platform,
                      gpu: { mode: 'auto' },
                      screen: { mode: 'auto' },
                      platformVersion: undefined,
                      windowsFontMetrics: platform === 'windows' ? fp.windowsFontMetrics : false,
                    });
                  }}
                >
                  {PLATFORMS.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.label}
                    </option>
                  ))}
                </select>
              </Field>
            </div>

            <div class="grid3" style={{ marginTop: 14 }}>
              <Field label="OS version" hint="Client Hints platform version.">
                <select
                  value={fp.platformVersion ?? ''}
                  onChange={(e) =>
                    patchFp({
                      platformVersion: (e.currentTarget as HTMLSelectElement).value || undefined,
                    })
                  }
                >
                  <option value="">Auto</option>
                  {PLATFORM_VERSION_POOL[fp.platform].map((v) => (
                    <option key={v} value={v}>
                      {v}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Browser brand">
                <select
                  value={fp.brand ?? 'Chrome'}
                  onChange={(e) =>
                    patchFp({ brand: (e.currentTarget as HTMLSelectElement).value as BrowserBrand })
                  }
                >
                  {BRANDS.map((b) => (
                    <option key={b} value={b}>
                      {b}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Brand version" hint="Blank = match the binary.">
                <input
                  type="text"
                  value={fp.brandVersion ?? ''}
                  placeholder="Auto"
                  onInput={(e) =>
                    patchFp({ brandVersion: (e.currentTarget as HTMLInputElement).value || undefined })
                  }
                />
              </Field>
            </div>

            <div class="section-head">Hardware</div>

            <Field label="Screen resolution">
              <div class="row">
                <select
                  style={{ maxWidth: 130 }}
                  value={fp.screen.mode}
                  onChange={(e) => {
                    const mode = (e.currentTarget as HTMLSelectElement).value as 'auto' | 'manual';
                    const [w, h] = SCREEN_POOL[fp.platform][0]!;
                    patchFp({
                      screen:
                        mode === 'auto'
                          ? { mode: 'auto' }
                          : {
                              mode: 'manual',
                              width: fp.screen.width ?? w,
                              height: fp.screen.height ?? h,
                            },
                    });
                  }}
                >
                  <option value="auto">Auto</option>
                  <option value="manual">Manual</option>
                </select>
                {fp.screen.mode === 'manual' ? (
                  <select
                    value={`${fp.screen.width}x${fp.screen.height}`}
                    onChange={(e) => {
                      const [w, h] = (e.currentTarget as HTMLSelectElement).value.split('x');
                      patchFp({ screen: { mode: 'manual', width: Number(w), height: Number(h) } });
                    }}
                  >
                    {SCREEN_POOL[fp.platform]
                      .filter((v, i, a) => a.findIndex((x) => x[0] === v[0] && x[1] === v[1]) === i)
                      .map(([w, h]) => (
                        <option key={`${w}x${h}`} value={`${w}x${h}`}>
                          {w} × {h}
                        </option>
                      ))}
                  </select>
                ) : null}
              </div>
            </Field>

            <div style={{ marginTop: 14 }}>
              <Field label="GPU">
                <div class="row">
                  <select
                    style={{ maxWidth: 130 }}
                    value={fp.gpu.mode}
                    onChange={(e) => {
                      const mode = (e.currentTarget as HTMLSelectElement).value as 'auto' | 'manual';
                      const first = GPU_POOL[fp.platform][0]!;
                      patchFp({
                        gpu:
                          mode === 'auto'
                            ? { mode: 'auto' }
                            : { mode: 'manual', vendor: first.vendor, renderer: first.renderer },
                      });
                    }}
                  >
                    <option value="auto">Auto</option>
                    <option value="manual">Manual</option>
                  </select>
                  {fp.gpu.mode === 'manual' ? (
                    <select
                      style={{ flex: 1, minWidth: 240 }}
                      value={fp.gpu.renderer ?? ''}
                      onChange={(e) => {
                        const renderer = (e.currentTarget as HTMLSelectElement).value;
                        const found = GPU_POOL[fp.platform].find((g) => g.renderer === renderer);
                        patchFp({
                          gpu: { mode: 'manual', vendor: found?.vendor ?? fp.gpu.vendor, renderer },
                        });
                      }}
                    >
                      {GPU_POOL[fp.platform].map((g) => (
                        <option key={g.renderer} value={g.renderer}>
                          {g.renderer}
                        </option>
                      ))}
                    </select>
                  ) : null}
                </div>
              </Field>
            </div>

            <div class="grid2" style={{ marginTop: 14 }}>
              <Field label="CPU cores" hint="navigator.hardwareConcurrency">
                <select
                  value={fp.cpuCores.mode === 'manual' ? String(fp.cpuCores.value) : 'auto'}
                  onChange={(e) => {
                    const v = (e.currentTarget as HTMLSelectElement).value;
                    patchFp({
                      cpuCores: v === 'auto' ? { mode: 'auto' } : { mode: 'manual', value: Number(v) },
                    });
                  }}
                >
                  <option value="auto">Auto</option>
                  {[...new Set(CPU_POOL)].map((c) => (
                    <option key={c} value={String(c)}>
                      {c}
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Device memory (GB)" hint="navigator.deviceMemory">
                <select
                  value={fp.deviceMemory.mode === 'manual' ? String(fp.deviceMemory.value) : 'auto'}
                  onChange={(e) => {
                    const v = (e.currentTarget as HTMLSelectElement).value;
                    patchFp({
                      deviceMemory:
                        v === 'auto' ? { mode: 'auto' } : { mode: 'manual', value: Number(v) },
                    });
                  }}
                >
                  <option value="auto">Auto</option>
                  {[...new Set(MEMORY_POOL)].map((m) => (
                    <option key={m} value={String(m)}>
                      {m}
                    </option>
                  ))}
                </select>
              </Field>
            </div>

            <div class="section-head">Anti-detection details</div>

            <div class="grid2">
              <Field
                label="Storage quota (MB)"
                hint="Incognito windows report a small quota. 5000 looks like a normal profile."
              >
                <input
                  type="number"
                  value={fp.storageQuotaMb ?? ''}
                  placeholder="binary default"
                  onInput={(e) =>
                    patchFp({ storageQuotaMb: intOrUndef((e.currentTarget as HTMLInputElement).value) })
                  }
                />
              </Field>
              <Field
                label="Taskbar height (px)"
                hint="Affects screen.availHeight. Blank = derived from the OS being spoofed."
              >
                <input
                  type="number"
                  value={fp.taskbarHeight ?? ''}
                  placeholder="Auto"
                  onInput={(e) =>
                    patchFp({ taskbarHeight: intOrUndef((e.currentTarget as HTMLInputElement).value) })
                  }
                />
              </Field>
            </div>

            <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Check
                checked={fp.noise}
                onChange={(noise) => patchFp({ noise })}
                label="Canvas / WebGL / audio noise"
                hint="Seed-deterministic noise. Leave on unless a site breaks because of it."
              />
              <Check
                checked={fp.allowThirdPartyCookies}
                onChange={(allowThirdPartyCookies) => patchFp({ allowThirdPartyCookies })}
                label="Allow third-party cookies"
                hint="Needed for some SSO, reCAPTCHA and payment flows. Off is the safer default."
              />
              <Check
                checked={fp.windowsFontMetrics}
                disabled={fp.platform !== 'windows'}
                onChange={(windowsFontMetrics) => patchFp({ windowsFontMetrics })}
                label="Windows font metrics"
                hint="Aligns text measurement with real Windows. Only relevant when spoofing Windows from another OS."
              />
            </div>

            <div style={{ marginTop: 14 }}>
              <Field
                label="Fonts directory"
                hint="Optional folder of target-platform fonts, e.g. real Windows fonts when spoofing Windows from Linux."
              >
                <div class="row" style={{ gap: 6 }}>
                  <input
                    type="text"
                    value={fp.fontsDir ?? ''}
                    placeholder="Not set"
                    onInput={(e) =>
                      patchFp({ fontsDir: (e.currentTarget as HTMLInputElement).value || undefined })
                    }
                  />
                  <button
                    class="btn sm"
                    onClick={async () => {
                      const dir = await window.hub.app.pickDir();
                      if (dir) patchFp({ fontsDir: dir });
                    }}
                  >
                    Browse
                  </button>
                </div>
              </Field>
            </div>

            <div class="section-head">WebRTC</div>
            <div class="grid2">
              <Field label="Mode">
                <select
                  value={fp.webrtc.mode}
                  onChange={(e) =>
                    patchFp({
                      webrtc: {
                        mode: (e.currentTarget as HTMLSelectElement).value as 'off' | 'auto' | 'manual',
                        ip: fp.webrtc.ip,
                      },
                    })
                  }
                >
                  <option value="auto">Auto — follow the proxy exit IP</option>
                  <option value="manual">Manual — pin an IP</option>
                  <option value="off">Off — leave untouched</option>
                </select>
              </Field>
              {fp.webrtc.mode === 'manual' ? (
                <Field label="Public IP">
                  <input
                    type="text"
                    value={fp.webrtc.ip ?? ''}
                    placeholder="203.0.113.10"
                    onInput={(e) =>
                      patchFp({
                        webrtc: { mode: 'manual', ip: (e.currentTarget as HTMLInputElement).value },
                      })
                    }
                  />
                </Field>
              ) : null}
            </div>
            {fp.webrtc.mode === 'auto' && !hasProxy ? (
              <div style={{ marginTop: 10 }}>
                <Callout tone="warn" icon="!">
                  Auto WebRTC is skipped without a proxy — spoofing an ICE candidate on a direct
                  connection would itself be a mismatch.
                </Callout>
              </div>
            ) : null}

            <div class="row" style={{ marginTop: 18 }}>
              <button
                class="btn"
                onClick={() => patchFp(randomFingerprint(fp.platform))}
                title="Pin a random but internally coherent device"
              >
                Randomise device
              </button>
              <button
                class="btn ghost"
                onClick={() =>
                  patchFp({
                    screen: { mode: 'auto' },
                    gpu: { mode: 'auto' },
                    cpuCores: { mode: 'auto' },
                    deviceMemory: { mode: 'auto' },
                    platformVersion: undefined,
                  })
                }
              >
                Reset to auto
              </button>
            </div>
          </>
        ) : null}

        {/* -------------------------------------------------------------- Proxy */}
        {tab === 'proxy' ? (
          <>
            <div class="grid2">
              <Field label="From proxy library" hint="Pick a saved proxy to fill the fields below.">
                <select
                  value={draft.proxy.savedProxyId ?? ''}
                  onChange={(e) => useSavedProxy((e.currentTarget as HTMLSelectElement).value)}
                >
                  <option value="">— none —</option>
                  {savedProxies.map((p) => (
                    <option key={p.id} value={p.id}>
                      {p.name} ({p.kind})
                    </option>
                  ))}
                </select>
              </Field>
              <Field label="Type">
                <select
                  value={draft.proxy.kind}
                  onChange={(e) =>
                    patchProxy({
                      kind: (e.currentTarget as HTMLSelectElement).value as ProxyConfig['kind'],
                      savedProxyId: undefined,
                    })
                  }
                >
                  <option value="none">Direct — no proxy</option>
                  <option value="http">HTTP</option>
                  <option value="https">HTTPS</option>
                  <option value="socks5">SOCKS5</option>
                </select>
              </Field>
            </div>

            {draft.proxy.kind !== 'none' ? (
              <>
                <div class="grid2" style={{ marginTop: 14 }}>
                  <Field label="Host">
                    <input
                      type="text"
                      value={draft.proxy.host ?? ''}
                      placeholder="proxy.example.com"
                      onInput={(e) =>
                        patchProxy({
                          host: (e.currentTarget as HTMLInputElement).value.trim(),
                          savedProxyId: undefined,
                        })
                      }
                    />
                  </Field>
                  <Field label="Port">
                    <input
                      type="number"
                      value={draft.proxy.port ?? ''}
                      placeholder="8080"
                      onInput={(e) =>
                        patchProxy({
                          port: intOrUndef((e.currentTarget as HTMLInputElement).value),
                          savedProxyId: undefined,
                        })
                      }
                    />
                  </Field>
                </div>
                <div class="grid2" style={{ marginTop: 14 }}>
                  <Field label="Username">
                    <input
                      type="text"
                      value={draft.proxy.username ?? ''}
                      placeholder="Optional"
                      onInput={(e) =>
                        patchProxy({
                          username: (e.currentTarget as HTMLInputElement).value || undefined,
                        })
                      }
                    />
                  </Field>
                  <Field label="Password" hint="Stored encrypted with the OS keychain.">
                    <input
                      type="password"
                      value={draft.proxy.password ?? ''}
                      placeholder="Optional"
                      onInput={(e) =>
                        patchProxy({
                          password: (e.currentTarget as HTMLInputElement).value || undefined,
                        })
                      }
                    />
                  </Field>
                </div>
                <div class="grid2" style={{ marginTop: 14 }}>
                  <Field
                    label="Bypass list"
                    hint="Comma separated hosts that skip the proxy, e.g. localhost, .internal"
                  >
                    <input
                      type="text"
                      value={draft.proxy.bypass ?? ''}
                      placeholder="localhost, 127.0.0.1"
                      onInput={(e) =>
                        patchProxy({ bypass: (e.currentTarget as HTMLInputElement).value || undefined })
                      }
                    />
                  </Field>
                  <Field
                    label="IP rotation URL"
                    hint="Optional GET endpoint your provider gives you to change IP."
                  >
                    <div class="row" style={{ gap: 6 }}>
                      <input
                        type="url"
                        value={draft.proxy.rotationUrl ?? ''}
                        placeholder="https://provider/rotate?key=…"
                        onInput={(e) =>
                          patchProxy({
                            rotationUrl: (e.currentTarget as HTMLInputElement).value || undefined,
                          })
                        }
                      />
                      <button
                        class="btn sm"
                        disabled={!draft.proxy.rotationUrl}
                        onClick={() => {
                          const url = draft.proxy.rotationUrl;
                          if (!url) return;
                          void toast.run(async () => {
                            const res = await window.hub.proxies.rotate(url);
                            if (!res.ok) throw new Error(res.error ?? 'Rotation failed.');
                            toast.ok('Rotation endpoint called.');
                          });
                        }}
                      >
                        Rotate
                      </button>
                    </div>
                  </Field>
                </div>

                <div class="row" style={{ marginTop: 18 }}>
                  <button class="btn" onClick={checkProxyNow} disabled={checking}>
                    {checking ? 'Checking…' : 'Check proxy'}
                  </button>
                  <button class="btn ghost" onClick={saveToLibrary} disabled={!hasProxy}>
                    Save to library
                  </button>
                </div>
              </>
            ) : (
              <div style={{ marginTop: 14 }}>
                <Callout tone="warn" icon="!">
                  Without a proxy every profile shares your real IP. A perfect fingerprint behind a
                  shared IP still links accounts together.
                </Callout>
              </div>
            )}
          </>
        ) : null}

        {/* -------------------------------------------------------- Locale & Geo */}
        {tab === 'locale' ? (
          <>
            <Field label="Language & timezone source">
              <select
                value={draft.locale.mode}
                onChange={(e) =>
                  patchLocale({ mode: (e.currentTarget as HTMLSelectElement).value as 'ip' | 'manual' })
                }
              >
                <option value="ip">Follow the proxy exit IP (recommended)</option>
                <option value="manual">Set manually</option>
              </select>
            </Field>

            {draft.locale.mode === 'ip' && !hasProxy ? (
              <div style={{ marginTop: 12 }}>
                <Callout tone="warn" icon="!">
                  There is no proxy on this profile, so there is no exit IP to follow — the browser
                  will use your own machine's language and timezone. Add a proxy or switch to manual.
                </Callout>
              </div>
            ) : null}

            {draft.locale.mode === 'manual' ? (
              <>
                <div style={{ marginTop: 14 }}>
                  <Field label="Preset" hint="Fills both fields with a matching pair.">
                    <select
                      value=""
                      onChange={(e) => {
                        const label = (e.currentTarget as HTMLSelectElement).value;
                        const preset = LOCALE_PRESETS.find((p) => p.label === label);
                        if (preset) patchLocale({ locale: preset.locale, timezone: preset.timezone });
                      }}
                    >
                      <option value="">— choose a preset —</option>
                      {LOCALE_PRESETS.map((p) => (
                        <option key={p.label} value={p.label}>
                          {p.label}
                        </option>
                      ))}
                    </select>
                  </Field>
                </div>
                <div class="grid2" style={{ marginTop: 14 }}>
                  <Field label="Locale" hint="BCP 47, e.g. en-US. Also sets Accept-Language.">
                    <input
                      type="text"
                      value={draft.locale.locale ?? ''}
                      placeholder="en-US"
                      onInput={(e) =>
                        patchLocale({ locale: (e.currentTarget as HTMLInputElement).value || undefined })
                      }
                    />
                  </Field>
                  <Field label="Timezone" hint="IANA zone, e.g. America/New_York.">
                    <input
                      type="text"
                      value={draft.locale.timezone ?? ''}
                      placeholder="America/New_York"
                      onInput={(e) =>
                        patchLocale({
                          timezone: (e.currentTarget as HTMLInputElement).value || undefined,
                        })
                      }
                    />
                  </Field>
                </div>
              </>
            ) : null}

            <div class="section-head">Geolocation</div>
            <Field label="Mode">
              <select
                value={draft.geo.mode}
                onChange={(e) =>
                  patchGeo({
                    mode: (e.currentTarget as HTMLSelectElement).value as 'off' | 'ip' | 'manual',
                  })
                }
              >
                <option value="ip">Match the IP location</option>
                <option value="manual">Pin coordinates</option>
                <option value="off">Off — leave the browser default</option>
              </select>
            </Field>

            {draft.geo.mode === 'manual' ? (
              <div class="grid3" style={{ marginTop: 14 }}>
                <Field label="Latitude">
                  <input
                    type="text"
                    value={draft.geo.latitude ?? ''}
                    placeholder="40.7128"
                    onInput={(e) =>
                      patchGeo({ latitude: floatOrUndef((e.currentTarget as HTMLInputElement).value) })
                    }
                  />
                </Field>
                <Field label="Longitude">
                  <input
                    type="text"
                    value={draft.geo.longitude ?? ''}
                    placeholder="-74.0060"
                    onInput={(e) =>
                      patchGeo({ longitude: floatOrUndef((e.currentTarget as HTMLInputElement).value) })
                    }
                  />
                </Field>
                <Field label="Accuracy (m)">
                  <input
                    type="number"
                    value={draft.geo.accuracy ?? ''}
                    placeholder="100"
                    onInput={(e) =>
                      patchGeo({ accuracy: intOrUndef((e.currentTarget as HTMLInputElement).value) })
                    }
                  />
                </Field>
              </div>
            ) : null}
          </>
        ) : null}

        {/* ------------------------------------------------------------ Cookies */}
        {tab === 'cookies' ? <CookiesTab profile={draft} onChanged={patch} /> : null}

        {/* ---------------------------------------------------------- Behaviour */}
        {tab === 'behaviour' ? (
          <>
            <Callout icon="i">
              These settings affect scripted interaction (mouse curves, typing rhythm). They change
              nothing when you drive the browser by hand.
            </Callout>

            <div style={{ marginTop: 16 }}>
              <Check
                checked={draft.behaviour.humanize}
                onChange={(humanize) => patchBehaviour({ humanize })}
                label="Human-like input"
                hint="Bezier mouse paths, per-character typing, natural scrolling."
              />
            </div>

            {draft.behaviour.humanize ? (
              <>
                <div class="grid2" style={{ marginTop: 16 }}>
                  <Field label="Preset">
                    <select
                      value={draft.behaviour.preset}
                      onChange={(e) =>
                        patchBehaviour({
                          preset: (e.currentTarget as HTMLSelectElement).value as 'default' | 'careful',
                        })
                      }
                    >
                      <option value="default">Default — balanced speed</option>
                      <option value="careful">Careful — slower, more variance</option>
                    </select>
                  </Field>
                  <Field label="Typing delay (ms/char)" hint="Blank = preset default.">
                    <input
                      type="number"
                      value={draft.behaviour.typingDelay ?? ''}
                      placeholder="Auto"
                      onInput={(e) =>
                        patchBehaviour({
                          typingDelay: intOrUndef((e.currentTarget as HTMLInputElement).value),
                        })
                      }
                    />
                  </Field>
                </div>
                <div class="grid2" style={{ marginTop: 14 }}>
                  <Field
                    label="Mistype chance (0–1)"
                    hint="Typos with self-correction. 0.02 is realistic."
                  >
                    <input
                      type="text"
                      value={draft.behaviour.mistypeChance ?? ''}
                      placeholder="Auto"
                      onInput={(e) =>
                        patchBehaviour({
                          mistypeChance: floatOrUndef((e.currentTarget as HTMLInputElement).value),
                        })
                      }
                    />
                  </Field>
                </div>
                <div style={{ marginTop: 16 }}>
                  <Check
                    checked={draft.behaviour.idleBetweenActions ?? false}
                    onChange={(idleBetweenActions) => patchBehaviour({ idleBetweenActions })}
                    label="Idle pauses between actions"
                    hint="Adds small human hesitation between steps. Slower but less machine-like."
                  />
                </div>
              </>
            ) : null}
          </>
        ) : null}

        {/* ------------------------------------------------------------ Startup */}
        {tab === 'startup' ? (
          <>
            <Field label="Start pages" hint="One URL per line. Opened in tabs when the session starts.">
              <textarea
                value={draft.startup.startUrls.join('\n')}
                placeholder={'https://example.com\nhttps://mail.google.com'}
                onInput={(e) =>
                  patchStartup({
                    startUrls: (e.currentTarget as HTMLTextAreaElement).value
                      .split('\n')
                      .map((u) => u.trim())
                      .filter(Boolean),
                  })
                }
              />
            </Field>

            <div style={{ marginTop: 16 }}>
              <Check
                checked={draft.startup.headless}
                onChange={(headless) => patchStartup({ headless })}
                label="Headless"
                hint="No visible window. Not recommended for account work — headless is easier to detect and you cannot solve a challenge."
              />
            </div>

            <div style={{ marginTop: 16 }}>
              <Field label="Extensions" hint="Absolute paths to unpacked extension folders, one per line.">
                <textarea
                  value={draft.startup.extensionPaths.join('\n')}
                  placeholder="/home/user/extensions/my-extension"
                  onInput={(e) =>
                    patchStartup({
                      extensionPaths: (e.currentTarget as HTMLTextAreaElement).value
                        .split('\n')
                        .map((p) => p.trim())
                        .filter(Boolean),
                    })
                  }
                />
              </Field>
              <div class="row" style={{ marginTop: 8 }}>
                <button
                  class="btn sm"
                  onClick={async () => {
                    const dir = await window.hub.app.pickDir();
                    if (dir) patchStartup({ extensionPaths: [...draft.startup.extensionPaths, dir] });
                  }}
                >
                  Add folder…
                </button>
              </div>
            </div>

            <div class="section-head">User agent</div>
            <Field
              label="Custom user agent"
              hint="Leave blank. The browser builds a UA that matches the OS, brand and version above — a hand-written UA is the most common way to break coherence."
            >
              <input
                type="text"
                value={draft.userAgent ?? ''}
                placeholder="Auto (recommended)"
                onInput={(e) =>
                  patch({ userAgent: (e.currentTarget as HTMLInputElement).value || undefined })
                }
              />
            </Field>
          </>
        ) : null}

        {/* ----------------------------------------------------------- Advanced */}
        {tab === 'advanced' ? (
          <>
            <Field
              label="Extra Chromium flags"
              hint="One flag per line. Applied last, but flags this app owns (--fingerprint*, --lang) always win."
            >
              <textarea
                value={draft.startup.extraArgs.join('\n')}
                placeholder="--disable-blink-features=AutomationControlled"
                onInput={(e) =>
                  patchStartup({
                    extraArgs: (e.currentTarget as HTMLTextAreaElement).value
                      .split('\n')
                      .map((a) => a.trim())
                      .filter(Boolean),
                  })
                }
              />
            </Field>

            <div class="section-head">Resolved launch flags</div>
            <p class="card-desc">
              Exactly what will be passed to the browser with the current settings. Auto values are
              absent on purpose — the browser derives them from the seed.
            </p>
            <div class="code-block">{resolvedArgs.join('\n')}</div>

            <div class="section-head">Resolved options</div>
            <div class="stat-grid">
              <div class="stat">
                <div class="k">Proxy</div>
                <div class="v small mono">{proxyLabel(draft)}</div>
              </div>
              <div class="stat">
                <div class="k">Geo-IP locale</div>
                <div class="v small">{draft.locale.mode === 'ip' && hasProxy ? 'On' : 'Off'}</div>
              </div>
              <div class="stat">
                <div class="k">Window</div>
                <div class="v small">{draft.startup.headless ? 'Headless' : 'Headed'}</div>
              </div>
            </div>
          </>
        ) : null}
      </div>
    </Modal>
  );
}
