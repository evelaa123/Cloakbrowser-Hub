/**
 * App shell: sidebar navigation plus the active page.
 *
 * Navigation is a plain string union rather than a router — the app has seven
 * fixed screens and no URLs to deep-link, so a router would only add weight.
 */

import type { JSX } from 'preact';
import { useState } from 'preact/hooks';
import { useHub } from './state';
import { Spinner } from './components/ui';
import { BinaryDownloadModal } from './components/binary-download';
// Generated from build/icon-master.png by build/make-icon.py, so the sidebar
// mark and the packaged application icon cannot drift apart. The cloak-only
// crop is used because the HUB wordmark is unreadable at this size — the
// sidebar spells the name out in text next to it anyway.
import cloakMark from './assets/cloak-mark.png';
import { ProfilesPage } from './pages/ProfilesPage';
import { ProxiesPage } from './pages/ProxiesPage';
import { LicensePage } from './pages/LicensePage';
import { ImportPage } from './pages/ImportPage';
import { SettingsPage } from './pages/SettingsPage';

export type Route = 'profiles' | 'proxies' | 'import' | 'license' | 'settings';

const NAV: Array<{ id: Route; label: string; icon: string }> = [
  { id: 'profiles', label: 'Profiles', icon: '◉' },
  { id: 'proxies', label: 'Proxies', icon: '⇄' },
  { id: 'import', label: 'Import', icon: '⤓' },
  { id: 'license', label: 'License', icon: '✦' },
  { id: 'settings', label: 'Settings', icon: '⚙' },
];

export function App(): JSX.Element {
  const [route, setRoute] = useState<Route>('profiles');
  const hub = useHub();

  const tierLabel =
    hub.license?.tier === 'pro'
      ? `Pro · ${hub.license.plan ?? 'paid'}`
      : hub.license?.tier === 'free'
        ? 'Free key'
        : 'No key';

  return (
    <div class="shell">
      <aside class="sidebar">
        <div class="brand">
          <img class="brand-mark" src={cloakMark} alt="" width={26} height={26} />
          <div>
            <div class="brand-name">CloakBrowser</div>
            <div class="brand-sub">Hub</div>
          </div>
        </div>

        {NAV.map((item) => (
          <button
            key={item.id}
            class={`nav-item ${route === item.id ? 'active' : ''}`}
            onClick={() => setRoute(item.id)}
          >
            <span class="nav-icon" aria-hidden="true">
              {item.icon}
            </span>
            {item.label}
            {item.id === 'profiles' && hub.runningCount > 0 ? (
              <span class="nav-badge">{hub.runningCount}</span>
            ) : null}
          </button>
        ))}

        <div class="sidebar-spacer" />

        <div class="sidebar-foot">
          <div class="row" style={{ gap: 6 }}>
            <span
              class={`badge ${hub.license?.valid ? 'ok' : hub.license?.tier === 'none' ? '' : 'warn'}`}
            >
              {tierLabel}
            </span>
          </div>
          <div>
            {hub.binary?.installed
              ? `Browser ${hub.binary.version ?? 'installed'}`
              : 'Browser not installed'}
          </div>
          <div>v{hub.info?.version ?? '—'}</div>
        </div>
      </aside>

      {/* Shell-level so it covers whichever page triggered the download. */}
      <BinaryDownloadModal />

      <main class="main">
        {hub.loading ? (
          <div class="empty" style={{ marginTop: 80 }}>
            <Spinner />
            <p style={{ marginTop: 14 }}>Loading…</p>
          </div>
        ) : route === 'profiles' ? (
          <ProfilesPage onNavigate={setRoute} />
        ) : route === 'proxies' ? (
          <ProxiesPage />
        ) : route === 'import' ? (
          <ImportPage onNavigate={setRoute} />
        ) : route === 'license' ? (
          <LicensePage />
        ) : (
          <SettingsPage />
        )}
      </main>
    </div>
  );
}
