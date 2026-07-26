# CloakBrowser Hub

A desktop manager for **anti-detect browser profiles**, built on top of the
[`cloakbrowser`](https://www.npmjs.com/package/cloakbrowser) patched-Chromium
runtime. Create isolated profiles, give each one its own fingerprint, proxy,
timezone and cookie jar, and launch them side by side from a single window.

Electron + Preact + TypeScript. Windows, macOS and Linux.

> **Status: 0.1.0, pre-release.** The full feature surface is implemented and
> unit-tested, but the app has not yet been run against live sites end to end.
> See [Limitations](#limitations).

---

## Why

Running multiple accounts on the same machine normally leaks a shared identity:
the same GPU strings, screen size, fonts, timezone and WebRTC IP appear
everywhere. CloakBrowser Hub gives each profile a **consistent, isolated
identity** — fingerprint, network egress and locale all agree with each other —
and keeps its cookies and user-data directory separate from every other profile.

---

## Features

### Profiles
- Unlimited local profiles, each with its own user-data directory and cookie jar.
- Per-profile fingerprint: platform, screen size, GPU vendor/renderer, CPU cores,
  device memory, storage quota, font metrics and a stable noise seed.
- One-click **randomise**, drawn from realistic hardware pools rather than
  uniform noise — a 4-core machine reporting a 32 GB memory size is itself a
  signal, so the pools are weighted toward plausible combinations.
- Duplicate, export and import profiles as JSON.
- Colour-coded rows, search and bulk launch.

### Fingerprint consistency
The fingerprint is passed to Chromium as command-line switches
(`--fingerprint-*`), so it is applied by the browser itself rather than patched
in by JavaScript after page load — there is no injected-script timing window for
a detector to catch. The **Preview args** button in the editor shows the exact
argv for a profile before you launch it.

Coupled values are derived, not chosen independently: locale drives `--lang`,
`Accept-Language` and `--fingerprint-locale`; the timezone follows the selected
region; WebRTC egress follows the proxy IP. 18 locale presets cover the common
regions (US ×3, UK, DE, FR, NL, ES, IT, PL, TR, BR, CA, AU, IN, SG, JP, AE).

### Proxies
- HTTP, HTTPS, SOCKS4 and SOCKS5.
- A tolerant parser accepts the formats providers actually ship:
  `host:port`, `host:port:user:pass`, `user:pass@host:port`,
  `host:port:user`, an optional `scheme://` prefix, and a leading
  `US-1 | ` style label. `host:port:user:pass` and `user:pass:host:port` are
  disambiguated by inspecting which side looks like a hostname plus a numeric
  port, falling back to the provider-standard order when genuinely ambiguous.
- Bulk paste, one proxy per line.
- **Check** resolves the live egress IP, country, city and timezone through the
  proxy, with three geo-IP providers tried in order so a single outage does not
  block a check.
- Rotate a profile onto the next proxy in the pool.

### Cookies
Import into a profile from three formats, auto-detected:
- **JSON** — Cookie-Editor, EditThisCookie and Playwright `storageState` shapes.
- **Netscape** `cookies.txt` — curl, wget and browser extensions.
- **Raw `Cookie:` header** — `name=value; name2=value2`. This format carries no
  domain, so the UI asks for one when it detects it.

Validation runs before import and reports the detected format, cookie count,
domains and any authentication cookies it recognises. Two rough edges of real
exports are handled: `HttpOnly` flags that Netscape exports omit are restored
for cookies known to require them, and `SameSite` is defaulted for known
cross-site identity providers — without this, imported sessions silently fail
to authenticate.

### Import from installed browsers
Discover and import existing local profiles from **Chrome, Edge, Brave,
Chromium, Opera, Vivaldi, Yandex and Firefox**, on all three platforms.

### Sessions
Launch, stop and stop-all, with live per-session status and a streaming log
viewer per session.

### Licensing
Reflects the upstream `cloakbrowser` tiers (`none` / `free` / `pro`), including
a GitHub sign-in flow for a free key and a seat-count hint. The patched-Chromium
binary is downloaded on demand with progress reporting, not bundled.

### Automation API
An opt-in local HTTP API (Settings → Automation API) for driving profiles from a
script. Starting a profile returns a **CDP endpoint**, so any Chrome DevTools
Protocol client attaches to a real Hub session — same profile directory, same
fingerprint switches, same proxy — rather than a separate browser you would have
to configure yourself. See [Automation](#automation) below.

---

## Install

Requires **Node.js 20+**.

```bash
git clone https://github.com/evelaa123/Cloakbrowser-Hub.git
cd Cloakbrowser-Hub
npm install
```

## Run

```bash
npm run dev     # development, with hot reload
npm start       # preview a production build
```

## Build

```bash
npm run build       # compile main, preload and renderer into dist/
npm run dist        # installer for the current platform
npm run dist:win    # or :mac / :linux / :all
```

The icon set is committed, so a build needs no extra step. To regenerate it
after changing `build/icon-master.png`:

```bash
python3 build/make-icon.py   # needs Pillow
```

That one script writes `build/icon.png`, `build/icons/*.png` **and** the sidebar
mark at `src/renderer/assets/cloak-mark.png`, so the in-app mark and the
packaged icon are always the same artwork. Sizes below 64px are emitted without
the `HUB` wordmark — three glyphs at 32px are a grey smear, and the cloak
silhouette alone reads better.

## Test

```bash
npm test        # vitest, 154 tests
npm run test:watch
npm run typecheck
```

---

## Architecture

Standard Electron three-process split, with `contextIsolation` on and no
`nodeIntegration` in the renderer.

```
src/
  main/                     Node side — full privileges
    index.ts                app entry, window lifecycle
    ipc/handlers.ts         every IPC endpoint
    browser/                session spawn + lifecycle
    importers/              installed-browser profile discovery
    services/               license, proxy, cookies, store, secrets, paths
  preload/index.ts          contextBridge — the only renderer↔main surface
  renderer/                 Preact UI (no Node access)
    App.tsx, state.tsx      shell + global store
    pages/                  Profiles, Editor, Proxies, Cookies, Import,
                            License, Settings, Logs
  shared/                   used by both sides
    types.ts                domain model
    ipc.ts                  channel names + payload types
    fingerprint-args.ts     fingerprint → Chromium argv
    defaults.ts             hardware pools, locale presets, factory defaults
```

**IPC is the contract.** All 52 channels — 48 request/response plus 4
main→renderer events — are declared once in `src/shared/ipc.ts` with typed
payloads, so the renderer, preload bridge and main handlers cannot drift apart
without a type error. Two dedicated test files (`ipc-contract`,
`preload-contract`) assert that every declared channel is actually implemented
and exposed, and additionally that the 4 event channels are *not* registered as
invoke handlers. A channel added to one layer and forgotten in another fails the
suite rather than failing silently at runtime.

The renderer never touches `fs`, `net` or `child_process`; everything crosses
the preload bridge.

---

## Automation

Off by default. Enable it in **Settings → Automation API**, which also shows the
access token and a copyable snippet.

The server binds `127.0.0.1` only — never `0.0.0.0` — so nothing on the network
can reach it, and no CORS headers are ever sent, so a web page in a browser
cannot call it either. Every request needs the token:

```
authorization: Bearer <token>
```

`x-api-token: <token>` is accepted as an alternative. Tokens are compared by
hashing both sides and using `crypto.timingSafeEqual`, so a wrong token costs the
same time as a right one and the response reveals nothing about the length.
Bodies are capped at 256 KB.

### Routes

| Method | Path | Result |
|---|---|---|
| `GET` | `/health` | `{ ok, api, version }`. The only unauthenticated route — liveness only, no data. |
| `GET` | `/profiles` | `{ profiles: [{ id, name, platform, running }] }` |
| `POST` | `/profiles` | Creates a profile from the JSON body (all fields optional). `201 { profile }` |
| `GET` | `/profiles/:id` | `{ profile }` — the full record |
| `PATCH` | `/profiles/:id` | Merges the JSON body into the profile. `{ profile }` |
| `DELETE` | `/profiles/:id` | Deletes it. Pass `?keepData=true` to keep the profile directory. `409` if running. |
| `POST` | `/profiles/:id/start` | Launches it, returns the CDP endpoint (below) |
| `POST` | `/profiles/:id/stop` | `{ stopped: true }` |
| `GET` | `/profiles/:id/endpoint` | The CDP endpoint of an already-running profile, or `404` |

Unknown paths return `404`; `OPTIONS` returns `405`.

`start` is **idempotent**: calling it on an already-running profile returns the
existing endpoint with `alreadyRunning: true` rather than launching a second
browser, so a client retrying after a timeout cannot leave an orphan process.

If the binary is missing, `start` downloads it first — the call just takes
longer, it does not fail.

### The CDP endpoint

`start` and `endpoint` return:

```json
{
  "profileId": "…",
  "profileName": "…",
  "wsEndpoint": "ws://127.0.0.1:41763/devtools/browser/<uuid>",
  "httpEndpoint": "http://127.0.0.1:41763",
  "port": 41763
}
```

The port is assigned by the kernel per launch, not fixed, so concurrent profiles
cannot collide. The `wsEndpoint` UUID is read back from Chromium's
`/json/version` — it cannot be derived from the port.

Attaching gives you the **real Hub session**: same profile directory, same
`--fingerprint-*` switches, same proxy. That is the point of going through the
API rather than launching Chromium yourself.

```js
const r = await fetch(`${API}/profiles/${id}/start`, {
  method: 'POST',
  headers: { authorization: `Bearer ${TOKEN}` },
});
const { wsEndpoint, httpEndpoint } = await r.json();

// Puppeteer
const browser = await puppeteer.connect({ browserWSEndpoint: wsEndpoint });

// Playwright
const browser = await chromium.connectOverCDP(wsEndpoint);
```

Selenium attaches over the HTTP endpoint instead:

```python
opts = webdriver.ChromeOptions()
opts.debugger_address = httpEndpoint.removeprefix("http://")  # "127.0.0.1:41763"
driver = webdriver.Chrome(options=opts)
```

Sessions started *before* the API was enabled have no debugging port; restart
them to control them.

Rotating the token in Settings takes effect immediately — old tokens are
rejected from the next request on.

---

## Testing

154 tests across 8 files:

| File | Covers |
|---|---|
| `proxy.test.ts` | all accepted proxy formats, ambiguous-order disambiguation |
| `cookies.test.ts` | JSON / Netscape / header parsing, HttpOnly + SameSite repair |
| `fingerprint-args.test.ts` | fingerprint → argv, locale/timezone coupling |
| `automation.test.ts` | the API over real HTTP: auth, every route, start idempotency, port release |
| `cloakbrowser-api.test.ts` | pins upstream `binaryInfo` / `ensureBinary` signatures against the installed package |
| `ipc-contract.test.ts` | every declared channel has a handler |
| `preload-contract.test.ts` | bridge exposes exactly the declared surface |
| `renderer-smoke.test.tsx` | app shell mounts in jsdom, navigation, launch wiring |

The renderer test uses `act()` from `preact/test-utils`. Preact defers
`useEffect` behind `options.requestAnimationFrame`, which in jsdom is a real
~16 ms timer — flushing microtasks alone leaves the shell stuck on `Loading…`.
`act()` swaps both the rAF hook and the render debounce for queues it drains
itself, making the flush deterministic instead of timing-dependent.

---

## Limitations

Worth knowing before relying on this:

- **Not yet validated against live detection suites.** The fingerprint switches
  are unit-tested for correct argv construction, but no end-to-end run against
  CreepJS, Pixelscan or a real target site has been done. Treat anti-detect
  effectiveness as unverified.
- **No master password.** Profile data and proxy credentials are stored on disk
  unencrypted, under the OS user profile.
- **No recorded/no-code action scripting.** The automation API exposes profile
  and session control plus a CDP endpoint; writing the browser steps themselves
  is left to your own Puppeteer/Playwright/Selenium code.
- **License and binary download require network access** to `cloakbrowser.dev`,
  and depend on that upstream API remaining stable.
- **Unsigned builds.** No code-signing or notarisation is configured, so
  installers will trigger OS warnings.
- **The icon contains the Chrome logo.** It is the CloakBrowser hooded-cloak
  mark with a `HUB` wordmark, and the disc inside the hood is the Google Chrome
  logo, which is a Google trademark. Fine for private and internal use;
  redistributing through an app store may not be.

---

## Legal

Intended for legitimate multi-account work — QA, ad verification, web-scraping
compliance, managing your own accounts across platforms. Using it to violate a
site's terms of service, commit fraud or evade a ban is on you. Check the rules
of any platform you point it at.

## Licence

Not yet chosen. All rights reserved for now.
