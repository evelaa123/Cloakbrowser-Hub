/**
 * License and browser binary management.
 *
 * The two are on one page because they are one decision from the user's point of
 * view: which key you hold determines which binary you get. The tier explanation
 * is spelled out because "why is my Chromium old?" is otherwise invisible.
 */

import type { JSX } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import { useHub } from '../state';
import { useToast } from '../components/toast';
import { Callout, Card, ConfirmModal, timeAgo } from '../components/ui';

export function LicensePage(): JSX.Element {
  const hub = useHub();
  const toast = useToast();
  const [key, setKey] = useState('');
  const [activating, setActivating] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [downloading, setDownloading] = useState(false);
  const [signOutOpen, setSignOutOpen] = useState(false);

  // The main process broadcasts download progress; without this the button would
  // look stuck for the minute or two a first download takes.
  useEffect(() => {
    const off = window.hub.events.onBinaryProgress((p) => {
      if (p.state === 'downloading') setDownloading(true);
      else setDownloading(false);
    });
    return off;
  }, []);

  const license = hub.license;
  const binary = hub.binary;

  async function activate(): Promise<void> {
    if (!key.trim()) {
      toast.warn('Paste your license key first.');
      return;
    }
    setActivating(true);
    const res = await toast.run(() => window.hub.license.activate(key.trim()));
    setActivating(false);
    if (res) {
      setKey('');
      toast.ok(
        res.plan && res.plan !== 'free'
          ? `Pro license activated (${res.plan}).`
          : 'Free license activated. You now get the latest browser build.',
      );
      await hub.refreshLicense(true);
      await hub.refreshBinary();
    }
  }

  async function refresh(): Promise<void> {
    setRefreshing(true);
    await hub.refreshLicense(true);
    await hub.refreshBinary();
    setRefreshing(false);
  }

  async function download(): Promise<void> {
    setDownloading(true);
    const res = await toast.run(() => window.hub.binary.download());
    setDownloading(false);
    if (res) {
      toast.ok(`Browser ${res.version ?? ''} ready.`);
      await hub.refreshBinary();
    }
  }

  async function signOut(): Promise<void> {
    await toast.run(() => window.hub.license.logout(), 'License key removed.');
    await hub.refreshLicense(true);
    await hub.refreshBinary();
  }

  const tierText =
    license?.tier === 'pro'
      ? `Pro — ${license.plan ?? 'paid plan'}`
      : license?.tier === 'free'
        ? 'Free key'
        : 'No key';

  const seatText =
    license?.seatHint === null
      ? 'unlimited'
      : license?.seatHint != null
        ? String(license.seatHint)
        : '—';

  return (
    <>
      <div class="topbar">
        <div>
          <h1>License</h1>
          <div class="sub">CloakBrowser key and stealth browser binary</div>
        </div>
        <div class="topbar-actions">
          <button class="btn" onClick={refresh} disabled={refreshing}>
            {refreshing ? 'Checking…' : 'Re-check'}
          </button>
          <button class="btn" onClick={() => void window.hub.license.openPricing()}>
            View plans
          </button>
        </div>
      </div>

      <div class="content">
        <Card
          title="Current license"
          desc="The key is stored at ~/.cloakbrowser/license.key — the same file the cloakbrowser CLI reads, so activating here also covers your own scripts on this machine."
        >
          <div class="stat-grid">
            <div class="stat">
              <div class="k">Tier</div>
              <div class="v small">{tierText}</div>
            </div>
            <div class="stat">
              <div class="k">Key</div>
              <div class="v small mono">{license?.maskedKey ?? 'none'}</div>
            </div>
            <div class="stat">
              <div class="k">Sessions</div>
              <div class="v small">
                {license?.localSessions ?? 0} local
                {license?.activeSessions != null ? ` · ${license.activeSessions} on server` : ''} /{' '}
                {seatText}
              </div>
            </div>
            <div class="stat">
              <div class="k">Checked</div>
              <div class="v small">{timeAgo(license?.checkedAt)}</div>
            </div>
          </div>

          {license?.error ? (
            <div style={{ marginTop: 14 }}>
              <Callout tone={license.valid ? 'warn' : 'err'} icon="!">
                {license.error}
              </Callout>
            </div>
          ) : null}

          {license?.expires ? (
            <div style={{ marginTop: 14 }}>
              <Callout icon="i">
                This key expires on <strong>{license.expires}</strong>.
              </Callout>
            </div>
          ) : null}

          {license?.tier !== 'none' ? (
            <div class="row" style={{ marginTop: 16 }}>
              <button class="btn danger" onClick={() => setSignOutOpen(true)}>
                Remove key
              </button>
            </div>
          ) : null}
        </Card>

        <Card
          title="Get a free key with GitHub"
          desc="A free key unlocks the latest browser build and one concurrent session. Sign-in happens in your normal browser; CloakBrowser then emails the key to your GitHub address."
        >
          <ol class="dim" style={{ margin: '0 0 16px', paddingLeft: 20, lineHeight: 1.8 }}>
            <li>Click the button below — your browser opens the GitHub sign-in page.</li>
            <li>Authorise CloakBrowser; the key arrives by email within a minute.</li>
            <li>Paste it into the field below and press Activate.</li>
          </ol>
          <div class="row">
            <button class="btn primary" onClick={() => void window.hub.license.signInWithGithub()}>
              Sign in with GitHub
            </button>
          </div>

          <div class="section-head">Activate a key</div>
          <div class="row" style={{ alignItems: 'stretch' }}>
            <input
              type="text"
              style={{ flex: 1, minWidth: 240 }}
              class="mono"
              value={key}
              placeholder="Paste your free or paid license key"
              onInput={(e) => setKey((e.currentTarget as HTMLInputElement).value)}
              onKeyDown={(e) => {
                if (e.key === 'Enter') void activate();
              }}
            />
            <button class="btn primary" onClick={activate} disabled={activating}>
              {activating ? 'Validating…' : 'Activate'}
            </button>
          </div>
          <div class="hint" style={{ marginTop: 8 }}>
            The key is verified with the license server before anything is written, so an invalid
            key can never replace a working one.
          </div>
        </Card>

        <Card
          title="Stealth browser binary"
          desc="A patched Chromium downloaded on demand, not bundled with this app — that keeps the installer small and lets you update the browser without updating the Hub."
          actions={
            <button class="btn primary" onClick={download} disabled={downloading}>
              {downloading
                ? 'Downloading…'
                : binary?.installed
                  ? 'Re-download'
                  : 'Download browser'}
            </button>
          }
        >
          <div class="stat-grid">
            <div class="stat">
              <div class="k">Status</div>
              <div class="v small">{binary?.installed ? 'Installed' : 'Not installed'}</div>
            </div>
            <div class="stat">
              <div class="k">Version</div>
              <div class="v small mono">{binary?.version ?? '—'}</div>
            </div>
            <div class="stat">
              <div class="k">Build</div>
              <div class="v small">{binary?.tier === 'pro' ? 'Pro (latest)' : 'Free'}</div>
            </div>
            <div class="stat">
              <div class="k">Platform</div>
              <div class="v small mono">{binary?.platform ?? '—'}</div>
            </div>
          </div>

          {binary?.error ? (
            <div style={{ marginTop: 14 }}>
              <Callout tone="err" icon="✕">
                {binary.error}
              </Callout>
            </div>
          ) : null}

          {downloading ? (
            <div style={{ marginTop: 14 }}>
              <Callout icon="↓">
                Downloading the browser — this is a few hundred megabytes on a first run and may take
                a couple of minutes. You can keep using the rest of the app.
              </Callout>
            </div>
          ) : null}

          {binary?.path ? (
            <div style={{ marginTop: 14 }}>
              <div class="field">
                <label>Binary path</label>
                <div class="row">
                  <span class="mono faint" style={{ wordBreak: 'break-all', flex: 1 }}>
                    {binary.path}
                  </span>
                  <button
                    class="btn sm"
                    onClick={() =>
                      void toast.run(() => window.hub.app.openPath(binary.cacheDir ?? binary.path!))
                    }
                  >
                    Open folder
                  </button>
                </div>
              </div>
            </div>
          ) : null}
        </Card>

        <Card title="What each tier gives you">
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Tier</th>
                  <th>Browser build</th>
                  <th>Concurrent sessions</th>
                </tr>
              </thead>
              <tbody>
                <tr>
                  <td>No key</td>
                  <td class="dim">Older free build</td>
                  <td class="dim">Not tracked</td>
                </tr>
                <tr>
                  <td>Free (GitHub)</td>
                  <td class="dim">Latest</td>
                  <td class="dim">1</td>
                </tr>
                <tr>
                  <td>Solo</td>
                  <td class="dim">Latest</td>
                  <td class="dim">5</td>
                </tr>
                <tr>
                  <td>Team</td>
                  <td class="dim">Latest</td>
                  <td class="dim">20</td>
                </tr>
                <tr>
                  <td>Scale</td>
                  <td class="dim">Latest</td>
                  <td class="dim">200</td>
                </tr>
                <tr>
                  <td>Enterprise</td>
                  <td class="dim">Latest</td>
                  <td class="dim">Negotiated</td>
                </tr>
              </tbody>
            </table>
          </div>
          <div class="hint" style={{ marginTop: 10 }}>
            The seat limit is enforced by the browser itself, not by this app. Exceeding it makes a
            launch fail with a clear message rather than starting a degraded session.
          </div>
        </Card>
      </div>

      {signOutOpen ? (
        <ConfirmModal
          title="Remove the license key?"
          message="The key file is deleted. Sessions will fall back to the older free browser build until you activate a key again. Your profiles and cookies are untouched."
          confirmLabel="Remove key"
          danger
          onClose={() => setSignOutOpen(false)}
          onConfirm={() => void signOut()}
        />
      ) : null}
    </>
  );
}
