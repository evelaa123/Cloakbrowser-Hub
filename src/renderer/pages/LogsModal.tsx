/**
 * Session log viewer.
 *
 * Logs arrive both as history (fetched once from the main process) and as a live
 * stream (the global log event, already collected in the hub state). They are
 * merged here so opening the modal mid-session shows everything, not just what
 * happened after the modal opened.
 */

import type { JSX } from 'preact';
import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import type { ProfileRow, SessionLogEntry } from '../../shared/types';
import { useHub } from '../state';
import { Empty, Modal, clockTime } from '../components/ui';

export function LogsModal(props: { profile: ProfileRow; onClose: () => void }): JSX.Element {
  const hub = useHub();
  const [history, setHistory] = useState<SessionLogEntry[]>([]);
  const [follow, setFollow] = useState(true);
  const viewRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    void window.hub.sessions
      .logs(props.profile.id)
      .then(setHistory)
      .catch(() => setHistory([]));
  }, [props.profile.id]);

  // The live buffer and the fetched history overlap, so entries are keyed by
  // timestamp + message to avoid printing the same line twice.
  const lines = useMemo(() => {
    const live = hub.logs[props.profile.id] ?? [];
    const seen = new Set<string>();
    const out: SessionLogEntry[] = [];
    for (const entry of [...history, ...live]) {
      const key = `${entry.at}|${entry.message}`;
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(entry);
    }
    return out.sort((a, b) => a.at - b.at);
  }, [history, hub.logs, props.profile.id]);

  useEffect(() => {
    if (follow && viewRef.current) viewRef.current.scrollTop = viewRef.current.scrollHeight;
  }, [lines.length, follow]);

  return (
    <Modal
      title={`Session log — ${props.profile.name}`}
      subtitle={`${lines.length} line(s)`}
      wide
      onClose={props.onClose}
      footer={
        <>
          <label class="check left" style={{ alignItems: 'center' }}>
            <input
              type="checkbox"
              checked={follow}
              onChange={(e) => setFollow((e.currentTarget as HTMLInputElement).checked)}
            />
            <span class="check-text">
              <strong style={{ fontWeight: 500 }}>Follow output</strong>
            </span>
          </label>
          <button
            class="btn"
            disabled={!lines.length}
            onClick={() => {
              void navigator.clipboard
                .writeText(lines.map((l) => `${clockTime(l.at)} [${l.level}] ${l.message}`).join('\n'))
                .catch(() => undefined);
            }}
          >
            Copy
          </button>
          <button class="btn" onClick={props.onClose}>
            Close
          </button>
        </>
      }
    >
      {!lines.length ? (
        <Empty
          icon="≡"
          title="No log yet"
          text="Start this profile and the launch details, cookie injection results and any errors will appear here."
        />
      ) : (
        <div class="log-view" ref={viewRef}>
          {lines.map((l, i) => (
            <div class={`log-line ${l.level}`} key={`${l.at}-${i}`}>
              <span class="at">{clockTime(l.at)}</span>
              <span class="msg">{l.message}</span>
            </div>
          ))}
        </div>
      )}
    </Modal>
  );
}
