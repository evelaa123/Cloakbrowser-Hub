<div align="center">

<img src="dotnet/src/CloakHub.App/Assets/app-icon.png" width="120" alt="CloakBrowser Hub" />

# CloakBrowser Hub

**An anti-detect browser manager — profiles, fingerprints, folders and proxies.**

Built on [CloakBrowser](https://www.npmjs.com/package/cloakbrowser). .NET 8 + Avalonia.
Runs on Windows, Linux and macOS from a single codebase.

[Download](#download) · [Build from source](#build-from-source) · [What works today](#what-works-today) · [How it works](#how-it-works)

</div>

---

## What this is

Every browser profile here is a separate identity: its own fingerprint, its own
cookie jar, its own proxy, its own disk directory. Nothing is shared between them,
which is the entire point — two profiles that leak a common trait are two profiles a
site can link back to one person.

The Hub is the manager around that idea. It stores the profiles, generates
fingerprints that are internally coherent, groups them into folders, and launches
sessions.

> **Status: the desktop app is a work in progress.**
> Profile management is complete and usable. Session launching is not yet wired to a
> real browser. Read [What works today](#what-works-today) before downloading — it is
> an honest table, not a feature list.

---

## Download

Self-contained builds. No .NET runtime, no Node, no installer — one file.

| Platform | File | Size |
|---|---|---|
| Windows x64 | `CloakBrowserHub-v1.0.0-win-x64.zip` | ~42 MB |
| Linux x64 | `CloakBrowserHub-v1.0.0-linux-x64.tar.gz` | ~40 MB |

Grab them from the [Releases page](../../releases).

**Windows** — unzip, run `CloakBrowserHub.exe`. SmartScreen will warn about an
unrecognised publisher because the binary is unsigned; *More info → Run anyway*.

**Linux** — needs a desktop session (X11 or Wayland):

```bash
tar -xzf CloakBrowserHub-v1.0.0-linux-x64.tar.gz
chmod +x CloakBrowserHub
./CloakBrowserHub
```

**macOS** — not currently published as a binary. The code targets it and the icon
pipeline emits a proper `.icns`, but an unsigned, unnotarised `.app` is refused by
Gatekeeper in a way that looks like a corrupt download, so shipping one would waste
your time. Build it yourself — see below.

---

## What works today

The desktop app has five sections. Three of them are still placeholders, and they say
so on screen rather than showing an empty pane.

| Area | State | Notes |
|---|---|---|
| **Profiles list** | ✅ Working | Search, sort, duplicate, delete, live counts |
| **Folders** | ✅ Working | Create, inline rename, delete, move profiles between them |
| **Profile editor** | ✅ Working | 7 tabs — General, Fingerprint, Proxy, Locale & Geo, Behaviour, Startup, Advanced |
| **Fingerprint generation** | ✅ Working | Coherent per-platform draws, one-click re-roll |
| **Settings** | ✅ Working | Session limit, data directory, release channel, UI zoom, automation port |
| **Storage & migration** | ✅ Working | Atomic writes, corrupt-file quarantine, schema 1→4 migration |
| **Launching a browser** | ⚠️ **Not wired** | Everything up to the launch runs — limit check, badge assets, argument building — but no browser starts. `IBrowserLauncher` has no implementation yet. |
| **Proxy library** | ⚠️ Placeholder | Per-profile proxies work in the editor; the shared library, parser and checker are not ported |
| **Import** | ⚠️ Placeholder | Chrome/Firefox/anti-detect importers not ported |
| **License** | ⚠️ Placeholder | Key parsing exists in Core; activation UI not ported |
| **Cookie import/export** | ❌ Not ported | |
| **Automation HTTP API** | ❌ Not ported | The port setting exists; the server does not |

If you need the missing pieces today, the previous Electron implementation still has
them — see [History](#history).

---

## Features in detail

### Coherent fingerprints, not random ones

A fingerprint is only convincing when its parts co-occur in the real world. A machine
claiming macOS with an `ANGLE (NVIDIA, ... D3D11)` renderer describes a computer that
cannot exist, and that single contradiction is *more* identifying than the honest
values would have been.

So the value pools are keyed by platform and drawn per platform, never mixed:

- **GPU vendor and renderer are stored as pairs**, so "Apple Inc." can never be
  emitted with a Radeon renderer.
- **Screen resolutions are per-OS** — Apple has never shipped a 1366×768 panel.
- **Locale and timezone are offered together** — `de-DE` in `Asia/Tokyo` is a
  contradiction a site can test for in one line of JavaScript.
- **`deviceMemory` is powers of two only**, because that is the entire set of values
  the API is specified to report.

The distributions are also deliberately lumpy rather than uniform. 1920×1080 appears
three times in the Windows screen pool because it is genuinely modal. Sampling
uniformly from a set of plausible values produces a population that is itself
implausible — a profile holding a 1-in-9 screen size stands out more, not less.

### Folders

Grouping, like Dolphin Anty: a sidebar with live counts, inline rename on Enter,
right-click for rename/delete, and a **Move to** submenu on every row.

**Deleting a folder never deletes the profiles inside it.** They move to the root.
Deleting a container in a file manager takes its contents, but a profile represents
real work — an aged identity with cookies and history — and losing several to one
misclick on a grouping label would be indefensible.

### Storage that does not lose your work

- **Atomic writes** — write to a temp file, then rename. A crash mid-save leaves the
  previous file intact rather than a truncated one.
- **Corrupt files are quarantined, not overwritten.** If `profiles.json` cannot be
  parsed it is moved aside and the app opens empty with a message naming the file.
  An empty list is indistinguishable from the app having thrown your work away, so
  it says where the bytes went.
- **One unreadable profile does not hide the others.** A single bad entry is skipped
  and reported; the rest load.
- **Version-gated migration.** Backfill only runs for profiles below the version that
  introduced a field, so an explicitly cleared value is never resurrected.

### Per-instance taskbar badges

Each running session gets a numbered icon so twelve open windows are still tellable
apart — a real `.ico` on Windows, `.icns` on macOS, and X11 window icons on Linux.

---

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else.

```bash
git clone https://github.com/evelaa123/Cloakbrowser-Hub.git
cd Cloakbrowser-Hub/dotnet

dotnet build                # 0 warnings expected — warnings are errors here
dotnet test                 # 342 tests
dotnet run --project src/CloakHub.App
```

### Publishing a single-file binary

```bash
# Windows
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o artifacts/win-x64

# Linux
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o artifacts/linux-x64

# macOS (Apple Silicon; use osx-x64 for Intel)
dotnet publish src/CloakHub.App/CloakHub.App.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/osx-arm64
```

### Diagnostics CLI

`CloakHub.Doctor` prints what the app would do without launching anything — the exact
browser arguments a profile produces, host detection, badge planning, network checks.

```bash
dotnet run --project src/CloakHub.Doctor -- --help
```

---

## How it works

```
dotnet/
├── src/
│   ├── CloakHub.Core/          # No UI. All logic, fully testable.
│   │   ├── Model/              # Profile, Fingerprint, Defaults (pools + factory)
│   │   ├── Storage/            # ProfileStore, JsonStore, ProfileMigration
│   │   ├── Launch/             # FingerprintArgs, PrivacyArgs, SessionManager
│   │   ├── Branding/           # Per-instance badge icons (.ico/.icns/X11)
│   │   ├── Licensing/          # Key parsing, session limits
│   │   ├── Network/            # MAC address planning
│   │   └── Platform/           # Host OS detection
│   ├── CloakHub.App/           # Avalonia UI — views and view models only
│   └── CloakHub.Doctor/        # Diagnostics CLI
└── tests/
    └── CloakHub.Core.Tests/    # 342 xUnit tests
```

**Core holds every decision; the UI holds none.** The UI project contains no
fingerprint logic, no file format knowledge and no argument building. That is why the
diagnostics CLI can produce byte-identical launch arguments to the app — they call the
same code — and why the rules are testable without a display server.

Two conventions worth knowing before contributing:

- **Warnings are errors** (`TreatWarningsAsErrors`). The build has zero warnings and
  should stay that way.
- **Compiled bindings are on by default.** Every view declares `x:DataType`, so a
  binding to a property that does not exist is a build error rather than a silently
  empty field at runtime.

### Where your data lives

| OS | Path |
|---|---|
| Windows | `%APPDATA%\CloakBrowserHub\` |
| macOS | `~/Library/Application Support/CloakBrowserHub/` |
| Linux | `~/.config/CloakBrowserHub/` |

`profiles.json` holds the profiles and folders; `settings.json` holds preferences.
Profile browser data goes in a `profiles/` subdirectory, relocatable in Settings.

---

## History

This project began as an Electron + Preact application. It is being rewritten in
.NET 8 + Avalonia, and **the .NET implementation is now the basis of this
repository** — a single toolchain, a single language, and a ~40 MB self-contained
binary instead of a bundled Chromium runtime.

The rewrite is not finished. The Electron sources remain in `src/` and `tests/`
precisely because they still implement things the .NET app does not: cookie
import/export, the proxy library and checker, browser importers, the automation HTTP
API, and — most importantly — actually launching a browser. Deleting them now would
destroy the only working implementation of those features and the reference the port
is being written against.

They will be removed once the table in [What works today](#what-works-today) has no
gaps left.

---

## Limitations worth stating plainly

- **MAC address and device name do not affect your browser fingerprint.** No web API
  exposes them — not `navigator`, not WebRTC, not WebGL. They change what the *local
  network* sees. They are modelled because other tools offer them and users
  reasonably ask, and the UI states the limitation rather than implying a benefit.
- **Per-surface noise currently collapses to one flag.** The CloakBrowser binary
  exposes a single `--fingerprint-noise` switch covering canvas, WebGL, audio and
  client rects together. The four values are stored separately so the UI can already
  offer the control users expect and a future binary needs no migration — but today
  any surface asking for noise enables it for all of them.
- **No fingerprint is undetectable.** A sufficiently determined site can detect that
  values are being spoofed at all. The goal here is to stop *correlation* between
  your profiles, which is a much more achievable and more useful property.

---

## License

MIT.
