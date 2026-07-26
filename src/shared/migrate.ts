/**
 * Forward-migration for profiles read off disk.
 *
 * `profiles.json` is hand-editable by design, and it accumulates profiles
 * created by older builds. Until now `ProfileRepo.load()` spread those objects
 * straight into the app, which meant a field added in a later release simply
 * stayed `undefined` on every pre-existing profile. That is not a cosmetic gap:
 * `buildFingerprintArgs` only emits a flag when the value is present, so an old
 * profile silently launched *without* the flag while the UI showed the profile
 * as "default".
 *
 * That is exactly how the incognito-detection bug happened. `storageQuotaMb`
 * defaults to a non-incognito value in `defaultFingerprint()`, but a profile
 * created before that default landed had no `storageQuotaMb` at all, so
 * `--fingerprint-storage-quota` was never passed, the binary's normalised
 * default applied, and BrowserScan read it as a private window — with the
 * profile's own settings page insisting everything was default.
 *
 * Two rules make this safe:
 *
 *  1. Structural repair is unconditional. A missing sub-object (`webrtc`,
 *     `startup`, …) is always filled, because there is no legitimate reading of
 *     "absent" for those — the app would crash or misbehave either way.
 *
 *  2. Value backfill is version-gated. `schemaVersion` records which migration
 *     steps a profile has already been through, so backfilling can never undo a
 *     deliberate choice. If the user clears "Storage quota" to hand control back
 *     to the binary, that empty value survives every future load, because the
 *     profile is already at or past the version that introduced the field.
 */

import type { FingerprintConfig, Profile } from './types';
import {
  DEFAULT_STORAGE_QUOTA_MB,
  PROFILE_SCHEMA_VERSION,
  defaultBehaviour,
  defaultFingerprint,
  defaultGeo,
  defaultLocale,
  defaultProxy,
  defaultStartup,
  randomSeed,
} from './defaults';

export { PROFILE_SCHEMA_VERSION };

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v);
}

function strArray(v: unknown): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : [];
}

/**
 * Key-order-independent structural comparison.
 *
 * `changed` decides whether to rewrite the user's `profiles.json`, so it must
 * mean "the content differs", not "the keys came back in a different order".
 * `migrateProfile` rebuilds the object literal from scratch, so a plain
 * `JSON.stringify` comparison would report a change for every profile on every
 * start and rewrite the file pointlessly.
 */
function stableStringify(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`;
  if (isObject(value)) {
    const keys = Object.keys(value)
      .filter((k) => value[k] !== undefined)
      .sort();
    return `{${keys.map((k) => `${JSON.stringify(k)}:${stableStringify(value[k])}`).join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

/**
 * Fill any structurally missing part of a fingerprint without changing set values.
 *
 * Deliberately does NOT spread `fallback` wholesale. Doing that would make every
 * *optional* field non-optional again — a user who clears "Storage quota" to
 * hand control back to the binary would silently get the default back on the
 * next load, and the version-gated backfill below could never even observe the
 * field as absent. Structural fields (the sub-objects, the booleans) are filled
 * because there is no meaningful "unset" for them; optional fields are copied
 * only when the stored profile actually has them.
 */
function repairFingerprint(raw: unknown, fallback: FingerprintConfig): FingerprintConfig {
  if (!isObject(raw)) return fallback;
  const fp = raw as Partial<FingerprintConfig>;

  const out: FingerprintConfig = {
    // A profile with no seed re-rolls its device identity on every launch, which
    // is the opposite of what a profile is for. Pin one now rather than let the
    // account look like a different machine each session.
    seed: typeof fp.seed === 'number' && Number.isFinite(fp.seed) && fp.seed > 0 ? fp.seed : randomSeed(),
    platform: fp.platform ?? fallback.platform,
    brand: fp.brand ?? fallback.brand,
    screen: isObject(fp.screen) ? (fp.screen as FingerprintConfig['screen']) : fallback.screen,
    gpu: isObject(fp.gpu) ? (fp.gpu as FingerprintConfig['gpu']) : fallback.gpu,
    cpuCores: isObject(fp.cpuCores) ? (fp.cpuCores as FingerprintConfig['cpuCores']) : fallback.cpuCores,
    deviceMemory: isObject(fp.deviceMemory)
      ? (fp.deviceMemory as FingerprintConfig['deviceMemory'])
      : fallback.deviceMemory,
    webrtc: isObject(fp.webrtc) ? (fp.webrtc as FingerprintConfig['webrtc']) : fallback.webrtc,
    noise: typeof fp.noise === 'boolean' ? fp.noise : fallback.noise,
    windowsFontMetrics:
      typeof fp.windowsFontMetrics === 'boolean' ? fp.windowsFontMetrics : fallback.windowsFontMetrics,
    allowThirdPartyCookies:
      typeof fp.allowThirdPartyCookies === 'boolean'
        ? fp.allowThirdPartyCookies
        : fallback.allowThirdPartyCookies,
  };

  // Optional fields: present-or-absent is itself the setting, so only carry over
  // what was actually stored.
  if (typeof fp.storageQuotaMb === 'number') out.storageQuotaMb = fp.storageQuotaMb;
  if (typeof fp.taskbarHeight === 'number') out.taskbarHeight = fp.taskbarHeight;
  if (typeof fp.platformVersion === 'string' && fp.platformVersion) out.platformVersion = fp.platformVersion;
  if (typeof fp.brandVersion === 'string' && fp.brandVersion) out.brandVersion = fp.brandVersion;
  if (typeof fp.fontsDir === 'string' && fp.fontsDir) out.fontsDir = fp.fontsDir;

  return out;
}

export interface MigrationResult {
  profile: Profile;
  /** True when the stored form differed and `profiles.json` should be rewritten. */
  changed: boolean;
  /** Human-readable notes, so a silent rewrite is explainable in the logs. */
  notes: string[];
}

/**
 * Bring one stored profile up to the current schema.
 *
 * Returns the profile plus whether anything actually changed, so the caller can
 * flush once for a whole batch instead of per profile.
 */
export function migrateProfile(raw: unknown): MigrationResult {
  const notes: string[] = [];
  const src = isObject(raw) ? raw : {};
  const stored = src as Partial<Profile> & { schemaVersion?: unknown };
  const from = typeof stored.schemaVersion === 'number' ? stored.schemaVersion : 1;

  const platform =
    (isObject(stored.fingerprint) && (stored.fingerprint as Partial<FingerprintConfig>).platform) || 'windows';
  const fallback = defaultFingerprint(platform);

  const fingerprint = repairFingerprint(stored.fingerprint, fallback);

  // ---- version-gated value backfill -------------------------------------
  // v1 -> v2: the profile was written before storageQuotaMb existed, so an
  // absent value here means "never had the setting", not "user cleared it".
  if (from < 2 && fingerprint.storageQuotaMb === undefined) {
    fingerprint.storageQuotaMb = DEFAULT_STORAGE_QUOTA_MB;
    notes.push(`storage quota set to ${DEFAULT_STORAGE_QUOTA_MB} MB (was unset — read as incognito)`);
  }

  const now = Date.now();
  const profile: Profile = {
    id: typeof stored.id === 'string' && stored.id ? stored.id : `p${now.toString(36)}`,
    name: typeof stored.name === 'string' && stored.name.trim() ? stored.name : 'Profile',
    notes: typeof stored.notes === 'string' ? stored.notes : undefined,
    tags: strArray(stored.tags),
    color: typeof stored.color === 'string' ? stored.color : undefined,
    createdAt: typeof stored.createdAt === 'number' ? stored.createdAt : now,
    updatedAt: typeof stored.updatedAt === 'number' ? stored.updatedAt : now,
    lastRunAt: typeof stored.lastRunAt === 'number' ? stored.lastRunAt : undefined,
    fingerprint,
    proxy: isObject(stored.proxy) ? (stored.proxy as Profile['proxy']) : defaultProxy(),
    locale: isObject(stored.locale) ? (stored.locale as Profile['locale']) : defaultLocale(),
    geo: isObject(stored.geo) ? (stored.geo as Profile['geo']) : defaultGeo(),
    behaviour: isObject(stored.behaviour) ? (stored.behaviour as Profile['behaviour']) : defaultBehaviour(),
    startup: isObject(stored.startup)
      ? { ...defaultStartup(), ...(stored.startup as Profile['startup']) }
      : defaultStartup(),
    cookies: isObject(stored.cookies) ? (stored.cookies as Profile['cookies']) : undefined,
    userAgent: typeof stored.userAgent === 'string' && stored.userAgent ? stored.userAgent : undefined,
    searchEngineSeeded:
      typeof stored.searchEngineSeeded === 'boolean' ? stored.searchEngineSeeded : undefined,
    schemaVersion: PROFILE_SCHEMA_VERSION,
  };

  const changed =
    from !== PROFILE_SCHEMA_VERSION || stableStringify(stored) !== stableStringify(profile);
  return { profile, changed, notes };
}

/** Migrate a whole list, reporting whether the file needs rewriting. */
export function migrateProfiles(list: unknown[]): {
  profiles: Profile[];
  changed: boolean;
  notes: string[];
} {
  let changed = false;
  const notes: string[] = [];
  const profiles = list.map((raw) => {
    const res = migrateProfile(raw);
    if (res.changed) changed = true;
    for (const n of res.notes) notes.push(`${res.profile.name}: ${n}`);
    return res.profile;
  });
  return { profiles, changed, notes };
}
