/**
 * Profiles list — the app's home screen.
 *
 * The table is the primary control surface: start/stop, status, proxy, cookies
 * and last-run at a glance, with everything else behind the editor modal.
 */

import type { JSX } from 'preact';
import { useMemo, useState } from 'preact/hooks';
import type { Profile, ProfileRow } from '../../shared/types';
import { useHub } from '../state';
import { useToast } from '../components/toast';
import { Callout, ConfirmModal, Empty, StatusBadge, timeAgo } from '../components/ui';
import { ProfileEditor } from './ProfileEditor';
import { LogsModal } from './LogsModal';
import type { Route } from '../App';

function proxySummary(p: Profile): JSX.Element {
  if (p.proxy.kind === 'none') {
    return <span class="faint">Direct</span>;
  }
  return (
    <span class="mono nowrap">
      {p.proxy.kind}://{p.proxy.host}:{p.proxy.port}
    </span>
  );
}

export function ProfilesPage(props: { onNavigate: (r: Route) => void }): JSX.Element {
  const hub = useHub();
  const toast = useToast();
  const [query, setQuery] = useState('');
  const [editing, setEditing] = useState<Profile | null>(null);
  const [logsFor, setLogsFor] = useState<ProfileRow | null>(null);
  const [deleting, setDeleting] = useState<ProfileRow | null>(null);
  const [deleteData, setDeleteData] = useState(true);
  const [busy, setBusy] = useState<Record<string, boolean>>({});

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return hub.profiles;
    return hub.profiles.filter((p) =>
      [p.name, p.notes ?? '', p.tags.join(' '), p.proxy.host ?? '', p.fingerprint.platform]
        .join(' ')
        .toLowerCase()
        .includes(q),
    );
  }, [hub.profiles, query]);

  const mark = (id: string, on: boolean): void => setBusy((b) => ({ ...b, [id]: on }));

  async function create(): Promise<void> {
    // No overrides: main applies the default platform from settings, so the
    // renderer does not have to duplicate that decision.
    const created = await toast.run(() => window.hub.profiles.create());
    if (created) setEditing(created);
  }

  async function start(row: ProfileRow): Promise<void> {
    mark(row.id, true);
    await toast.run(() => window.hub.sessions.start(row.id));
    mark(row.id, false);
  }

  async function stop(row: ProfileRow): Promise<void> {
    mark(row.id, true);
    await toast.run(() => window.hub.sessions.stop(row.id));
    mark(row.id, false);
  }

  async function duplicate(row: ProfileRow): Promise<void> {
    await toast.run(
      () => window.hub.profiles.duplicate(row.id, { newSeed: true, copyCookies: false }),
      'Profile duplicated with a fresh fingerprint.',
    );
  }

  async function openEditor(row: ProfileRow): Promise<void> {
    // Fetch the stored profile rather than reusing the row: the row carries live
    // status fields that must not be written back on save.
    const fresh = await toast.run(() => window.hub.profiles.get(row.id));
    if (fresh) setEditing(fresh);
  }

  async function exportAll(): Promise<void> {
    const res = await toast.run(() => window.hub.profiles.exportToFile());
    if (res) toast.ok(`Exported ${res.count} profile(s).`);
  }

  async function importFile(): Promise<void> {
    const res = await toast.run(() => window.hub.profiles.importFromFile());
    if (res) {
      toast.ok(
        `Imported ${res.imported} profile(s)${res.skipped ? `, skipped ${res.skipped} invalid entr${res.skipped === 1 ? 'y' : 'ies'}` : ''}.`,
      );
    }
  }

  const needsBinary = hub.binary && !hub.binary.installed;
  const noKey = hub.license?.tier === 'none';

  return (
    <>
      <div class="topbar">
        <div>
          <h1>Profiles</h1>
          <div class="sub">
            {hub.profiles.length} profile{hub.profiles.length === 1 ? '' : 's'} ·{' '}
            {hub.runningCount} running
          </div>
        </div>
        <div class="topbar-actions">
          <div class="search">
            <span class="icon">⌕</span>
            <input
              type="text"
              placeholder="Search profiles…"
              value={query}
              onInput={(e) => setQuery((e.currentTarget as HTMLInputElement).value)}
            />
          </div>
          <button class="btn" onClick={importFile} title="Import profiles from a JSON export">
            Import
          </button>
          <button
            class="btn"
            onClick={exportAll}
            disabled={!hub.profiles.length}
            title="Export all profiles (proxy passwords are stripped)"
          >
            Export
          </button>
          <button
            class="btn"
            onClick={() => void toast.run(() => window.hub.sessions.stopAll(), 'All sessions closed.')}
            disabled={hub.runningCount === 0}
          >
            Stop all
          </button>
          <button class="btn primary" onClick={create}>
            + New profile
          </button>
        </div>
      </div>

      <div class="content">
        {needsBinary ? (
          <Callout tone="warn" icon="!">
            The stealth browser binary is not installed yet, so sessions cannot start.{' '}
            <button class="link" onClick={() => props.onNavigate('license')}>
              Go to License to download it
            </button>
            .
          </Callout>
        ) : null}

        {noKey && !needsBinary ? (
          <Callout icon="i">
            You are running the free binary without a license key (older Chromium build, no
            concurrency tracking).{' '}
            <button class="link" onClick={() => props.onNavigate('license')}>
              Sign in with GitHub for a free key
            </button>{' '}
            to get the latest build.
          </Callout>
        ) : null}

        <div style={{ marginTop: needsBinary || noKey ? 14 : 0 }}>
          {!hub.profiles.length ? (
            <Empty
              icon="◉"
              title="No profiles yet"
              text="A profile is one isolated browser identity: its own fingerprint, proxy, cookies and storage. Create one, or import an existing browser profile from this machine."
              action={
                <div class="row" style={{ justifyContent: 'center' }}>
                  <button class="btn primary" onClick={create}>
                    + New profile
                  </button>
                  <button class="btn" onClick={() => props.onNavigate('import')}>
                    Import from browser
                  </button>
                </div>
              }
            />
          ) : !filtered.length ? (
            <Empty icon="⌕" title="No matches" text={`Nothing matches “${query}”.`} />
          ) : (
            <div class="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Name</th>
                    <th>Status</th>
                    <th>OS</th>
                    <th>Proxy</th>
                    <th>Cookies</th>
                    <th>Last run</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {filtered.map((row) => {
                    const running = row.status === 'running';
                    const transitioning = row.status === 'starting' || row.status === 'stopping';
                    const isBusy = busy[row.id] || transitioning;
                    return (
                      <tr key={row.id}>
                        <td style={{ maxWidth: 280 }}>
                          <div class="name-cell">
                            <span
                              class="dot"
                              style={{ background: row.color ?? 'var(--accent)' }}
                              aria-hidden="true"
                            />
                            <div class="name-main">
                              <div class="title" title={row.notes || row.name}>
                                {row.name}
                              </div>
                              <div class="meta">
                                {row.tags.length ? (
                                  row.tags.slice(0, 3).map((t) => (
                                    <span class="tag" key={t}>
                                      {t}
                                    </span>
                                  ))
                                ) : (
                                  <>seed {row.fingerprint.seed ?? 'random'}</>
                                )}
                              </div>
                            </div>
                          </div>
                        </td>
                        <td>
                          <StatusBadge status={row.status} message={row.statusMessage} />
                        </td>
                        <td class="nowrap dim">
                          {row.fingerprint.platform === 'macos'
                            ? 'macOS'
                            : row.fingerprint.platform === 'windows'
                              ? 'Windows'
                              : 'Linux'}
                        </td>
                        <td style={{ maxWidth: 200, overflow: 'hidden' }}>{proxySummary(row)}</td>
                        <td class="nowrap">
                          {row.cookies?.count ? (
                            <span class="dim" title={`${row.cookies.domains} domain(s)`}>
                              {row.cookies.count}
                            </span>
                          ) : (
                            <span class="faint">—</span>
                          )}
                        </td>
                        <td class="nowrap faint">{timeAgo(row.lastRunAt)}</td>
                        <td class="actions">
                          <div class="row">
                            {running ? (
                              <button
                                class="btn sm danger"
                                onClick={() => void stop(row)}
                                disabled={isBusy}
                              >
                                Stop
                              </button>
                            ) : (
                              <button
                                class="btn sm primary"
                                onClick={() => void start(row)}
                                disabled={isBusy || !hub.binary?.installed}
                                title={
                                  hub.binary?.installed
                                    ? 'Launch this profile'
                                    : 'Install the browser binary first (License page)'
                                }
                              >
                                {isBusy ? '…' : 'Start'}
                              </button>
                            )}
                            <button class="btn sm" onClick={() => void openEditor(row)}>
                              Edit
                            </button>
                            <button
                              class="btn sm ghost"
                              onClick={() => setLogsFor(row)}
                              title="Session log"
                            >
                              Log
                            </button>
                            <button
                              class="btn sm ghost"
                              onClick={() => void duplicate(row)}
                              title="Duplicate with a new fingerprint"
                            >
                              ⧉
                            </button>
                            <button
                              class="btn sm ghost"
                              onClick={() => {
                                setDeleteData(true);
                                setDeleting(row);
                              }}
                              title="Delete profile"
                            >
                              ✕
                            </button>
                          </div>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      {editing ? <ProfileEditor profile={editing} onClose={() => setEditing(null)} /> : null}

      {logsFor ? <LogsModal profile={logsFor} onClose={() => setLogsFor(null)} /> : null}

      {deleting ? (
        <ConfirmModal
          title={`Delete “${deleting.name}”?`}
          message="The profile is removed from the list. Its browser data and cookie jar are only deleted if you keep the option below checked."
          confirmLabel="Delete"
          danger
          onClose={() => setDeleting(null)}
          onConfirm={() => {
            const id = deleting.id;
            void toast.run(() => window.hub.profiles.remove(id, deleteData), 'Profile deleted.');
          }}
          extra={
            <label class="check">
              <input
                type="checkbox"
                checked={deleteData}
                onChange={(e) => setDeleteData((e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="check-text">
                <strong>Also delete browser data and cookies</strong>
                <span>Uncheck to keep the folder on disk so it can be recovered manually.</span>
              </span>
            </label>
          }
        />
      ) : null}
    </>
  );
}
