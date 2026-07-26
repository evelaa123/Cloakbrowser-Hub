/**
 * Cookies tab of the profile editor.
 *
 * Import is a two-step flow on purpose: validate first, show what was actually
 * recognised (count, domains, which services look logged in), and only then
 * write. Silently importing a file that turned out to be the wrong format is how
 * people lose a session and never find out why.
 */

import type { JSX } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import type { CookieValidation, Profile } from '../../shared/types';
import { useToast } from '../components/toast';
import { Callout, Check, Field, timeAgo } from '../components/ui';

export function CookiesTab(props: {
  profile: Profile;
  onChanged: (patch: Partial<Profile>) => void;
}): JSX.Element {
  const toast = useToast();
  const [summary, setSummary] = useState<{ count: number; domains: string[] }>();
  const [text, setText] = useState('');
  const [validation, setValidation] = useState<CookieValidation>();
  const [files, setFiles] = useState<string[]>([]);
  const [replace, setReplace] = useState(false);
  const [defaultDomain, setDefaultDomain] = useState('');
  const [working, setWorking] = useState(false);

  const profileId = props.profile.id;

  async function loadSummary(): Promise<void> {
    try {
      setSummary(await window.hub.cookies.summary(profileId));
    } catch {
      setSummary({ count: 0, domains: [] });
    }
  }

  useEffect(() => {
    void loadSummary();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [profileId]);

  /** Re-read the jar and push the new counts up into the editor draft. */
  async function afterImport(): Promise<void> {
    await loadSummary();
    const fresh = await window.hub.profiles.get(profileId);
    if (fresh?.cookies) props.onChanged({ cookies: fresh.cookies });
  }

  async function pickFiles(): Promise<void> {
    const picked = await toast.run(() => window.hub.cookies.pickFiles());
    if (!picked?.length) return;
    setFiles(picked);
    setText('');
    // Validate the first file so the user sees what they picked before writing.
    const v = await toast.run(() => window.hub.cookies.validateFile(picked[0]!));
    if (v) setValidation(v);
  }

  async function validateTextNow(value: string): Promise<void> {
    setText(value);
    setFiles([]);
    if (!value.trim()) {
      setValidation(undefined);
      return;
    }
    try {
      setValidation(await window.hub.cookies.validateText(value));
    } catch (e) {
      setValidation({
        ok: false,
        count: 0,
        format: 'unknown',
        domains: [],
        authHints: [],
        suggestedName: '',
        error: e instanceof Error ? e.message : String(e),
      });
    }
  }

  async function doImport(): Promise<void> {
    setWorking(true);
    const opts = { replace, domain: defaultDomain.trim() || undefined };
    if (files.length) {
      const res = await toast.run(() => window.hub.cookies.importFiles(profileId, files, opts));
      if (res) {
        toast.ok(
          `Imported ${res.count} cookie(s) from ${res.files} file(s)${res.authHints.length ? ` — session detected for ${res.authHints.join(', ')}` : ''}.`,
        );
        setFiles([]);
        setValidation(undefined);
        await afterImport();
      }
    } else if (text.trim()) {
      const res = await toast.run(() => window.hub.cookies.importText(profileId, text, opts));
      if (res) {
        toast.ok(
          `Imported ${res.count} cookie(s)${res.authHints.length ? ` — session detected for ${res.authHints.join(', ')}` : ''}.`,
        );
        setText('');
        setValidation(undefined);
        await afterImport();
      }
    } else {
      toast.warn('Pick a file or paste cookies first.');
    }
    setWorking(false);
  }

  async function exportCookies(format: 'json' | 'netscape'): Promise<void> {
    const res = await toast.run(() => window.hub.cookies.exportToFile(profileId, format));
    if (res) toast.ok(`Exported ${res.count} cookie(s).`);
  }

  async function clearCookies(): Promise<void> {
    await toast.run(() => window.hub.cookies.clear(profileId), 'Cookie jar cleared.');
    await afterImport();
  }

  const canImport = (files.length > 0 || text.trim().length > 0) && !working;
  const meta = props.profile.cookies;

  return (
    <>
      <div class="stat-grid">
        <div class="stat">
          <div class="k">In jar</div>
          <div class="v">{summary?.count ?? meta?.count ?? 0}</div>
        </div>
        <div class="stat">
          <div class="k">Domains</div>
          <div class="v">{summary?.domains.length ?? meta?.domains ?? 0}</div>
        </div>
        <div class="stat">
          <div class="k">Updated</div>
          <div class="v small">{timeAgo(meta?.updatedAt)}</div>
        </div>
        <div class="stat">
          <div class="k">Source</div>
          <div class="v small">{meta?.source ?? '—'}</div>
        </div>
      </div>

      <div class="row" style={{ marginTop: 12 }}>
        <button class="btn sm" onClick={() => void exportCookies('json')} disabled={!summary?.count}>
          Export JSON
        </button>
        <button
          class="btn sm"
          onClick={() => void exportCookies('netscape')}
          disabled={!summary?.count}
        >
          Export cookies.txt
        </button>
        <button class="btn sm danger" onClick={() => void clearCookies()} disabled={!summary?.count}>
          Clear jar
        </button>
      </div>

      {summary?.domains.length ? (
        <div style={{ marginTop: 12 }}>
          <div class="section-head" style={{ marginTop: 8 }}>
            Domains in jar
          </div>
          <div class="code-block" style={{ maxHeight: 120 }}>
            {summary.domains.join('\n')}
          </div>
        </div>
      ) : null}

      <div class="section-head">Import cookies</div>
      <Callout icon="i">
        Accepted formats: a JSON export (EditThisCookie, Cookie-Editor, Playwright
        <span class="mono"> storageState</span>), a Netscape
        <span class="mono"> cookies.txt</span>, or a raw
        <span class="mono"> Cookie:</span> header line. Cookies are injected into the browser on
        every launch and saved back when the session closes.
      </Callout>

      <div class="row" style={{ marginTop: 14 }}>
        <button class="btn" onClick={pickFiles}>
          Choose file(s)…
        </button>
        {files.length ? (
          <span class="faint mono" style={{ wordBreak: 'break-all' }}>
            {files.length === 1 ? files[0] : `${files.length} files selected`}
          </span>
        ) : null}
      </div>

      <div style={{ marginTop: 14 }}>
        <Field label="…or paste cookies here">
          <textarea
            value={text}
            placeholder={'[{"name":"sid","value":"…","domain":".example.com","path":"/"}]'}
            onInput={(e) => void validateTextNow((e.currentTarget as HTMLTextAreaElement).value)}
          />
        </Field>
      </div>

      {validation ? (
        <div style={{ marginTop: 12 }}>
          {validation.ok ? (
            <Callout tone="ok" icon="✓">
              Recognised <strong>{validation.count}</strong> cookie(s) in{' '}
              <strong>{validation.format}</strong> format across {validation.domains.length}{' '}
              domain(s).
              {validation.authHints.length ? (
                <>
                  {' '}
                  Looks like a signed-in session for <strong>{validation.authHints.join(', ')}</strong>
                  .
                </>
              ) : (
                <>
                  {' '}
                  No known service session was detected — this may still be fine, but double-check
                  you exported the cookies while logged in, with httpOnly cookies included.
                </>
              )}
            </Callout>
          ) : (
            <Callout tone="err" icon="✕">
              {validation.error ?? 'Those cookies could not be parsed.'}
            </Callout>
          )}
        </div>
      ) : null}

      <div style={{ marginTop: 16, display: 'flex', flexDirection: 'column', gap: 12 }}>
        <Check
          checked={replace}
          onChange={setReplace}
          label="Replace the existing jar"
          hint="Off (default) merges with what is already stored, so importing a second account's cookies does not wipe the first."
        />
      </div>

      <div style={{ marginTop: 14 }}>
        <Field
          label="Default domain"
          hint="Only used for entries that have no domain of their own, e.g. a pasted Cookie: header."
        >
          <input
            type="text"
            value={defaultDomain}
            placeholder=".example.com"
            onInput={(e) => setDefaultDomain((e.currentTarget as HTMLInputElement).value)}
          />
        </Field>
      </div>

      <div class="row" style={{ marginTop: 16 }}>
        <button class="btn primary" onClick={doImport} disabled={!canImport}>
          {working ? 'Importing…' : 'Import cookies'}
        </button>
        {files.length || text ? (
          <button
            class="btn ghost"
            onClick={() => {
              setFiles([]);
              setText('');
              setValidation(undefined);
            }}
          >
            Clear selection
          </button>
        ) : null}
      </div>
    </>
  );
}
