/**
 * Profile migration tests.
 *
 * These exist because of a specific bug report: a profile with "default"
 * settings was still flagged as an incognito window by browserscan.net. The
 * cause was not the flag builder and not the default value — both were correct.
 * It was that a profile written by an earlier build had no `storageQuotaMb` at
 * all, and `load()` spread the stored object into the app verbatim, so the
 * launch never passed `--fingerprint-storage-quota` while the editor happily
 * displayed the current default.
 *
 * So the assertions below are split in two, and the split is the whole point:
 *   - an old profile (no schemaVersion) MUST get the quota backfilled;
 *   - a current profile with the quota deliberately cleared MUST keep it clear.
 * Getting the first without the second would mean the app overrides the user.
 */

import { describe, expect, it } from 'vitest';
import { migrateProfile, migrateProfiles, PROFILE_SCHEMA_VERSION } from '../src/shared/migrate';
import { DEFAULT_STORAGE_QUOTA_MB, newProfile } from '../src/shared/defaults';
import { buildFingerprintArgs } from '../src/shared/fingerprint-args';

/** A v1 profile as an early build would have written it: no schemaVersion, no quota. */
function legacyProfile(): Record<string, unknown> {
  return {
    id: 'legacy1',
    name: 'Old Profile',
    tags: [],
    createdAt: 1700000000000,
    updatedAt: 1700000000000,
    fingerprint: {
      seed: 42424,
      platform: 'windows',
      screen: { mode: 'auto' },
      gpu: { mode: 'auto' },
      cpuCores: { mode: 'auto' },
      deviceMemory: { mode: 'auto' },
      noise: true,
      windowsFontMetrics: false,
      allowThirdPartyCookies: false,
      webrtc: { mode: 'auto' },
    },
    proxy: { kind: 'none' },
    locale: { mode: 'ip' },
    geo: { mode: 'ip' },
    behaviour: { humanize: true, preset: 'default', idleBetweenActions: false },
    startup: { startUrls: [], headless: false, extensionPaths: [], extraArgs: [] },
  };
}

describe('migrateProfile — the incognito regression', () => {
  it('reproduces the bug: a legacy profile emits no storage-quota flag before migration', () => {
    // Guard on the premise. If this ever stops holding, the migration below is
    // solving a problem that no longer exists and should be reconsidered.
    const raw = legacyProfile() as unknown as Parameters<typeof buildFingerprintArgs>[0];
    const args = buildFingerprintArgs(raw);
    expect(args.some((a) => a.startsWith('--fingerprint-storage-quota'))).toBe(false);
  });

  it('backfills storageQuotaMb on a profile that predates the field', () => {
    const { profile, changed, notes } = migrateProfile(legacyProfile());
    expect(profile.fingerprint.storageQuotaMb).toBe(DEFAULT_STORAGE_QUOTA_MB);
    expect(changed).toBe(true);
    expect(notes.join(' ')).toMatch(/storage quota/i);
  });

  it('the migrated profile now actually emits the flag', () => {
    const { profile } = migrateProfile(legacyProfile());
    const args = buildFingerprintArgs(profile);
    expect(args).toContain(`--fingerprint-storage-quota=${DEFAULT_STORAGE_QUOTA_MB}`);
  });

  it('uses a quota that describes a plausible disk, not a token bump', () => {
    // 5000 MB clears the incognito threshold but claims a ~8 GB disk, which is
    // its own anomaly. Real Chrome grants ~60% of free space.
    expect(DEFAULT_STORAGE_QUOTA_MB).toBeGreaterThanOrEqual(50000);
  });
});

describe('migrateProfile — respecting deliberate choices', () => {
  it('does NOT re-add a quota the user cleared on a current-version profile', () => {
    const current = { ...newProfile('Mine'), schemaVersion: PROFILE_SCHEMA_VERSION };
    delete (current.fingerprint as { storageQuotaMb?: number }).storageQuotaMb;

    const { profile } = migrateProfile(current);
    expect(profile.fingerprint.storageQuotaMb).toBeUndefined();
  });

  it('preserves a custom quota rather than resetting it to the default', () => {
    const raw = legacyProfile();
    (raw.fingerprint as Record<string, unknown>).storageQuotaMb = 7777;
    const { profile } = migrateProfile(raw);
    expect(profile.fingerprint.storageQuotaMb).toBe(7777);
  });

  it('reports no change for an already-current profile', () => {
    const first = migrateProfile(newProfile('Fresh')).profile;
    const second = migrateProfile(first);
    expect(second.changed).toBe(false);
    expect(second.profile).toEqual(first);
  });

  it('stamps the current schema version', () => {
    expect(migrateProfile(legacyProfile()).profile.schemaVersion).toBe(PROFILE_SCHEMA_VERSION);
  });
});

describe('migrateProfile — structural repair', () => {
  it('fills missing sub-objects instead of producing an unusable profile', () => {
    const { profile } = migrateProfile({ id: 'x', name: 'Bare' });
    expect(profile.fingerprint.screen.mode).toBe('auto');
    expect(profile.fingerprint.webrtc.mode).toBeDefined();
    expect(profile.proxy.kind).toBe('none');
    expect(profile.locale.mode).toBe('ip');
    expect(profile.behaviour.humanize).toBe(true);
    expect(profile.startup.startUrls).toEqual([]);
    expect(Array.isArray(profile.tags)).toBe(true);
  });

  it('pins a seed when one is missing, so the device identity stops re-rolling', () => {
    const raw = legacyProfile();
    delete (raw.fingerprint as Record<string, unknown>).seed;
    const { profile } = migrateProfile(raw);
    expect(profile.fingerprint.seed).toBeGreaterThan(0);
    expect(buildFingerprintArgs(profile).some((a) => a.startsWith('--fingerprint='))).toBe(true);
  });

  it('keeps an existing seed untouched — a changed seed is a changed identity', () => {
    const { profile } = migrateProfile(legacyProfile());
    expect(profile.fingerprint.seed).toBe(42424);
  });

  it('survives garbage where a profile should be', () => {
    for (const junk of [null, undefined, 42, 'nope', []]) {
      const { profile } = migrateProfile(junk);
      expect(profile.name).toBeTruthy();
      expect(profile.fingerprint.platform).toBe('windows');
    }
  });

  it('drops non-string entries from tags rather than passing them to the UI', () => {
    const raw = legacyProfile();
    raw.tags = ['ok', 5, null, 'fine'];
    expect(migrateProfile(raw).profile.tags).toEqual(['ok', 'fine']);
  });

  it('backfills startup keys added after the profile was written', () => {
    const raw = legacyProfile();
    raw.startup = { startUrls: ['https://example.com'] };
    const { profile } = migrateProfile(raw);
    expect(profile.startup.startUrls).toEqual(['https://example.com']);
    expect(profile.startup.extensionPaths).toEqual([]);
    expect(profile.startup.extraArgs).toEqual([]);
    expect(profile.startup.headless).toBe(false);
  });
});

describe('migrateProfiles', () => {
  it('reports changed when any single profile needed work', () => {
    const res = migrateProfiles([newProfile('Fresh'), legacyProfile()]);
    expect(res.changed).toBe(true);
    expect(res.profiles).toHaveLength(2);
  });

  it('prefixes notes with the profile name so a rewrite is explainable', () => {
    const res = migrateProfiles([legacyProfile()]);
    expect(res.notes[0]).toMatch(/^Old Profile: /);
  });

  it('reports no change for an all-current list', () => {
    expect(migrateProfiles([newProfile('A'), newProfile('B')]).changed).toBe(false);
  });

  it('handles an empty list', () => {
    expect(migrateProfiles([])).toEqual({ profiles: [], changed: false, notes: [] });
  });
});
