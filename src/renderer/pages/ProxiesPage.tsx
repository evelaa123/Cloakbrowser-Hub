/**
 * Proxy library.
 *
 * A shared list so a proxy is entered once and referenced by many profiles, and
 * so its exit IP / geo can be verified in one place. Bulk paste accepts the
 * formats providers actually hand out (six variants, see services/proxy.ts).
 */

import type { JSX } from 'preact';
import { useEffect, useMemo, useState } from 'preact/hooks';
import type { ProxyConfig, SavedProxy } from '../../shared/types';
import { useToast } from '../components/toast';
import { Callout, ConfirmModal, Empty, Field, Modal, timeAgo } from '../components/ui';

const BULK_EXAMPLE = `host:port
host:port:user:pass
user:pass@host:port
socks5://user:pass@host:port
https://host:port
US East | 203.0.113.10:8080:user:pass`;

function checkBadge(p: SavedProxy): JSX.Element {
  if (!p.lastCheck) return <span class="badge">Not checked</span>;
  if (!p.lastCheck.ok) {
    return (
      <span class="badge err" title={p.lastCheck.error ?? 'Failed'}>
        Failed
      </span>
    );
  }
  return (
    <span class="badge ok" title={`Checked ${timeAgo(p.lastCheck.checkedAt)}`}>
      {p.lastCheck.countryCode ?? p.lastCheck.country ?? 'OK'}
    </span>
  );
}

export function ProxiesPage(): JSX.Element {
  const toast = useToast();
  const [items, setItems] = useState<SavedProxy[]>([]);
  const [query, setQuery] = useState('');
  const [bulkOpen, setBulkOpen] = useState(false);
  const [bulkText, setBulkText] = useState('');
  const [addOpen, setAddOpen] = useState(false);
  const [deleting, setDeleting] = useState<SavedProxy | null>(null);
  const [checkingId, setCheckingId] = useState<string | null>(null);
  const [checkingAll, setCheckingAll] = useState(false);
  const [draft, setDraft] = useState<ProxyConfig & { name: string }>({
    kind: 'http',
    name: '',
    host: '',
    port: undefined,
  });

  async function reload(): Promise<void> {
    try {
      setItems(await window.hub.proxies.list());
    } catch (e) {
      toast.err(e);
    }
  }

  useEffect(() => {
    void reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return items;
    return items.filter((p) =>
      [p.name, p.host ?? '', p.username ?? '', p.lastCheck?.country ?? '']
        .join(' ')
        .toLowerCase()
        .includes(q),
    );
  }, [items, query]);

  async function addOne(): Promise<void> {
    if (!draft.host?.trim() || !draft.port) {
      toast.warn('Host and port are required.');
      return;
    }
    const { name, ...config } = draft;
    const added = await toast.run(
      () => window.hub.proxies.add(config, name || `${config.host}:${config.port}`),
      'Proxy added.',
    );
    if (added) {
      setAddOpen(false);
      setDraft({ kind: 'http', name: '', host: '', port: undefined });
      await reload();
    }
  }

  async function addBulk(): Promise<void> {
    if (!bulkText.trim()) {
      toast.warn('Paste at least one proxy line.');
      return;
    }
    const res = await toast.run(() => window.hub.proxies.addBulk(bulkText));
    if (!res) return;
    if (!res.added) {
      toast.err('None of those lines could be parsed as a proxy.');
      return;
    }
    toast.ok(
      `Added ${res.added} prox${res.added === 1 ? 'y' : 'ies'}${res.failed.length ? `, ${res.failed.length} line(s) skipped` : ''}.`,
    );
    if (res.failed.length) {
      toast.warn(`Skipped line ${res.failed.map((f) => f.line).join(', ')}.`);
    }
    setBulkOpen(false);
    setBulkText('');
    await reload();
  }

  async function check(p: SavedProxy): Promise<void> {
    setCheckingId(p.id);
    const res = await toast.run(() => window.hub.proxies.checkSaved(p.id));
    setCheckingId(null);
    await reload();
    if (res && !res.ok) toast.err(`${p.name}: ${res.error ?? 'no response'}`);
  }

  async function checkAll(): Promise<void> {
    setCheckingAll(true);
    let ok = 0;
    // Sequential on purpose: firing dozens of parallel requests through the same
    // provider is a good way to get rate-limited or temporarily banned.
    for (const p of items) {
      try {
        const res = await window.hub.proxies.checkSaved(p.id);
        if (res.ok) ok++;
      } catch {
        /* individual failures are visible in the table */
      }
    }
    setCheckingAll(false);
    await reload();
    toast.info(`${ok} of ${items.length} proxies responded.`);
  }

  return (
    <>
      <div class="topbar">
        <div>
          <h1>Proxies</h1>
          <div class="sub">
            {items.length} saved · {items.filter((p) => p.lastCheck?.ok).length} verified
          </div>
        </div>
        <div class="topbar-actions">
          <div class="search">
            <span class="icon">⌕</span>
            <input
              type="text"
              placeholder="Search proxies…"
              value={query}
              onInput={(e) => setQuery((e.currentTarget as HTMLInputElement).value)}
            />
          </div>
          <button class="btn" onClick={checkAll} disabled={!items.length || checkingAll}>
            {checkingAll ? 'Checking…' : 'Check all'}
          </button>
          <button class="btn" onClick={() => setBulkOpen(true)}>
            Bulk paste
          </button>
          <button class="btn primary" onClick={() => setAddOpen(true)}>
            + Add proxy
          </button>
        </div>
      </div>

      <div class="content">
        {!items.length ? (
          <Empty
            icon="⇄"
            title="No proxies saved"
            text="Add proxies here once and attach them to profiles by name. Checking a proxy reports its real exit IP, country and latency, which is also the value used for geo-IP locale."
            action={
              <div class="row" style={{ justifyContent: 'center' }}>
                <button class="btn primary" onClick={() => setAddOpen(true)}>
                  + Add proxy
                </button>
                <button class="btn" onClick={() => setBulkOpen(true)}>
                  Bulk paste
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
                  <th>Type</th>
                  <th>Address</th>
                  <th>Status</th>
                  <th>Exit IP</th>
                  <th>Latency</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {filtered.map((p) => (
                  <tr key={p.id}>
                    <td style={{ maxWidth: 200 }}>
                      <div class="name-main">
                        <div class="title">{p.name}</div>
                        <div class="meta">
                          {p.username ? `${p.username} · ` : ''}
                          added {timeAgo(p.createdAt)}
                        </div>
                      </div>
                    </td>
                    <td class="dim nowrap">{p.kind}</td>
                    <td class="mono nowrap">
                      {p.host}:{p.port}
                    </td>
                    <td>{checkBadge(p)}</td>
                    <td class="mono nowrap">
                      {p.lastCheck?.ok ? (
                        <span title={[p.lastCheck.city, p.lastCheck.timezone].filter(Boolean).join(' · ')}>
                          {p.lastCheck.ip}
                        </span>
                      ) : (
                        <span class="faint">—</span>
                      )}
                    </td>
                    <td class="nowrap faint">
                      {p.lastCheck?.latencyMs ? `${p.lastCheck.latencyMs} ms` : '—'}
                    </td>
                    <td class="actions">
                      <div class="row">
                        <button
                          class="btn sm"
                          onClick={() => void check(p)}
                          disabled={checkingId === p.id || checkingAll}
                        >
                          {checkingId === p.id ? '…' : 'Check'}
                        </button>
                        <button class="btn sm ghost" onClick={() => setDeleting(p)} title="Delete">
                          ✕
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {addOpen ? (
        <Modal
          title="Add proxy"
          onClose={() => setAddOpen(false)}
          footer={
            <>
              <button class="btn" onClick={() => setAddOpen(false)}>
                Cancel
              </button>
              <button class="btn primary" onClick={addOne}>
                Add
              </button>
            </>
          }
        >
          <div class="grid2">
            <Field label="Name" hint="Blank = host:port.">
              <input
                type="text"
                value={draft.name}
                placeholder="US East residential"
                onInput={(e) => setDraft({ ...draft, name: (e.currentTarget as HTMLInputElement).value })}
              />
            </Field>
            <Field label="Type">
              <select
                value={draft.kind}
                onChange={(e) =>
                  setDraft({
                    ...draft,
                    kind: (e.currentTarget as HTMLSelectElement).value as ProxyConfig['kind'],
                  })
                }
              >
                <option value="http">HTTP</option>
                <option value="https">HTTPS</option>
                <option value="socks5">SOCKS5</option>
              </select>
            </Field>
          </div>
          <div class="grid2" style={{ marginTop: 14 }}>
            <Field label="Host">
              <input
                type="text"
                value={draft.host ?? ''}
                placeholder="proxy.example.com"
                onInput={(e) =>
                  setDraft({ ...draft, host: (e.currentTarget as HTMLInputElement).value.trim() })
                }
              />
            </Field>
            <Field label="Port">
              <input
                type="number"
                value={draft.port ?? ''}
                placeholder="8080"
                onInput={(e) => {
                  const n = Number.parseInt((e.currentTarget as HTMLInputElement).value, 10);
                  setDraft({ ...draft, port: Number.isFinite(n) ? n : undefined });
                }}
              />
            </Field>
          </div>
          <div class="grid2" style={{ marginTop: 14 }}>
            <Field label="Username">
              <input
                type="text"
                value={draft.username ?? ''}
                placeholder="Optional"
                onInput={(e) =>
                  setDraft({ ...draft, username: (e.currentTarget as HTMLInputElement).value || undefined })
                }
              />
            </Field>
            <Field label="Password" hint="Encrypted with the OS keychain.">
              <input
                type="password"
                value={draft.password ?? ''}
                placeholder="Optional"
                onInput={(e) =>
                  setDraft({ ...draft, password: (e.currentTarget as HTMLInputElement).value || undefined })
                }
              />
            </Field>
          </div>
          <div style={{ marginTop: 14 }}>
            <Field label="IP rotation URL" hint="Optional GET endpoint that changes the exit IP.">
              <input
                type="url"
                value={draft.rotationUrl ?? ''}
                placeholder="https://provider/rotate?key=…"
                onInput={(e) =>
                  setDraft({
                    ...draft,
                    rotationUrl: (e.currentTarget as HTMLInputElement).value || undefined,
                  })
                }
              />
            </Field>
          </div>
        </Modal>
      ) : null}

      {bulkOpen ? (
        <Modal
          title="Bulk paste proxies"
          subtitle="One proxy per line"
          onClose={() => setBulkOpen(false)}
          footer={
            <>
              <button class="btn" onClick={() => setBulkOpen(false)}>
                Cancel
              </button>
              <button class="btn primary" onClick={addBulk}>
                Add all
              </button>
            </>
          }
        >
          <Callout icon="i">
            Recognised formats — lines starting with <span class="mono">#</span> are ignored, and a
            <span class="mono"> Label | </span>prefix is used as the name:
            <div class="code-block" style={{ marginTop: 8 }}>
              {BULK_EXAMPLE}
            </div>
          </Callout>
          <div style={{ marginTop: 14 }}>
            <textarea
              style={{ minHeight: 180 }}
              value={bulkText}
              placeholder="203.0.113.10:8080:user:pass"
              onInput={(e) => setBulkText((e.currentTarget as HTMLTextAreaElement).value)}
            />
          </div>
        </Modal>
      ) : null}

      {deleting ? (
        <ConfirmModal
          title={`Delete “${deleting.name}”?`}
          message="Profiles that copied this proxy keep their own settings — only the library entry is removed."
          confirmLabel="Delete"
          danger
          onClose={() => setDeleting(null)}
          onConfirm={() => {
            const id = deleting.id;
            void toast
              .run(() => window.hub.proxies.remove(id), 'Proxy deleted.')
              .then(() => reload());
          }}
        />
      ) : null}
    </>
  );
}
