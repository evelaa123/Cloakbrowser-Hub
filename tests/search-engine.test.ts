/**
 * Search-engine seeding tests.
 *
 * The binary is de-Googled and ships no prepopulated search engine, so a fresh
 * profile cannot search from the address bar. Per the binary's maintainer the
 * only supported fix is to drive chrome://settings/searchEngines once in a
 * persistent profile — Preferences edits fail the protected-prefs MAC check and
 * the Web Data keywords table is overwritten on every startup.
 *
 * The DOM-driving half needs a real Chromium, so what is covered here is the
 * decision logic around it: when seeding runs, and the one case where the
 * `--lang=en-US` pin must NOT be applied.
 */

import { describe, expect, it } from 'vitest';
import {
  GOOGLE_KEYWORD,
  GOOGLE_SEARCH_URL,
  needsSearchEngineSeed,
  seedingArgs,
} from '../src/main/browser/search-engine';

describe('needsSearchEngineSeed', () => {
  it('runs for a profile that has never been seeded', () => {
    expect(needsSearchEngineSeed({})).toBe(true);
    expect(needsSearchEngineSeed({ searchEngineSeeded: undefined })).toBe(true);
  });

  it('does not run again once seeded', () => {
    expect(needsSearchEngineSeed({ searchEngineSeeded: true })).toBe(false);
  });

  it('retries after a failed attempt', () => {
    // The flag is only set on success, so a failure must leave the profile
    // eligible — silently giving up would leave a permanently broken omnibox.
    expect(needsSearchEngineSeed({ searchEngineSeeded: false })).toBe(true);
  });
});

describe('seedingArgs', () => {
  it('pins en-US when the profile has no locale of its own', () => {
    // chrome://settings is localised; a deterministic DOM makes the run
    // reproducible regardless of the host's system language.
    expect(seedingArgs(false)).toEqual(['--lang=en-US']);
  });

  it('does NOT override a locale the profile pinned deliberately', () => {
    // This is the important one. --lang feeds Accept-Language, so forcing en-US
    // over a profile pinned to de-AT would change what the site sees — trading a
    // missing search engine for a fingerprint inconsistency.
    expect(seedingArgs(true)).toEqual([]);
  });
});

describe('Google engine constants', () => {
  it('uses the %s placeholder Chromium expects in a search URL', () => {
    expect(GOOGLE_SEARCH_URL).toContain('%s');
  });

  it('uses a keyword that matches how the engine is looked up', () => {
    expect(GOOGLE_KEYWORD).toBe('google.com');
  });

  it('points at real Google over https', () => {
    expect(GOOGLE_SEARCH_URL.startsWith('https://www.google.com/')).toBe(true);
  });
});
