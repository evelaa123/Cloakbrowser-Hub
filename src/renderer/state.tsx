/**
 * Global renderer state.
 *
 * Profiles, license, binary and settings are needed by several pages at once
 * (the sidebar shows a running count, the profiles page shows a license warning,
 * the settings page edits the same object), so they live in one context rather
 * than being re-fetched per page. Everything else is page-local.
 */

import type { ComponentChildren, JSX } from 'preact';
import { createContext } from 'preact';
import { useCallback, useContext, useEffect, useMemo, useState } from 'preact/hooks';
import type { AppSettings, BinaryState, ProfileRow, SessionLogEntry } from '../shared/types';
import type { AppInfo, LicenseView } from '../preload/index';
import { useToast } from './components/toast';

interface HubState {
  profiles: ProfileRow[];
  license?: LicenseView;
  binary?: BinaryState;
  settings?: AppSettings;
  info?: AppInfo;
  /** Session logs, keyed by profile id, kept live via the log event. */
  logs: Record<string, SessionLogEntry[]>;
  loading: boolean;
  refreshProfiles: () => Promise<void>;
  refreshLicense: (refresh?: boolean) => Promise<void>;
  refreshBinary: () => Promise<void>;
  saveSettings: (patch: Partial<AppSettings>) => Promise<void>;
  runningCount: number;
}

const HubCtx = createContext<HubState | null>(null);

export function useHub(): HubState {
  const ctx = useContext(HubCtx);
  if (!ctx) throw new Error('useHub must be used inside <HubProvider>');
  return ctx;
}

const MAX_LOG_LINES = 400;

export function HubProvider(props: { children: ComponentChildren }): JSX.Element {
  const toast = useToast();
  const [profiles, setProfiles] = useState<ProfileRow[]>([]);
  const [license, setLicense] = useState<LicenseView>();
  const [binary, setBinary] = useState<BinaryState>();
  const [settings, setSettings] = useState<AppSettings>();
  const [info, setInfo] = useState<AppInfo>();
  const [logs, setLogs] = useState<Record<string, SessionLogEntry[]>>({});
  const [loading, setLoading] = useState(true);

  const refreshProfiles = useCallback(async () => {
    try {
      setProfiles(await window.hub.profiles.list());
    } catch (e) {
      toast.err(e);
    }
  }, [toast]);

  const refreshLicense = useCallback(async (refresh = true) => {
    try {
      setLicense(await window.hub.license.state(refresh));
    } catch (e) {
      // A license lookup needs the network; a failure here must not block the UI,
      // so it is reported in the license page rather than as a toast on startup.
      setLicense({
        tier: 'none',
        valid: false,
        localSessions: 0,
        seatHint: null,
        error: e instanceof Error ? e.message : String(e),
      });
    }
  }, []);

  const refreshBinary = useCallback(async () => {
    try {
      setBinary(await window.hub.binary.state());
    } catch (e) {
      setBinary({ installed: false, error: e instanceof Error ? e.message : String(e) });
    }
  }, []);

  const saveSettings = useCallback(
    async (patch: Partial<AppSettings>) => {
      const next = await window.hub.settings.update(patch);
      setSettings(next);
      document.documentElement.dataset['theme'] = next.theme;
    },
    [],
  );

  // Initial load. The license check is intentionally not awaited alongside the
  // rest: it hits the network and would otherwise delay first paint.
  useEffect(() => {
    void (async () => {
      try {
        const [p, s, i, b] = await Promise.all([
          window.hub.profiles.list(),
          window.hub.settings.get(),
          window.hub.app.info(),
          window.hub.binary.state().catch(() => ({ installed: false }) as BinaryState),
        ]);
        setProfiles(p);
        setSettings(s);
        setInfo(i);
        setBinary(b);
        document.documentElement.dataset['theme'] = s.theme;
      } catch (e) {
        toast.err(e);
      } finally {
        setLoading(false);
      }
      void refreshLicense(true);
    })();
    // Run once on mount.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Live events from the main process.
  useEffect(() => {
    const offProfiles = window.hub.events.onProfilesChanged((rows) => setProfiles(rows));

    const offSessions = window.hub.events.onSessions(() => {
      // A session starting or stopping changes the seat count the license page
      // shows, so the cached license view is refreshed without a network call.
      void window.hub.license.state(false).then(setLicense).catch(() => undefined);
    });

    const offLog = window.hub.events.onLog((entry) => {
      setLogs((prev) => {
        const list = [...(prev[entry.profileId] ?? []), entry];
        return { ...prev, [entry.profileId]: list.slice(-MAX_LOG_LINES) };
      });
    });

    return () => {
      offProfiles();
      offSessions();
      offLog();
    };
  }, []);

  const runningCount = useMemo(
    () => profiles.filter((p) => p.status === 'running' || p.status === 'starting').length,
    [profiles],
  );

  const value: HubState = {
    profiles,
    license,
    binary,
    settings,
    info,
    logs,
    loading,
    refreshProfiles,
    refreshLicense,
    refreshBinary,
    saveSettings,
    runningCount,
  };

  return <HubCtx.Provider value={value}>{props.children}</HubCtx.Provider>;
}
