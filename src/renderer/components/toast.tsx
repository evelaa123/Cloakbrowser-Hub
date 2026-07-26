/**
 * Toast notifications.
 *
 * Every IPC call in this app can fail for reasons the user needs to see (bad
 * license key, dead proxy, missing shared library on Linux), so a single global
 * channel for those messages beats scattering inline error banners everywhere.
 */

import type { JSX } from 'preact';
import { createContext } from 'preact';
import { useCallback, useContext, useState } from 'preact/hooks';

export type ToastTone = 'ok' | 'err' | 'warn' | 'info';

interface Toast {
  id: number;
  tone: ToastTone;
  message: string;
}

interface ToastApi {
  push: (tone: ToastTone, message: string) => void;
  ok: (message: string) => void;
  err: (message: unknown) => void;
  warn: (message: string) => void;
  info: (message: string) => void;
  /** Run an async action, surfacing failures as an error toast. */
  run: <T>(fn: () => Promise<T>, success?: string) => Promise<T | undefined>;
}

const noop: ToastApi = {
  push: () => {},
  ok: () => {},
  err: () => {},
  warn: () => {},
  info: () => {},
  run: async () => undefined,
};

const ToastCtx = createContext<ToastApi>(noop);

export function useToast(): ToastApi {
  return useContext(ToastCtx);
}

const ICON: Record<ToastTone, string> = { ok: '✓', err: '✕', warn: '!', info: 'i' };
/** Errors stay long enough to read a stack-free sentence; successes vanish fast. */
const TTL: Record<ToastTone, number> = { ok: 2600, info: 3200, warn: 5200, err: 8000 };

export function ToastProvider(props: { children: preact.ComponentChildren }): JSX.Element {
  const [toasts, setToasts] = useState<Toast[]>([]);

  const remove = useCallback((id: number) => {
    setToasts((list) => list.filter((t) => t.id !== id));
  }, []);

  const push = useCallback(
    (tone: ToastTone, message: string) => {
      const id = Date.now() + Math.random();
      // Cap the stack: a loop of failures must not cover the whole window.
      setToasts((list) => [...list.slice(-4), { id, tone, message }]);
      window.setTimeout(() => remove(id), TTL[tone]);
    },
    [remove],
  );

  const api: ToastApi = {
    push,
    ok: (m) => push('ok', m),
    warn: (m) => push('warn', m),
    info: (m) => push('info', m),
    err: (e) => push('err', e instanceof Error ? e.message : String(e)),
    run: async (fn, success) => {
      try {
        const out = await fn();
        if (success) push('ok', success);
        return out;
      } catch (e) {
        push('err', e instanceof Error ? e.message : String(e));
        return undefined;
      }
    },
  };

  return (
    <ToastCtx.Provider value={api}>
      {props.children}
      <div class="toasts">
        {toasts.map((t) => (
          <div key={t.id} class={`toast ${t.tone}`}>
            <span aria-hidden="true">{ICON[t.tone]}</span>
            <span class="msg">{t.message}</span>
            <button class="x" onClick={() => remove(t.id)} aria-label="Dismiss">
              ✕
            </button>
          </div>
        ))}
      </div>
    </ToastCtx.Provider>
  );
}
