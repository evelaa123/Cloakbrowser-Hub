/**
 * Automation API settings card.
 *
 * Its own component rather than more inline JSX in SettingsPage because it owns
 * real state: the server can fail to bind (port in use) independently of the
 * stored setting, so "enabled" and "actually listening" are two different facts
 * and the UI has to show both. Silently rendering a checkbox as on while nothing
 * is listening is the failure mode worth designing against.
 */

import type { JSX } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import type { AutomationState } from '../../shared/types';
import { useToast } from '../components/toast';
import { Callout, Card, Check, Field } from '../components/ui';

export function AutomationCard(): JSX.Element {
  const toast = useToast();
  const [state, setState] = useState<AutomationState>();
  const [port, setPort] = useState('');
  const [revealed, setRevealed] = useState(false);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void (async () => {
      try {
        const s = await window.hub.automation.state();
        setState(s);
        setPort(String(s.settings.port));
      } catch {
        // Leave the card in its loading state; the error surfaces on first use.
      }
    })();
  }, []);

  if (!state) return <Card title="Automation API">Loading…</Card>;

  const { settings, listening, baseUrl } = state;

  async function apply(patch: Parameters<typeof window.hub.automation.set>[0]): Promise<void> {
    setBusy(true);
    const next = await toast.run(() => window.hub.automation.set(patch));
    setBusy(false);
    if (next) {
      setState(next);
      setPort(String(next.settings.port));
    }
  }

  async function rotate(): Promise<void> {
    setBusy(true);
    const next = await toast.run(
      () => window.hub.automation.rotateToken(),
      'Token rotated. Scripts using the old token will now be rejected.',
    );
    setBusy(false);
    if (next) setState(next);
  }

  function copy(text: string, label: string): void {
    void navigator.clipboard.writeText(text).then(
      () => toast.ok(`${label} copied.`),
      () => toast.warn(`Could not copy the ${label.toLowerCase()}.`),
    );
  }

  // Port edits are committed on blur/Enter rather than per keystroke: applying
  // mid-typing would try to bind ":37" and throw before the user finished.
  function commitPort(): void {
    const n = Number(port);
    if (!Number.isInteger(n) || n < 1024 || n > 65535) {
      toast.warn('Port must be a whole number between 1024 and 65535.');
      setPort(String(settings.port));
      return;
    }
    if (n !== settings.port) void apply({ port: n });
  }

  return (
    <Card
      title="Automation API"
      desc="Drive profiles from a script instead of clicking. Start a profile over HTTP, get a CDP endpoint back, then attach Puppeteer, Playwright or Selenium."
    >
      <Check
        label="Enable the local automation API"
        hint="Listens on 127.0.0.1 only. Never exposed to the network."
        checked={settings.enabled}
        disabled={busy}
        onChange={(v) => void apply({ enabled: v })}
      />

      {/* The disagreement case: stored as enabled, but nothing bound. */}
      {settings.enabled && !listening ? (
        <Callout tone="warn" icon="!">
          The API is enabled but not listening — port {settings.port} is most likely in use by
          another program. Change the port below.
        </Callout>
      ) : null}

      {settings.enabled && listening ? (
        <Callout tone="ok" icon="✓">
          Listening on <span class="mono">{baseUrl}</span>
        </Callout>
      ) : null}

      <Field label="Port" hint="1024–65535. Applied when you leave the field.">
        <div class="row" style={{ gap: 6 }}>
          <input
            type="text"
            class="mono"
            style={{ maxWidth: 140 }}
            value={port}
            disabled={busy}
            onInput={(e) => setPort((e.currentTarget as HTMLInputElement).value)}
            onBlur={commitPort}
            onKeyDown={(e) => {
              if ((e as KeyboardEvent).key === 'Enter') commitPort();
            }}
          />
        </div>
      </Field>

      <Field
        label="Access token"
        hint="Required on every request. Treat it like a password — it can launch browsers and read cookies."
      >
        <div class="row" style={{ gap: 6 }}>
          <input
            type={revealed ? 'text' : 'password'}
            class="mono"
            value={settings.token}
            readOnly
          />
          <button class="btn sm" onClick={() => setRevealed((r) => !r)}>
            {revealed ? 'Hide' : 'Reveal'}
          </button>
          <button class="btn sm" onClick={() => copy(settings.token, 'Token')}>
            Copy
          </button>
          <button class="btn sm ghost" disabled={busy} onClick={() => void rotate()}>
            Rotate
          </button>
        </div>
      </Field>

      {settings.enabled ? (
        <details style={{ marginTop: 14 }}>
          <summary style={{ cursor: 'pointer' }}>Quick start</summary>
          <div style={{ marginTop: 10 }}>
            <div class="row" style={{ justifyContent: 'space-between', alignItems: 'center' }}>
              <div class="dim">Start a profile and attach Puppeteer:</div>
              <button class="btn sm" onClick={() => copy(snippet(baseUrl, settings.token), 'Snippet')}>
                Copy snippet
              </button>
            </div>
            <pre class="mono code-block">{snippet(baseUrl, settings.token)}</pre>
            <div class="dim" style={{ marginTop: 8 }}>
              Full route list is in the README. Sessions started before you enabled this have no
              CDP port — restart them to control them.
            </div>
          </div>
        </details>
      ) : null}
    </Card>
  );
}

/** Runnable example, with the user's real port and token filled in. */
function snippet(baseUrl: string, token: string): string {
  return `const TOKEN = '${token}';
const API = '${baseUrl}';

const res = await fetch(\`\${API}/profiles/<profileId>/start\`, {
  method: 'POST',
  headers: { authorization: \`Bearer \${TOKEN}\` },
});
const { wsEndpoint } = await res.json();

// Puppeteer
const browser = await puppeteer.connect({ browserWSEndpoint: wsEndpoint });

// Playwright
// const browser = await chromium.connectOverCDP(wsEndpoint);`;
}
