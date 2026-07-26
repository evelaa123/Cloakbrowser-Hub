/**
 * Import an existing browser profile from this machine.
 *
 * The honest explanation is on the page: cookie values in Chromium are encrypted
 * with an OS-level key, so instead of decrypting them we copy the profile's own
 * session state and let the stealth browser decrypt it. That works identically on
 * all three platforms and cannot silently corrupt a session.
 */

import type { JSX } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import type { DiscoveredBrowserProfile, FolderScan } from '../../shared/types';
import { useToast } from '../components/toast';
import { Callout, Card, Empty, Field, Modal, Spinner } from '../components/ui';
import type { Route } from '../App';

export function ImportPage(props: { onNavigate: (r: Route) => void }): JSX.Element {
  const toast = useToast();
  const [found, setFound] = useState<DiscoveredBrowserProfile[]>([]);
  const [scanning, setScanning] = useState(true);
  const [selected, setSelected] = useState<DiscoveredBrowserProfile | null>(null);
  const [name, setName] = useState('');
  const [copyData, setCopyData] = useState(true);
  const [importing, setImporting] = useState(false);
  /** Which manual scan is running, so both buttons disable together. */
  const [busy, setBusy] = useState<'folder' | 'archive' | null>(null);
  /** Results of a manual folder/archive scan, kept separate from the auto-scan. */
  const [manual, setManual] = useState<FolderScan | null>(null);

  /**
   * Release the temp directory an archive was unpacked into.
   *
   * Only after the user has finished with the results — the import copies files
   * out of it, so deleting on import would race with the copy.
   */
  function releaseExtraction(scan: FolderScan | null): void {
    if (scan?.extractedTo) void window.hub.importer.cleanup(scan.extractedTo).catch(() => undefined);
  }

  async function runManualScan(kind: 'folder' | 'archive'): Promise<void> {
    setBusy(kind);
    try {
      const res =
        kind === 'folder'
          ? await window.hub.importer.scanFolder()
          : await window.hub.importer.scanArchive();
      if (res.cancelled) return;

      // Replacing an earlier archive scan: free the previous temp dir first, or
      // it leaks until the OS clears /tmp.
      if (manual?.extractedTo && manual.extractedTo !== res.extractedTo) releaseExtraction(manual);

      setManual(res);
      if (!res.profiles.length && res.note) toast.warn(res.note);
      else if (res.truncated) {
        toast.warn('That folder is very large, so the scan stopped early. Pick a more specific folder if your profile is missing.');
      }
    } catch (e) {
      toast.err(e);
    } finally {
      setBusy(null);
    }
  }

  const pickFolder = (): Promise<void> => runManualScan('folder');
  const pickArchive = (): Promise<void> => runManualScan('archive');

  async function scan(): Promise<void> {
    setScanning(true);
    try {
      setFound(await window.hub.importer.discover());
    } catch (e) {
      toast.err(e);
    } finally {
      setScanning(false);
    }
  }

  useEffect(() => {
    void scan();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function openDialog(p: DiscoveredBrowserProfile): void {
    setSelected(p);
    setName(`${p.browser} — ${p.name}`);
    setCopyData(true);
  }

  async function doImport(): Promise<void> {
    if (!selected) return;
    setImporting(true);
    const res = await toast.run(() =>
      window.hub.importer.importProfile({
        sourcePath: selected.path,
        browser: selected.browser,
        name: name.trim() || undefined,
        copyData,
      }),
    );
    setImporting(false);
    if (res) {
      toast.ok(
        copyData
          ? `Profile created — ${res.copied} item(s) copied.`
          : 'Profile created from the browser settings.',
      );
      if (res.warning) toast.warn(res.warning);
      setSelected(null);
      props.onNavigate('profiles');
    }
  }

  return (
    <>
      <div class="topbar">
        <div>
          <h1>Import</h1>
          <div class="sub">Bring an existing browser profile into the Hub</div>
        </div>
        <div class="topbar-actions">
          <button class="btn" onClick={() => void pickFolder()} disabled={scanning || busy !== null}>
            {busy === 'folder' ? 'Scanning…' : 'Scan a folder…'}
          </button>
          <button class="btn" onClick={() => void pickArchive()} disabled={scanning || busy !== null}>
            {busy === 'archive' ? 'Unpacking…' : 'Import from .zip…'}
          </button>
          <button class="btn" onClick={scan} disabled={scanning || busy !== null}>
            {scanning ? 'Scanning…' : 'Re-scan'}
          </button>
        </div>
      </div>

      <div class="content">
        <Callout icon="i">
          <strong>Close the source browser before importing.</strong> A running browser holds a lock
          on its profile and copying it then would produce a corrupted session — the import refuses
          to run in that case rather than giving you a broken profile.
        </Callout>

        {manual && (
          <div style={{ marginTop: 14 }}>
            <Card
              title={manual.extractedTo ? 'Profiles found in the archive' : 'Profiles found in that folder'}
              desc={manual.root}
            >
              {manual.profiles.length ? (
                <div class="table-wrap">
                  <table>
                    <thead>
                      <tr>
                        <th>Browser</th>
                        <th>Profile</th>
                        <th>Cookies</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      {manual.profiles.map((p) => (
                        <tr key={p.path}>
                          <td class="nowrap">{p.browser}</td>
                          <td style={{ maxWidth: 380 }}>
                            <div class="name-main">
                              <div class="title">{p.name}</div>
                              <div class="meta mono" title={p.path}>
                                {p.path}
                              </div>
                            </div>
                          </td>
                          <td>
                            {p.hasCookies ? (
                              <span class="badge ok">Present</span>
                            ) : (
                              <span class="badge">None</span>
                            )}
                          </td>
                          <td class="actions">
                            <button class="btn sm primary" onClick={() => openDialog(p)}>
                              Import
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <Empty icon="⤓" title="Nothing importable there" text={manual.note ?? ''} />
              )}
              {manual.note && manual.profiles.length ? (
                <div class="faint" style={{ marginTop: 10 }}>
                  {manual.note}
                </div>
              ) : null}
              <div style={{ marginTop: 12 }}>
                <button
                  class="btn sm"
                  onClick={() => {
                    releaseExtraction(manual);
                    setManual(null);
                  }}
                >
                  Clear results
                </button>
              </div>
            </Card>
          </div>
        )}

        <div style={{ marginTop: 14 }}>
          <Card
            title="Browser profiles on this machine"
            desc="Chrome, Edge, Brave, Chromium, Opera, Vivaldi, Yandex and Firefox are searched in their standard locations for your operating system."
          >
            {scanning ? (
              <div class="empty" style={{ padding: 30 }}>
                <Spinner />
                <p style={{ marginTop: 12 }}>Looking for browser profiles…</p>
              </div>
            ) : !found.length ? (
              <Empty
                icon="⤓"
                title="No browser profiles found"
                text="Nothing was found in the standard locations. You can still create a profile manually and import cookies into it from a browser extension export."
                action={
                  <button class="btn" onClick={() => props.onNavigate('profiles')}>
                    Go to Profiles
                  </button>
                }
              />
            ) : (
              <div class="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Browser</th>
                      <th>Profile</th>
                      <th>Cookies</th>
                      <th>Size</th>
                      <th />
                    </tr>
                  </thead>
                  <tbody>
                    {found.map((p) => (
                      <tr key={p.path}>
                        <td class="nowrap">{p.browser}</td>
                        <td style={{ maxWidth: 300 }}>
                          <div class="name-main">
                            <div class="title">{p.name}</div>
                            <div class="meta mono" title={p.path}>
                              {p.path}
                            </div>
                          </div>
                        </td>
                        <td>
                          {p.hasCookies ? (
                            <span class="badge ok">Present</span>
                          ) : (
                            <span class="badge">None</span>
                          )}
                        </td>
                        <td class="nowrap faint">
                          {p.sizeMb != null ? `${p.sizeMb} MB` : '—'}
                        </td>
                        <td class="actions">
                          <button class="btn sm primary" onClick={() => openDialog(p)}>
                            Import
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </Card>

          <Card
            title="Importing cookies only"
            desc="If you just want the session and not the whole profile, export cookies from the source browser and import them into a Hub profile."
          >
            <ol class="dim" style={{ margin: 0, paddingLeft: 20, lineHeight: 1.9 }}>
              <li>
                Install a cookie exporter in the source browser (Cookie-Editor, EditThisCookie or
                similar) and export as JSON while logged in.
              </li>
              <li>
                Create or open a profile here, go to the <strong>Cookies</strong> tab and import that
                file. A Netscape <span class="mono">cookies.txt</span> or a raw{' '}
                <span class="mono">Cookie:</span> header also works.
              </li>
              <li>
                Make sure the export includes httpOnly cookies — most session cookies are httpOnly,
                and an export without them will not keep you signed in.
              </li>
            </ol>
          </Card>
        </div>
      </div>

      {selected ? (
        <Modal
          title={`Import ${selected.browser} profile`}
          subtitle={selected.name}
          onClose={() => setSelected(null)}
          footer={
            <>
              <button class="btn" onClick={() => setSelected(null)}>
                Cancel
              </button>
              <button class="btn primary" onClick={doImport} disabled={importing}>
                {importing ? 'Importing…' : 'Import'}
              </button>
            </>
          }
        >
          <Field label="New profile name">
            <input
              type="text"
              value={name}
              onInput={(e) => setName((e.currentTarget as HTMLInputElement).value)}
            />
          </Field>

          <div style={{ marginTop: 16 }}>
            <label class="check">
              <input
                type="checkbox"
                checked={copyData}
                onChange={(e) => setCopyData((e.currentTarget as HTMLInputElement).checked)}
              />
              <span class="check-text">
                <strong>Copy browser data (cookies, logins, local storage)</strong>
                <span>
                  Copies the profile's session state so you stay signed in. Uncheck to import only
                  the language settings and start with a clean profile.
                </span>
              </span>
            </label>
          </div>

          {copyData ? (
            <div style={{ marginTop: 14 }}>
              <Callout tone="warn" icon="!">
                {selected.sizeMb != null && selected.sizeMb > 500
                  ? `This profile is about ${selected.sizeMb} MB, so the copy will take a moment. `
                  : ''}
                Caches are skipped — only session-bearing data is copied. The source profile is read
                only and never modified.
              </Callout>
            </div>
          ) : null}

          {!selected.hasCookies && copyData ? (
            <div style={{ marginTop: 14 }}>
              <Callout tone="warn" icon="!">
                No cookie database was found in this profile, so no signed-in session will carry
                over.
              </Callout>
            </div>
          ) : null}
        </Modal>
      ) : null}
    </>
  );
}
