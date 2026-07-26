/**
 * Set Google as the default search engine in a fresh profile.
 *
 * Background, from the binary's maintainer: the Chromium build is de-Googled and
 * ships with *no* prepopulated search engine at all. That is why typing a term in
 * the address bar of a new profile does nothing useful. Three of the four obvious
 * fixes do not work, and it is worth recording why so nobody re-tries them:
 *
 *  - Editing `Default/Preferences` — rejected. Chromium protects search-engine
 *    prefs with an HMAC ("protected prefs" / preference MAC). A hand-written
 *    value fails validation and is silently reverted on the next start.
 *  - Writing the `Web Data` keywords table — overwritten. The prepopulated
 *    keyword set is re-applied to that table on every startup.
 *  - A CLI flag or `initial_preferences` hook — does not exist in this build.
 *
 * What does work is driving the settings UI once: `chrome://settings/searchEngines`
 * → add an engine → make it default, in a *persistent* profile, then close the
 * browser normally so Chromium flushes prefs with a valid MAC. After that the
 * profile keeps the engine like any hand-configured Chrome.
 *
 * Two details that are easy to get wrong:
 *
 *  - The settings UI is localised. With a system locale of, say, Russian the
 *    button labels change and any text-based selector breaks, so the seeding run
 *    pins `--lang=en-US` for a deterministic DOM.
 *  - `chrome://settings` is built from nested shadow roots, so ordinary CSS
 *    selectors cannot reach the inputs. Everything below goes through the
 *    settings API on the page object instead of clicking, which is both more
 *    robust and locale-independent.
 */

import type { BrowserContext, Page } from 'playwright-core';

/** Google's search URL template in Chromium's placeholder syntax. */
export const GOOGLE_SEARCH_URL = 'https://www.google.com/search?q=%s';
export const GOOGLE_SUGGEST_URL =
  'https://www.google.com/complete/search?client=chrome&q={searchTerms}';
export const GOOGLE_NAME = 'Google';
export const GOOGLE_KEYWORD = 'google.com';

export interface SeedResult {
  ok: boolean;
  /** Short, user-facing explanation for the session log. */
  message: string;
}

/**
 * Add Google and make it default, via the settings page's own JS API.
 *
 * Runs inside the page because `chrome.send`/the settings private API is only
 * reachable from a `chrome://settings` document. Deliberately tolerant: a
 * failure here degrades to "no default search engine", which is a usability
 * annoyance, not a broken session — so it never throws into the launch path.
 */
export async function seedGoogleSearch(context: BrowserContext): Promise<SeedResult> {
  let page: Page | undefined;
  try {
    page = await context.newPage();
    // `domcontentloaded` is enough; settings lazily hydrates its subpages and
    // waiting for `load` on chrome:// can hang.
    await page.goto('chrome://settings/searchEngines', {
      waitUntil: 'domcontentloaded',
      timeout: 30_000,
    });

    // The settings page exposes `chrome.searchEnginesPrivate`-backed methods
    // through its own bridge. Reaching them via the shadow DOM host avoids every
    // localised label.
    const outcome = await page.evaluate(
      async ([name, keyword, url]) => {
        // `searchEngines` browser proxy, as used by the settings UI itself.
        type Engine = { modelIndex?: number; displayName?: string; keyword?: string; isDefault?: boolean };
        type Proxy = {
          getSearchEnginesList: () => Promise<{ defaults: Engine[]; others: Engine[] }>;
          searchEngineEditStarted: (i: number) => void;
          searchEngineEditCompleted: (n: string, k: string, u: string) => void;
          searchEngineEditCancelled: () => void;
          setDefaultSearchEngine: (i: number) => void;
        };

        const w = window as unknown as {
          settings?: { SearchEnginesBrowserProxyImpl?: { getInstance: () => Proxy } };
        };
        const proxy = w.settings?.SearchEnginesBrowserProxyImpl?.getInstance?.();
        if (!proxy) return { ok: false, reason: 'settings API not available in this build' };

        const before = await proxy.getSearchEnginesList();
        const already = [...before.defaults, ...before.others].find(
          (e) => (e.keyword ?? '').toLowerCase() === keyword.toLowerCase(),
        );

        if (!already) {
          // -1 is the "new engine" sentinel the Add dialog uses.
          proxy.searchEngineEditStarted(-1);
          proxy.searchEngineEditCompleted(name, keyword, url);
        }

        const after = await proxy.getSearchEnginesList();
        const target = [...after.defaults, ...after.others].find(
          (e) => (e.keyword ?? '').toLowerCase() === keyword.toLowerCase(),
        );
        if (!target || typeof target.modelIndex !== 'number') {
          return { ok: false, reason: 'engine was added but could not be located' };
        }
        if (target.isDefault) return { ok: true, reason: 'already default' };

        proxy.setDefaultSearchEngine(target.modelIndex);
        return { ok: true, reason: 'set as default' };
      },
      [GOOGLE_NAME, GOOGLE_KEYWORD, GOOGLE_SEARCH_URL],
    );

    if (!outcome.ok) {
      return { ok: false, message: `Could not set Google as the default search engine: ${outcome.reason}.` };
    }

    // Give Chromium a moment to persist the pref before the page is torn down.
    // Prefs are written on a delay; closing instantly can lose the change.
    await page.waitForTimeout(600);

    return {
      ok: true,
      message:
        'Google set as the default search engine for this profile. ' +
        'It persists as long as the browser is closed normally (not force-killed).',
    };
  } catch (e) {
    return {
      ok: false,
      message: `Could not set the default search engine: ${(e as Error)?.message ?? String(e)}`,
    };
  } finally {
    try {
      await page?.close();
    } catch {
      /* already gone */
    }
  }
}

/**
 * Should this launch run the seeding pass?
 *
 * Once per profile, not once per app: the engine lives in the profile's own
 * user-data dir, so a second profile needs its own run.
 */
export function needsSearchEngineSeed(profile: { searchEngineSeeded?: boolean }): boolean {
  return profile.searchEngineSeeded !== true;
}

/**
 * Extra flags for a seeding launch.
 *
 * `--lang=en-US` is pinned so the settings DOM is deterministic. Only applied
 * when the profile does not itself pin a locale — overriding a profile's
 * deliberate `--lang` would change the Accept-Language header the site sees,
 * which is a fingerprint change and far worse than an unset search engine.
 */
export function seedingArgs(hasPinnedLocale: boolean): string[] {
  return hasPinnedLocale ? [] : ['--lang=en-US'];
}
