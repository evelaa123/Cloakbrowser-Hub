import { describe, expect, it, vi } from 'vitest';

// The cookies module imports ./secrets, which imports electron. Stub both so
// the pure parsing/sanitising logic can be tested outside Electron.
vi.mock('../src/main/services/secrets', () => ({
  encrypt: (s: string) => `PLAIN:${s}`,
  decrypt: (s: string) => (s.startsWith('PLAIN:') ? s.slice(6) : s),
  mask: (s: string) => s,
}));

const {
  parseJsonCookies,
  parseNetscapeCookies,
  parseHeaderCookies,
  parseCookieText,
  sanitizeCookie,
  validateCookieText,
  mergeCookies,
  detectAuthServices,
} = await import('../src/main/services/cookies');

describe('parseJsonCookies', () => {
  it('parses a bare Playwright-style array', () => {
    const out = parseJsonCookies(
      JSON.stringify([
        { name: 'a', value: '1', domain: '.example.com', path: '/', secure: true, httpOnly: true },
      ]),
    );
    expect(out).toHaveLength(1);
    expect(out[0]).toMatchObject({ name: 'a', value: '1', domain: '.example.com', secure: true, httpOnly: true });
  });

  it('parses Cookie-Editor field aliases and ms expiry', () => {
    const out = parseJsonCookies(
      JSON.stringify([
        { name: 'x', value: 'y', domain: '.google.com', expirationDate: 1893456000, sameSite: 'no_restriction' },
        { name: 'ms', value: 'v', domain: '.google.com', expires: 1893456000000 },
      ]),
    );
    expect(out[0].sameSite).toBe('None');
    expect(out[0].expires).toBe(1893456000);
    // milliseconds get folded down to seconds
    expect(out[1].expires).toBe(1893456000);
  });

  it('parses a Playwright storageState wrapper', () => {
    const out = parseJsonCookies(
      JSON.stringify({ cookies: [{ name: 'a', value: '1', domain: '.x.com', path: '/' }], origins: [] }),
    );
    expect(out).toHaveLength(1);
  });

  it('returns [] on invalid JSON instead of throwing', () => {
    expect(parseJsonCookies('{not json')).toEqual([]);
  });

  it('skips entries with no name or value', () => {
    const out = parseJsonCookies(JSON.stringify([{ name: 'ok', value: 'v' }, { name: 'bad' }, {}]));
    expect(out).toHaveLength(1);
  });
});

describe('parseNetscapeCookies', () => {
  const file = [
    '# Netscape HTTP Cookie File',
    '# a comment',
    '',
    '.example.com\tTRUE\t/\tTRUE\t1893456000\tfoo\tbar',
    '#HttpOnly_.example.com\tTRUE\t/\tTRUE\t1893456000\tsecret\tvalue',
  ].join('\n');

  it('parses tab-separated rows and skips comments', () => {
    const out = parseNetscapeCookies(file);
    expect(out).toHaveLength(2);
    expect(out[0]).toMatchObject({ name: 'foo', value: 'bar', domain: '.example.com', secure: true });
  });

  it('honours the #HttpOnly_ prefix', () => {
    const out = parseNetscapeCookies(file);
    expect(out[1]).toMatchObject({ name: 'secret', httpOnly: true, domain: '.example.com' });
  });

  it('infers httpOnly for known session cookie names without the prefix', () => {
    const out = parseNetscapeCookies('.google.com\tTRUE\t/\tTRUE\t0\tSID\tabc');
    expect(out[0].httpOnly).toBe(true);
    // expires 0 means "session cookie" → -1 in Playwright terms
    expect(out[0].expires).toBe(-1);
  });

  it('falls back to whitespace splitting and keeps spaces in the value', () => {
    const out = parseNetscapeCookies('.example.com TRUE / FALSE 0 name value with spaces');
    expect(out[0]).toMatchObject({ name: 'name', value: 'value with spaces' });
  });
});

describe('parseHeaderCookies', () => {
  it('parses a raw Cookie: header', () => {
    const out = parseHeaderCookies('Cookie: a=1; b=2; c=3', 'example.com');
    expect(out).toHaveLength(3);
    expect(out[0]).toMatchObject({ name: 'a', value: '1', domain: '.example.com' });
  });

  it('keeps = characters inside the value (base64 padding)', () => {
    const out = parseHeaderCookies('token=YWJjZA==', 'example.com');
    expect(out[0].value).toBe('YWJjZA==');
  });
});

describe('parseCookieText format detection', () => {
  it('detects JSON', () => {
    expect(parseCookieText('[{"name":"a","value":"1","domain":"x.com"}]')).toHaveLength(1);
  });
  it('detects Netscape', () => {
    expect(parseCookieText('.x.com\tTRUE\t/\tTRUE\t0\ta\t1')).toHaveLength(1);
  });
  it('detects a header string', () => {
    expect(parseCookieText('a=1; b=2', 'x.com')).toHaveLength(2);
  });
  it('returns [] for garbage', () => {
    expect(parseCookieText('hello world')).toEqual([]);
  });
});

describe('sanitizeCookie (Chromium acceptance rules)', () => {
  it('__Host- cookies become host-only: url set, domain and path removed', () => {
    const out = sanitizeCookie({ name: '__Host-GAPS', value: 'v', domain: '.accounts.google.com', path: '/' })!;
    expect(out.url).toBe('https://accounts.google.com/');
    expect(out.domain).toBeUndefined();
    // Playwright rejects url + path together
    expect(out.path).toBeUndefined();
    expect(out.secure).toBe(true);
  });

  it('__Secure- cookies are forced Secure', () => {
    const out = sanitizeCookie({ name: '__Secure-1PSID', value: 'v', domain: '.google.com', secure: false })!;
    expect(out.secure).toBe(true);
  });

  it('defaults known cross-site hosts to SameSite=None and pairs it with Secure', () => {
    const out = sanitizeCookie({ name: 'SID', value: 'v', domain: '.google.com', secure: false })!;
    expect(out.sameSite).toBe('None');
    expect(out.secure).toBe(true);
  });

  it('does not force SameSite=None on ordinary sites', () => {
    const out = sanitizeCookie({ name: 'sess', value: 'v', domain: '.myshop.local' })!;
    expect(out.sameSite).toBeUndefined();
    expect(out.secure).toBe(false);
  });

  it('an explicit SameSite=None from the source still forces Secure', () => {
    const out = sanitizeCookie({ name: 'a', value: 'v', domain: '.myshop.local', sameSite: 'None' })!;
    expect(out.secure).toBe(true);
  });

  it('preserves an explicit Strict/Lax value', () => {
    expect(sanitizeCookie({ name: 'a', value: 'v', domain: '.google.com', sameSite: 'Strict' })!.sameSite).toBe('Strict');
  });

  it('sets domain + path (never url) for ordinary cookies', () => {
    const out = sanitizeCookie({ name: 'a', value: 'v', domain: '.example.com', path: '/app' })!;
    expect(out.domain).toBe('.example.com');
    expect(out.path).toBe('/app');
    expect(out.url).toBeUndefined();
  });

  it('synthesises a url from the default host when no domain is present', () => {
    const out = sanitizeCookie({ name: 'a', value: 'v' }, 'fallback.test')!;
    expect(out.url).toBe('http://fallback.test/');
    expect(out.path).toBeUndefined();
  });

  it('defaults a missing expiry to -1 (session cookie)', () => {
    expect(sanitizeCookie({ name: 'a', value: 'v', domain: 'x.com' })!.expires).toBe(-1);
  });

  it('rejects a nameless cookie', () => {
    expect(sanitizeCookie({ name: '', value: 'v' })).toBeNull();
  });
});

describe('validateCookieText', () => {
  it('reports count, format, domains and a Google session hint', () => {
    const text = [
      '.google.com\tTRUE\t/\tTRUE\t1893456000\t__Secure-1PSID\tabc',
      '.google.com\tTRUE\t/\tTRUE\t1893456000\tSID\tdef',
      '.youtube.com\tTRUE\t/\tTRUE\t1893456000\tLOGIN_INFO\tghi',
    ].join('\n');
    const v = validateCookieText(text);
    expect(v.ok).toBe(true);
    expect(v.count).toBe(3);
    expect(v.format).toBe('netscape');
    expect(v.domains).toEqual(['.google.com', '.youtube.com']);
    expect(v.authHints).toContain('Google');
  });

  it('suggests an email found in the payload as the profile name', () => {
    const v = validateCookieText(JSON.stringify([{ name: 'a', value: 'user@gmail.com', domain: '.x.com' }]));
    expect(v.suggestedName).toBe('user@gmail.com');
  });

  it('flags an empty file with an explanation', () => {
    const v = validateCookieText('   ');
    expect(v.ok).toBe(false);
    expect(v.error).toMatch(/empty/i);
  });

  it('flags an unrecognised file with an explanation', () => {
    const v = validateCookieText('this is not a cookie file');
    expect(v.ok).toBe(false);
    expect(v.error).toMatch(/Unrecognised/i);
  });
});

describe('detectAuthServices', () => {
  it('needs two signature cookies or a matching domain', () => {
    // 'sessionid' alone is too generic to claim a service
    expect(detectAuthServices(new Set(['sessionid']), ['.unknown.com'])).toEqual([]);
    // domain evidence is enough
    expect(detectAuthServices(new Set(['sessionid']), ['.instagram.com'])).toContain('Instagram');
    // two signatures are enough without domain evidence
    expect(detectAuthServices(new Set(['c_user', 'xs']), ['.cdn.net'])).toContain('Facebook');
  });
});

describe('mergeCookies', () => {
  it('de-duplicates on name+domain+path with the later set winning', () => {
    const out = mergeCookies(
      [{ name: 'a', value: 'old', domain: '.x.com', path: '/' }],
      [{ name: 'a', value: 'new', domain: '.x.com', path: '/' }],
    );
    expect(out).toHaveLength(1);
    expect(out[0].value).toBe('new');
  });

  it('keeps same-name cookies that differ by domain or path', () => {
    const out = mergeCookies([
      { name: 'a', value: '1', domain: '.x.com', path: '/' },
      { name: 'a', value: '2', domain: '.y.com', path: '/' },
      { name: 'a', value: '3', domain: '.x.com', path: '/admin' },
    ]);
    expect(out).toHaveLength(3);
  });
});
