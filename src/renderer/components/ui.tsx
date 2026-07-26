/**
 * Small presentational primitives shared across pages.
 *
 * These stay deliberately dumb — no data fetching, no IPC — so pages remain the
 * only place where state lives and the components are trivially reusable.
 */

import type { ComponentChildren, JSX } from 'preact';
import { useEffect, useRef } from 'preact/hooks';
import type { ProfileStatus } from '../../shared/types';

// ---------------------------------------------------------------------------
// Field wrappers
// ---------------------------------------------------------------------------

export function Field(props: {
  label: string;
  hint?: string;
  children: ComponentChildren;
}): JSX.Element {
  return (
    <div class="field">
      <label>{props.label}</label>
      {props.children}
      {props.hint ? <div class="hint">{props.hint}</div> : null}
    </div>
  );
}

export function Check(props: {
  checked: boolean;
  onChange: (v: boolean) => void;
  label: string;
  hint?: string;
  disabled?: boolean;
}): JSX.Element {
  return (
    <label class="check">
      <input
        type="checkbox"
        checked={props.checked}
        disabled={props.disabled}
        onChange={(e) => props.onChange((e.currentTarget as HTMLInputElement).checked)}
      />
      <span class="check-text">
        <strong>{props.label}</strong>
        {props.hint ? <span>{props.hint}</span> : null}
      </span>
    </label>
  );
}

export function Card(props: {
  title?: string;
  desc?: string;
  children: ComponentChildren;
  actions?: ComponentChildren;
}): JSX.Element {
  return (
    <div class="card">
      {props.title ? (
        <div class="row" style={{ marginBottom: props.desc ? 0 : 12 }}>
          <div>
            <h2 class="card-title">{props.title}</h2>
            {props.desc ? <p class="card-desc">{props.desc}</p> : null}
          </div>
          {props.actions ? <div class="right row">{props.actions}</div> : null}
        </div>
      ) : null}
      {props.children}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Status / badges
// ---------------------------------------------------------------------------

const STATUS_LABEL: Record<ProfileStatus, string> = {
  idle: 'Idle',
  starting: 'Starting',
  running: 'Running',
  stopping: 'Stopping',
  error: 'Error',
};

export function StatusBadge(props: { status: ProfileStatus; message?: string }): JSX.Element {
  const tone =
    props.status === 'running'
      ? 'ok'
      : props.status === 'error'
        ? 'err'
        : props.status === 'starting' || props.status === 'stopping'
          ? 'warn'
          : '';
  const busy = props.status === 'starting' || props.status === 'stopping';
  return (
    <span class={`badge ${tone}`} title={props.message ?? STATUS_LABEL[props.status]}>
      {props.status === 'running' ? <span class="pulse" /> : null}
      {busy ? <span class="spinner" /> : null}
      {STATUS_LABEL[props.status]}
    </span>
  );
}

export function Spinner(): JSX.Element {
  return <span class="spinner" />;
}

export function Empty(props: {
  icon: string;
  title: string;
  text: string;
  action?: ComponentChildren;
}): JSX.Element {
  return (
    <div class="empty">
      <div class="empty-icon">{props.icon}</div>
      <h3>{props.title}</h3>
      <p>{props.text}</p>
      {props.action}
    </div>
  );
}

export function Callout(props: {
  tone?: 'warn' | 'err' | 'ok';
  icon?: string;
  children: ComponentChildren;
}): JSX.Element {
  return (
    <div class={`callout ${props.tone ?? ''}`}>
      {props.icon ? <span>{props.icon}</span> : null}
      <div>{props.children}</div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Modal
// ---------------------------------------------------------------------------

export function Modal(props: {
  title: string;
  subtitle?: string;
  wide?: boolean;
  onClose: () => void;
  children: ComponentChildren;
  footer?: ComponentChildren;
}): JSX.Element {
  const overlay = useRef<HTMLDivElement>(null);

  // Escape closes the dialog: expected of every desktop dialog, and the only
  // escape hatch if a footer button is ever off-screen.
  useEffect(() => {
    const onKey = (e: KeyboardEvent): void => {
      if (e.key === 'Escape') props.onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [props.onClose]);

  return (
    <div
      class="overlay"
      ref={overlay}
      onMouseDown={(e) => {
        if (e.target === overlay.current) props.onClose();
      }}
    >
      <div class={`modal ${props.wide ? 'wide' : ''}`}>
        <div class="modal-head">
          <div>
            <h2>{props.title}</h2>
            {props.subtitle ? <div class="sub">{props.subtitle}</div> : null}
          </div>
          <button class="close-x" onClick={props.onClose} title="Close" aria-label="Close">
            ✕
          </button>
        </div>
        <div class="modal-body">{props.children}</div>
        {props.footer ? <div class="modal-foot">{props.footer}</div> : null}
      </div>
    </div>
  );
}

export function ConfirmModal(props: {
  title: string;
  message: string;
  confirmLabel?: string;
  danger?: boolean;
  onConfirm: () => void;
  onClose: () => void;
  extra?: ComponentChildren;
}): JSX.Element {
  return (
    <Modal
      title={props.title}
      onClose={props.onClose}
      footer={
        <>
          <button class="btn" onClick={props.onClose}>
            Cancel
          </button>
          <button
            class={`btn ${props.danger ? 'danger' : 'primary'}`}
            onClick={() => {
              props.onConfirm();
              props.onClose();
            }}
          >
            {props.confirmLabel ?? 'Confirm'}
          </button>
        </>
      }
    >
      <p style={{ margin: '0 0 12px', lineHeight: 1.6 }}>{props.message}</p>
      {props.extra}
    </Modal>
  );
}

// ---------------------------------------------------------------------------
// Tabs
// ---------------------------------------------------------------------------

export function Tabs<T extends string>(props: {
  tabs: ReadonlyArray<{ id: T; label: string }>;
  active: T;
  onChange: (id: T) => void;
}): JSX.Element {
  return (
    <div class="tabs">
      {props.tabs.map((t) => (
        <button
          key={t.id}
          class={`tab ${props.active === t.id ? 'active' : ''}`}
          onClick={() => props.onChange(t.id)}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

// ---------------------------------------------------------------------------
// Formatting helpers
// ---------------------------------------------------------------------------

/** Relative time, e.g. "3 min ago". Absolute dates past a week. */
export function timeAgo(ts?: number): string {
  if (!ts) return 'never';
  const s = Math.round((Date.now() - ts) / 1000);
  if (s < 45) return 'just now';
  if (s < 90) return '1 min ago';
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const h = Math.round(m / 60);
  if (h < 24) return `${h} h ago`;
  const d = Math.round(h / 24);
  if (d < 7) return `${d} d ago`;
  return new Date(ts).toLocaleDateString();
}

export function clockTime(ts: number): string {
  return new Date(ts).toLocaleTimeString([], { hour12: false });
}
