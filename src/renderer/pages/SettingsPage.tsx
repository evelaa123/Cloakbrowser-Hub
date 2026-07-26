/**
 * Application settings.
 *
 * Every control writes through immediately (there is no Save button) because each
 * setting is independent and a form-wide save would only create a state where the
 * UI and disk disagree.
 */

import type { JSX } from 'preact';
import { useState } from 'preact/hooks';
import type { AppSettings, FingerprintPlatform } from '../../shared/types';
import { preferenceMax, resolveSessionLimit } from '../../shared/session-limit';
import { DEFAULT_ZOOM, ZOOM_STEPS, snapZoom, zoomLabel } from '../../shared/ui-zoom';
import { useHub } from '../state';
import { useToast } from '../components/toast';
import { Callout, Card, Check, Field } from '../components/ui';
import { AutomationCard } from './AutomationCard';

export function SettingsPage(): JSX.Element {
  const hub = useHub();
  const toast = useToast();
  const [version, setVersion] = useState(hub.settings?.browserVersion ?? '');

  const settings = hub.settings;
  if (!settings) return <div class="content">Loading…</div>;

  // Same resolver the session manager enforces with, so the number shown here
  // and the number that actually binds at launch can never disagree.
  const sessionLimit = resolveSessionLimit(settings.maxConcurrentSessions, hub.license?.seatHint);

  const set = (patch: Partial<AppSettings>): void => {
    void toast.run(() => hub.saveSettings(patch));
  };

  async function pickProfilesDir(): Promise<void> {
    const dir = await window.hub.app.pickDir();
    if (dir) set({ profilesDir: dir });
  }

  return (
    <>
      <div class="topbar">
        <div>
          <h1>Settings</h1>
          <div class="sub">Applies to the whole application</div>
        </div>
      </div>

      <div class="content">
        <Card title="Appearance">
          <div class="grid2">
            <Field label="Theme">
              <select
                value={settings.theme}
                onChange={(e) =>
                  set({ theme: (e.currentTarget as HTMLSelectElement).value as 'dark' | 'light' })
                }
              >
                <option value="dark">Dark</option>
                <option value="light">Light</option>
              </select>
            </Field>
            <Field
              label="Interface size"
              hint="Scales the whole interface, not just the text. Ctrl/⌘ with + − 0 also works."
            >
              <select
                value={String(snapZoom(settings.uiZoom))}
                onChange={(e) =>
                  set({ uiZoom: Number.parseFloat((e.currentTarget as HTMLSelectElement).value) })
                }
              >
                {ZOOM_STEPS.map((z) => (
                  <option key={z} value={String(z)}>
                    {zoomLabel(z)}
                    {z === DEFAULT_ZOOM ? ' (default)' : ''}
                  </option>
                ))}
              </select>
            </Field>
          </div>
        </Card>

        <Card
          title="Sessions"
          desc="How many browsers the Hub is willing to open at once, and what happens to them when you quit."
        >
          <div class="grid2">
            <Field
              label="Maximum concurrent sessions"
              hint={
                sessionLimit.planSeats != null
                  ? sessionLimit.cappedByPlan
                    ? `Your plan allows ${sessionLimit.planSeats}, so ${sessionLimit.limit} is what gets enforced. Lower it here if your machine cannot handle that many.`
                    : `Your plan allows ${sessionLimit.planSeats}. You can lower this, but not raise it past your plan.`
                  : 'Your plan’s seat count is not known yet (no key, or the license server is unreachable), so this number is used as-is.'
              }
            >
              <input
                type="number"
                min={1}
                // Bounded by the plan rather than a flat 500: typing a number the
                // browser will refuse anyway turns an upgrade decision into a
                // launch-time error message.
                max={preferenceMax(sessionLimit.planSeats)}
                value={settings.maxConcurrentSessions}
                onChange={(e) => {
                  const el = e.currentTarget as HTMLInputElement;
                  const n = Number.parseInt(el.value, 10);
                  if (!Number.isFinite(n) || n < 1) return;
                  const cap = preferenceMax(sessionLimit.planSeats);
                  if (n > cap) {
                    // Snap back visibly and say why, instead of accepting a value
                    // that silently means something else at launch time.
                    el.value = String(cap);
                    set({ maxConcurrentSessions: cap });
                    toast.info(
                      sessionLimit.planSeats != null
                        ? `Your plan allows ${sessionLimit.planSeats} concurrent sessions, so the limit was set to ${cap}.`
                        : `The maximum is ${cap}.`,
                    );
                    return;
                  }
                  set({ maxConcurrentSessions: n });
                }}
              />
            </Field>
            <Field label="New profiles default to" hint="The OS a brand-new profile pretends to run.">
              <select
                value={settings.defaultPlatform}
                onChange={(e) =>
                  set({
                    defaultPlatform: (e.currentTarget as HTMLSelectElement)
                      .value as FingerprintPlatform,
                  })
                }
              >
                <option value="windows">Windows</option>
                <option value="macos">macOS</option>
                <option value="linux">Linux</option>
              </select>
            </Field>
          </div>

          <div style={{ marginTop: 18, display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Check
              checked={settings.saveCookiesOnClose}
              onChange={(saveCookiesOnClose) => set({ saveCookiesOnClose })}
              label="Save cookies when a session closes"
              hint="Writes the browser's cookies back into the profile's encrypted jar, so a refreshed session token is not lost."
            />
            <Check
              checked={settings.closeSessionsOnQuit}
              onChange={(closeSessionsOnQuit) => set({ closeSessionsOnQuit })}
              label="Close all sessions when the app quits"
              hint="Off leaves running browsers open as independent processes — their cookies will not be saved back."
            />
          </div>
        </Card>

        <Card
          title="Browser binary"
          desc="Which stealth Chromium build sessions launch. Leave the version blank unless you need to roll back after a bad update."
        >
          <div class="grid2">
            <Field label="Release channel">
              <select
                value={settings.releaseChannel}
                onChange={(e) =>
                  set({
                    releaseChannel: (e.currentTarget as HTMLSelectElement).value as 'stable' | 'preview',
                  })
                }
              >
                <option value="stable">Stable</option>
                <option value="preview">Preview</option>
              </select>
            </Field>
            <Field label="Pin a version" hint="Blank = always use the newest build for your license.">
              <div class="row" style={{ gap: 6 }}>
                <input
                  type="text"
                  class="mono"
                  value={version}
                  placeholder="Latest"
                  onInput={(e) => setVersion((e.currentTarget as HTMLInputElement).value)}
                />
                <button
                  class="btn sm"
                  onClick={() => {
                    set({ browserVersion: version.trim() || undefined });
                    void hub.refreshBinary();
                  }}
                >
                  Apply
                </button>
              </div>
            </Field>
          </div>
          {settings.releaseChannel === 'preview' ? (
            <div style={{ marginTop: 14 }}>
              <Callout tone="warn" icon="!">
                Preview builds get fingerprint patches earliest but are less tested. Use stable for
                anything that matters.
              </Callout>
            </div>
          ) : null}
        </Card>

        <Card
          title="Storage"
          desc="Where profile data lives. Moving this does not migrate existing folders — copy them yourself if you change it."
        >
          <Field label="Profiles directory">
            <div class="row" style={{ gap: 6 }}>
              <input
                type="text"
                class="mono"
                value={settings.profilesDir ?? hub.info?.profilesDir ?? ''}
                readOnly
              />
              <button class="btn sm" onClick={pickProfilesDir}>
                Change…
              </button>
              {settings.profilesDir ? (
                <button class="btn sm ghost" onClick={() => set({ profilesDir: undefined })}>
                  Reset
                </button>
              ) : null}
            </div>
          </Field>

          <div class="row" style={{ marginTop: 14 }}>
            <button
              class="btn sm"
              onClick={() =>
                void toast.run(() =>
                  window.hub.app.openPath(settings.profilesDir ?? hub.info?.profilesDir ?? ''),
                )
              }
            >
              Open profiles folder
            </button>
            <button
              class="btn sm"
              onClick={() => void toast.run(() => window.hub.app.openPath(hub.info?.userData ?? ''))}
            >
              Open app data folder
            </button>
          </div>
        </Card>

        <AutomationCard />

        <Card title="About">
          <div class="stat-grid">
            <div class="stat">
              <div class="k">Hub version</div>
              <div class="v small mono">{hub.info?.version ?? '—'}</div>
            </div>
            <div class="stat">
              <div class="k">Platform</div>
              <div class="v small mono">
                {hub.info?.platform} {hub.info?.arch}
              </div>
            </div>
            <div class="stat">
              <div class="k">Electron</div>
              <div class="v small mono">{hub.info?.electron ?? '—'}</div>
            </div>
            <div class="stat">
              <div class="k">Node</div>
              <div class="v small mono">{hub.info?.node ?? '—'}</div>
            </div>
          </div>
          <div class="row" style={{ marginTop: 14 }}>
            <button
              class="btn sm ghost"
              onClick={() =>
                void window.hub.app.openExternal('https://github.com/CloakHQ/CloakBrowser')
              }
            >
              CloakBrowser on GitHub
            </button>
          </div>
        </Card>
      </div>
    </>
  );
}
