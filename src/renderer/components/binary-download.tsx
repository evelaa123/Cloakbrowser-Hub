/**
 * Global "downloading the browser" modal.
 *
 * Lives in the app shell rather than on a page because the download can now be
 * triggered from three places — the License button, activating a key, and
 * launching a profile that has no binary yet. A page-local spinner would leave
 * the two implicit triggers looking like the app had frozen: this is a
 * several-hundred-megabyte fetch that takes minutes on a first run.
 *
 * The wrapper's `ensureBinary()` reports progress to its own stdout and gives us
 * no byte counts, so this is deliberately indeterminate. Showing a fake
 * percentage would be worse than showing none.
 */

import type { JSX } from 'preact';
import { useEffect, useState } from 'preact/hooks';
import { Spinner } from './ui';

export function BinaryDownloadModal(): JSX.Element | null {
  const [downloading, setDownloading] = useState(false);
  const [elapsed, setElapsed] = useState(0);

  useEffect(() => {
    return window.hub.events.onBinaryProgress((p) => {
      setDownloading(p.state === 'downloading');
      if (p.state === 'downloading') setElapsed(0);
    });
  }, []);

  // A visible timer is the only honest progress signal available: without it an
  // indeterminate spinner gives the user no way to tell a slow download from a
  // hung one.
  useEffect(() => {
    if (!downloading) return;
    const t = setInterval(() => setElapsed((s) => s + 1), 1000);
    return () => clearInterval(t);
  }, [downloading]);

  if (!downloading) return null;

  const mins = Math.floor(elapsed / 60);
  const secs = elapsed % 60;

  return (
    // No onClose: this must not be dismissable. The launch that triggered it is
    // awaiting the download, so letting the user close the dialog would leave
    // the app looking idle while work continues in the background.
    <div class="overlay" role="dialog" aria-modal="true" aria-busy="true">
      <div class="modal">
        <div class="modal-head">
          <div>
            <h2>Downloading stealth browser</h2>
            <div class="sub">First run only — this is cached for every later launch.</div>
          </div>
        </div>
        <div class="modal-body">
          <div class="row" style={{ gap: 14, alignItems: 'center' }}>
            <Spinner />
            <div>
              <div>Fetching the patched Chromium build…</div>
              <div class="dim" style={{ marginTop: 4 }}>
                {mins > 0 ? `${mins}m ${secs}s elapsed` : `${secs}s elapsed`}
              </div>
            </div>
          </div>
          <p class="dim" style={{ marginTop: 16, marginBottom: 0 }}>
            The download is a few hundred megabytes and can take several minutes on a
            slow connection. Keep the app open — closing it now will restart the
            download next time.
          </p>
        </div>
      </div>
    </div>
  );
}
