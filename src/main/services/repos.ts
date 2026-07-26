/**
 * Persistence layer: profiles, proxy library, settings.
 *
 * Everything is kept in memory and flushed to disk on every mutation. Profile
 * counts in this class of tool are in the hundreds, not millions, so a JSON file
 * is the right trade-off: trivially inspectable, trivially backed up, no native
 * module to compile for three platforms.
 *
 * Proxy credentials are encrypted at rest; profile bodies are not, so a user can
 * still read and hand-edit their own profiles.json.
 */

import fs from 'node:fs';
import type { AppSettings, Profile, ProxyConfig, SavedProxy } from '../../shared/types';
import { cryptoId, defaultSettings, newProfile, randomSeed } from '../../shared/defaults';
import { paths, safeId } from './paths';
import { readJson, writeJson } from './store';
import { decrypt, encrypt } from './secrets';

// ---------------------------------------------------------------------------
// Proxy secret encoding
// ---------------------------------------------------------------------------

interface ProfilesFile {
  version: 1;
  profiles: Profile[];
}

/**
 * Stored form of a proxy: the password is encrypted, so profiles.json and
 * proxies.json never contain a plaintext credential.
 */
type StoredProxy<T extends ProxyConfig> = Omit<T, 'password'> & { password?: string; passwordEnc?: string };

function encodeProxy<T extends ProxyConfig>(p: T): StoredProxy<T> {
  const { password, ...rest } = p;
  const out = rest as StoredProxy<T>;
  if (password) out.passwordEnc = encrypt(password);
  return out;
}

function decodeProxy<T extends ProxyConfig>(p: StoredProxy<T>): T {
  const { passwordEnc, ...rest } = p;
  const out = rest as T;
  if (passwordEnc) {
    // A secret encrypted on another machine cannot be read here; treat it as
    // "no password" so the profile still opens and the user can re-enter it.
    const plain = decrypt(passwordEnc);
    if (plain) out.password = plain;
  }
  return out;
}

// ---------------------------------------------------------------------------
// Profiles
// ---------------------------------------------------------------------------

export class ProfileRepo {
  private profiles: Profile[] = [];
  private loaded = false;

  load(): void {
    if (this.loaded) return;
    const file = readJson<ProfilesFile>(paths.profilesFile(), { version: 1, profiles: [] });
    this.profiles = (file.profiles ?? []).map((p) => ({
      ...p,
      proxy: decodeProxy(p.proxy as StoredProxy<ProxyConfig>),
    }));
    this.loaded = true;
  }

  private flush(): void {
    writeJson(paths.profilesFile(), {
      version: 1,
      profiles: this.profiles.map((p) => ({ ...p, proxy: encodeProxy(p.proxy) })),
    } satisfies ProfilesFile);
  }

  all(): Profile[] {
    this.load();
    // Newest activity first — the profile you just touched is the one you want.
    return [...this.profiles].sort((a, b) => b.updatedAt - a.updatedAt);
  }

  get(id: string): Profile | undefined {
    this.load();
    return this.profiles.find((p) => p.id === id);
  }

  create(partial?: Partial<Profile>): Profile {
    this.load();
    const base = newProfile(
      partial?.name?.trim() || this.nextName(),
      partial?.fingerprint?.platform ?? 'windows',
    );
    const profile: Profile = { ...base, ...partial, id: partial?.id ?? base.id };
    // Guard against an id collision coming from an import.
    if (this.profiles.some((p) => p.id === profile.id)) profile.id = cryptoId();
    this.profiles.push(profile);
    this.flush();
    return profile;
  }

  update(id: string, patch: Partial<Profile>): Profile | undefined {
    this.load();
    const idx = this.profiles.findIndex((p) => p.id === id);
    if (idx === -1) return undefined;
    const merged: Profile = { ...this.profiles[idx]!, ...patch, id, updatedAt: Date.now() };
    this.profiles[idx] = merged;
    this.flush();
    return merged;
  }

  remove(id: string, opts: { deleteData?: boolean } = {}): boolean {
    this.load();
    const before = this.profiles.length;
    this.profiles = this.profiles.filter((p) => p.id !== id);
    if (this.profiles.length === before) return false;
    this.flush();

    if (opts.deleteData) {
      // Best effort: a locked user-data dir (browser still shutting down) must
      // not block removing the profile from the list.
      try {
        fs.rmSync(profileDataDir(id), { recursive: true, force: true });
      } catch {
        /* ignore */
      }
      try {
        fs.rmSync(paths.cookieJar(id), { force: true });
      } catch {
        /* ignore */
      }
    }
    return true;
  }

  /**
   * Duplicate a profile. The clone gets a fresh fingerprint seed by default —
   * two accounts sharing one fingerprint is the classic way to get both linked.
   */
  duplicate(id: string, opts: { newSeed?: boolean; copyCookies?: boolean } = {}): Profile | undefined {
    const src = this.get(id);
    if (!src) return undefined;

    const clone = this.create({
      ...structuredClone(src),
      id: cryptoId(),
      name: `${src.name} copy`,
      createdAt: Date.now(),
      updatedAt: Date.now(),
      lastRunAt: undefined,
      cookies: opts.copyCookies ? src.cookies : undefined,
    });

    if (opts.newSeed !== false) {
      this.update(clone.id, { fingerprint: { ...clone.fingerprint, seed: randomSeed() } });
    }
    if (opts.copyCookies) {
      try {
        fs.copyFileSync(paths.cookieJar(id), paths.cookieJar(clone.id));
      } catch {
        /* nothing to copy */
      }
    }
    return this.get(clone.id);
  }

  markRun(id: string): void {
    this.update(id, { lastRunAt: Date.now() });
  }

  private nextName(): string {
    const used = new Set(this.profiles.map((p) => p.name));
    for (let i = 1; i < 10_000; i++) {
      const name = `Profile ${i}`;
      if (!used.has(name)) return name;
    }
    return `Profile ${cryptoId()}`;
  }

  /** Export profiles for sharing or backup. Secrets are stripped. */
  export(ids?: string[]): { version: 1; exportedAt: number; profiles: Profile[] } {
    this.load();
    const chosen = ids?.length ? this.profiles.filter((p) => ids.includes(p.id)) : this.profiles;
    return {
      version: 1,
      exportedAt: Date.now(),
      // An export is a file the user may share, and a shared proxy password is
      // a leaked proxy password.
      profiles: chosen.map((p) => {
        const copy = structuredClone(p);
        delete copy.proxy.password;
        return copy;
      }),
    };
  }

  /** Import profiles from an export file. Ids are always regenerated. */
  import(data: unknown): { imported: number; skipped: number } {
    this.load();
    const payload = data as { profiles?: unknown };
    const list: unknown[] = Array.isArray(data)
      ? data
      : Array.isArray(payload?.profiles)
        ? (payload.profiles as unknown[])
        : [];

    let imported = 0;
    let skipped = 0;
    for (const item of list) {
      const p = item as Partial<Profile>;
      if (!p || typeof p !== 'object' || !p.fingerprint || !p.name) {
        skipped++;
        continue;
      }
      this.create({
        ...p,
        id: cryptoId(),
        createdAt: Date.now(),
        updatedAt: Date.now(),
        lastRunAt: undefined,
      });
      imported++;
    }
    return { imported, skipped };
  }
}

// ---------------------------------------------------------------------------
// Proxy library
// ---------------------------------------------------------------------------

interface ProxiesFile {
  version: 1;
  proxies: SavedProxy[];
}

export class ProxyRepo {
  private items: SavedProxy[] = [];
  private loaded = false;

  load(): void {
    if (this.loaded) return;
    const file = readJson<ProxiesFile>(paths.proxiesFile(), { version: 1, proxies: [] });
    this.items = (file.proxies ?? []).map((p) => decodeProxy(p as StoredProxy<SavedProxy>));
    this.loaded = true;
  }

  private flush(): void {
    writeJson(paths.proxiesFile(), {
      version: 1,
      proxies: this.items.map((p) => encodeProxy(p)),
    } satisfies ProxiesFile);
  }

  all(): SavedProxy[] {
    this.load();
    return [...this.items].sort((a, b) => b.createdAt - a.createdAt);
  }

  get(id: string): SavedProxy | undefined {
    this.load();
    return this.items.find((p) => p.id === id);
  }

  add(config: ProxyConfig, name?: string): SavedProxy {
    this.load();
    const entry: SavedProxy = {
      ...config,
      id: cryptoId(),
      name: name?.trim() || `${config.host ?? 'proxy'}:${config.port ?? ''}`,
      createdAt: Date.now(),
    };
    this.items.push(entry);
    this.flush();
    return entry;
  }

  addMany(configs: ProxyConfig[]): SavedProxy[] {
    return configs.map((c) => this.add(c));
  }

  update(id: string, patch: Partial<SavedProxy>): SavedProxy | undefined {
    this.load();
    const idx = this.items.findIndex((p) => p.id === id);
    if (idx === -1) return undefined;
    this.items[idx] = { ...this.items[idx]!, ...patch, id };
    this.flush();
    return this.items[idx];
  }

  remove(id: string): boolean {
    this.load();
    const before = this.items.length;
    this.items = this.items.filter((p) => p.id !== id);
    if (this.items.length === before) return false;
    this.flush();
    return true;
  }
}

// ---------------------------------------------------------------------------
// Settings
// ---------------------------------------------------------------------------

export class SettingsRepo {
  private settings?: AppSettings;

  get(): AppSettings {
    if (!this.settings) {
      this.settings = {
        ...defaultSettings(),
        ...readJson<Partial<AppSettings>>(paths.settingsFile(), {}),
      };
    }
    return this.settings;
  }

  update(patch: Partial<AppSettings>): AppSettings {
    const next = { ...this.get(), ...patch };
    this.settings = next;
    writeJson(paths.settingsFile(), next);
    return next;
  }
}

export const Profiles = new ProfileRepo();
export const Proxies = new ProxyRepo();
export const Settings = new SettingsRepo();

/** Resolve the Chromium user-data dir for a profile, honouring app settings. */
export function profileDataDir(profileId: string): string {
  return paths.profileDataDir(safeId(profileId), Settings.get().profilesDir);
}
