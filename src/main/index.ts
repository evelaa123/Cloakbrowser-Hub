/**
 * Main process entry point.
 *
 * Responsibilities, in order: enforce a single instance (two copies would fight
 * over the same profiles.json and the same Chromium user-data dirs), create the
 * window, register the IPC surface, and make sure no orphan browser is left
 * running when the app quits.
 */

import fs from 'node:fs';
import path from 'node:path';
import { BrowserWindow, app, shell } from 'electron';
import { automation, registerIpcHandlers, setZoomListener } from './ipc/handlers';
import { Sessions } from './browser/session-manager';
import { Settings } from './services/repos';
import { DEFAULT_ZOOM, snapZoom, stepZoom } from '../shared/ui-zoom';

const isDev = !app.isPackaged;

/** Set once the real quit sequence has run, so the second pass is allowed. */
let sessionsClosed = false;
let quitting = false;

/**
 * Window icon path, or undefined to let the platform decide.
 *
 * When *packaged*, Windows takes the icon from the executable resources and
 * macOS from the .app bundle, so only Linux needs an explicit path there.
 *
 * In development none of that applies: the running executable is Electron's own
 * `electron.exe` / `Electron.app`, so its resources are Electron's default atom
 * icon — which is exactly the reported bug ("npm start shows the default
 * Electron icon in the taskbar"). Passing the path explicitly is the only way to
 * override it, on every platform.
 *
 * Resolved at runtime because the file sits beside the bundle when packaged and
 * two levels up in dev, and a missing icon must never stop the window opening.
 */
function windowIcon(): string | undefined {
  // Packaged, non-Linux: the platform already has the right icon; overriding it
  // with a PNG would actually lose the multi-resolution .ico/.icns.
  if (app.isPackaged && process.platform !== 'linux') return undefined;

  const candidates = [
    path.join(process.resourcesPath ?? '', 'icon.png'),
    path.join(__dirname, '../../build/icons/512x512.png'),
    path.join(app.getAppPath(), 'build/icons/512x512.png'),
    path.join(app.getAppPath(), 'build/icon.png'),
  ];
  return candidates.find((p) => p && fs.existsSync(p));
}

/**
 * Give the dev build its own taskbar/dock identity.
 *
 * Two separate platform quirks, both invisible in a packaged build:
 *
 *  - Windows groups taskbar buttons by AppUserModelID and reads the icon from
 *    the process that owns it. Without an explicit ID, a dev run inherits
 *    Electron's, so the window groups under "Electron" with Electron's icon even
 *    after `BrowserWindow({ icon })` is set.
 *  - macOS takes the dock icon from the .app bundle, which in dev is
 *    Electron.app. `app.dock.setIcon` is the only override.
 */
function applyDevAppIdentity(): void {
  if (process.platform === 'win32') {
    // Harmless when packaged too, and keeps dev and prod grouping consistent.
    app.setAppUserModelId('com.cloakbrowser.hub');
  }
  if (app.isPackaged || process.platform !== 'darwin') return;

  const icon = windowIcon();
  if (!icon) return;
  try {
    // Typed as possibly-undefined because `app.dock` is macOS-only.
    app.dock?.setIcon(icon);
  } catch {
    // A wrong-format or unreadable icon must not stop the app from starting.
  }
}

function createWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 960,
    minHeight: 600,
    show: false,
    autoHideMenuBar: true,
    backgroundColor: '#0f1115',
    title: 'CloakBrowser Hub',
    ...(windowIcon() ? { icon: windowIcon() } : {}),
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false, // the preload needs Node built-ins via the bridge modules
      spellcheck: false,
    },
  });

  // Showing only once the first frame is painted avoids the white flash that
  // makes an Electron app feel cheap.
  win.once('ready-to-show', () => win.show());

  // Anything that tries to open a new window (target=_blank, window.open) goes
  // to the user's real browser instead of an unmanaged Electron window.
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http://') || url.startsWith('https://')) void shell.openExternal(url);
    return { action: 'deny' };
  });

  // Block in-page navigation away from the app shell entirely.
  win.webContents.on('will-navigate', (event, url) => {
    const devServer = process.env['ELECTRON_RENDERER_URL'];
    if (devServer && url.startsWith(devServer)) return;
    event.preventDefault();
    if (url.startsWith('http://') || url.startsWith('https://')) void shell.openExternal(url);
  });

  // Zoom has to be (re)applied after every load, not just once: Chromium resets
  // the zoom factor when the renderer navigates, so a dev-server reload would
  // silently snap the UI back to 100%.
  win.webContents.on('did-finish-load', () => {
    applyZoom(win, Settings.get().uiZoom);
  });

  // Ctrl/Cmd +, -, 0 — the shortcuts every user already expects for this, bound
  // here rather than in the renderer because the zoom factor lives on
  // webContents and the renderer has no access to it under contextIsolation.
  win.webContents.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown' || !(input.control || input.meta)) return;
    const key = input.key;
    let next: number | undefined;
    if (key === '=' || key === '+') next = stepZoom(Settings.get().uiZoom, 1);
    else if (key === '-' || key === '_') next = stepZoom(Settings.get().uiZoom, -1);
    else if (key === '0') next = DEFAULT_ZOOM;
    if (next === undefined) return;

    event.preventDefault();
    // Persisted, so the choice survives a restart — a zoom level you have to
    // reset on every launch is not a setting, it is a nuisance.
    Settings.update({ uiZoom: next });
    applyZoom(win, next);
  });

  const devServerUrl = process.env['ELECTRON_RENDERER_URL'];
  if (isDev && devServerUrl) {
    void win.loadURL(devServerUrl);
  } else {
    void win.loadFile(path.join(__dirname, '../renderer/index.html'));
  }

  return win;
}

/**
 * Apply a zoom factor to a window.
 *
 * `setZoomFactor` scales the whole layout — padding, control heights, borders —
 * not just text, which is the point: the complaint was that the interface felt
 * small, and enlarging only the type inside fixed-size boxes makes it cramped
 * instead of bigger.
 */
function applyZoom(win: BrowserWindow, zoom: number | undefined): void {
  if (win.isDestroyed()) return;
  try {
    win.webContents.setZoomFactor(snapZoom(zoom));
  } catch {
    // A destroyed or not-yet-attached webContents must not crash the app.
  }
}

/** Push a new zoom factor to every open window. Called by the settings handler. */
export function broadcastZoom(zoom: number | undefined): void {
  for (const win of BrowserWindow.getAllWindows()) applyZoom(win, zoom);
}

function focusExistingWindow(): void {
  const win = BrowserWindow.getAllWindows()[0];
  if (!win) {
    createWindow();
    return;
  }
  if (win.isMinimized()) win.restore();
  win.focus();
}

// A second instance would corrupt shared state, so hand the focus to the first
// one and exit immediately.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', focusExistingWindow);

  // Chromium in the Electron shell has nothing to do with the stealth browser;
  // disabling the shared GPU cache avoids two processes racing on it.
  app.commandLine.appendSwitch('disable-gpu-shader-disk-cache');

  void app.whenReady().then(() => {
    // Before the first window: the Windows AppUserModelID has to be set for the
    // taskbar button to pick up our icon rather than Electron's.
    applyDevAppIdentity();
    registerIpcHandlers();
    setZoomListener(broadcastZoom);
    createWindow();

    // Restore the automation API if the user had it enabled. Deliberately not
    // awaited and never fatal: a port taken by another program must not stop the
    // app from opening, so the failure is logged and the UI shows it as not
    // listening.
    const auto = Settings.get().automation;
    if (auto?.enabled) {
      void automation
        .start(auto)
        .catch((e) => console.error('[automation] could not start:', (e as Error)?.message ?? e));
    }

    app.on('activate', () => {
      if (BrowserWindow.getAllWindows().length === 0) createWindow();
    });
  });

  app.on('window-all-closed', () => {
    // macOS convention is to stay alive with no windows, but this app manages
    // running browser sessions, so keeping it alive is also the safer choice.
    if (process.platform !== 'darwin') app.quit();
  });

  /**
   * Closing browsers takes time, and `before-quit` is synchronous, so the first
   * pass is cancelled, the sessions are closed, then quit is re-issued.
   */
  app.on('before-quit', (event) => {
    if (sessionsClosed || quitting) return;

    // Stop listening immediately: a request that arrives mid-teardown could
    // start a browser the quit path has already walked past.
    void automation.stop().catch(() => undefined);

    const running = Sessions.runningCount();
    if (running === 0) {
      sessionsClosed = true;
      return;
    }

    if (!Settings.get().closeSessionsOnQuit) {
      // The user asked to keep sessions open; nothing to wait for. The browsers
      // are separate OS processes and survive on their own.
      sessionsClosed = true;
      return;
    }

    event.preventDefault();
    quitting = true;
    void Sessions.stopAll()
      .catch((e) => console.error('[quit] failed to close sessions:', e))
      .finally(() => {
        sessionsClosed = true;
        app.quit();
      });
  });
}
