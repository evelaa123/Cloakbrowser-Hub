import { describe, expect, it } from 'vitest';
import { parseProxyLine, parseProxyList, proxyUrl } from '../src/main/services/proxy';

describe('parseProxyLine', () => {
  it('parses host:port', () => {
    expect(parseProxyLine('1.2.3.4:8080')).toEqual({ kind: 'http', host: '1.2.3.4', port: 8080 });
  });

  it('parses host:port:user:pass — the most common provider export', () => {
    expect(parseProxyLine('proxy.example.com:8080:alice:s3cret')).toEqual({
      kind: 'http',
      host: 'proxy.example.com',
      port: 8080,
      username: 'alice',
      password: 's3cret',
    });
  });

  it('parses user:pass@host:port', () => {
    expect(parseProxyLine('alice:s3cret@proxy.example.com:8080')).toEqual({
      kind: 'http',
      host: 'proxy.example.com',
      port: 8080,
      username: 'alice',
      password: 's3cret',
    });
  });

  it('parses the inverted user:pass:host:port order', () => {
    expect(parseProxyLine('alice:s3cret:proxy.example.com:8080')).toEqual({
      kind: 'http',
      host: 'proxy.example.com',
      port: 8080,
      username: 'alice',
      password: 's3cret',
    });
  });

  it('honours the scheme prefix', () => {
    expect(parseProxyLine('socks5://1.2.3.4:1080')).toMatchObject({ kind: 'socks5' });
    expect(parseProxyLine('https://1.2.3.4:443')).toMatchObject({ kind: 'https' });
    expect(parseProxyLine('socks5h://1.2.3.4:1080')).toMatchObject({ kind: 'socks5' });
    expect(parseProxyLine('SOCKS://1.2.3.4:1080')).toMatchObject({ kind: 'socks5' });
  });

  it('combines a scheme with credentials', () => {
    expect(parseProxyLine('socks5://alice:s3cret@p.io:1080')).toEqual({
      kind: 'socks5',
      host: 'p.io',
      port: 1080,
      username: 'alice',
      password: 's3cret',
    });
  });

  it('keeps colons that belong to the password', () => {
    const out = parseProxyLine('alice:pa:ss@p.io:1080');
    expect(out).toMatchObject({ host: 'p.io', port: 1080, username: 'alice', password: 'pa:ss' });
  });

  it('strips a leading provider label', () => {
    expect(parseProxyLine('US-Residential-1 | 1.2.3.4:8080')).toMatchObject({ host: '1.2.3.4', port: 8080 });
  });

  it('parses host:port:user with no password', () => {
    expect(parseProxyLine('p.io:8080:alice')).toEqual({ kind: 'http', host: 'p.io', port: 8080, username: 'alice' });
  });

  it('rejects lines with no usable port', () => {
    expect(parseProxyLine('justahost.com')).toBeNull();
    expect(parseProxyLine('p.io:notaport')).toBeNull();
    expect(parseProxyLine('p.io:0')).toBeNull();
    expect(parseProxyLine('p.io:70000')).toBeNull();
  });

  it('ignores blank lines and comments', () => {
    expect(parseProxyLine('')).toBeNull();
    expect(parseProxyLine('   ')).toBeNull();
    expect(parseProxyLine('# my proxies')).toBeNull();
  });
});

describe('parseProxyList', () => {
  it('parses valid rows and reports the bad ones by line number', () => {
    const text = ['# residential', '1.2.3.4:8080', 'broken-line', '', 'socks5://a:b@p.io:1080'].join('\n');
    const { proxies, failed } = parseProxyList(text);
    expect(proxies).toHaveLength(2);
    expect(failed).toEqual([{ line: 3, text: 'broken-line' }]);
  });
});

describe('proxyUrl', () => {
  it('builds a URL with percent-encoded credentials', () => {
    const url = proxyUrl({ kind: 'http', host: 'p.io', port: 8080, username: 'user@corp', password: 'p@ss word' });
    expect(url).toBe('http://user%40corp:p%40ss%20word@p.io:8080');
  });

  it('omits the auth section when there are no credentials', () => {
    expect(proxyUrl({ kind: 'socks5', host: 'p.io', port: 1080 })).toBe('socks5://p.io:1080');
  });

  it('returns undefined for an unusable config', () => {
    expect(proxyUrl({ kind: 'none' })).toBeUndefined();
    expect(proxyUrl({ kind: 'http', host: 'p.io' })).toBeUndefined();
  });
});
