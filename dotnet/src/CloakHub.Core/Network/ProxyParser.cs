using System.Text.RegularExpressions;
using CloakHub.Core.Model;

namespace CloakHub.Core.Network;

/// <summary>
/// Reads proxies in whatever shape the provider exported them.
/// <para>
/// There is no standard format. Every provider invents its own ordering, and the
/// user is pasting a block of text they did not write and cannot easily reformat.
/// Demanding one canonical layout would push that work onto them for no gain, so
/// the parser accepts the shapes that actually occur and reports precisely which
/// lines it could not read.
/// </para>
/// </summary>
public static partial class ProxyParser
{
    /// <summary>
    /// Parse one line, or <c>null</c> when it cannot be understood.
    /// <para>
    /// Accepted forms, each with an optional <c>scheme://</c> prefix:
    /// <c>host:port</c>, <c>host:port:user:pass</c>, <c>user:pass@host:port</c>,
    /// <c>user:pass:host:port</c>, <c>host:port:user</c>.
    /// </para>
    /// </summary>
    public static ProxyConfig? ParseLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0 || text.StartsWith('#')) return null;

        // Some exports prefix a human label: "US-1 | 1.2.3.4:8080". Dropped only
        // when what follows still looks like a proxy, so a genuine pipe inside a
        // password does not truncate the line.
        var labelled = LabelPattern().Match(text);
        if (labelled.Success)
        {
            var rest = labelled.Groups[1].Value.Trim();
            if (rest.Contains(':') || rest.Contains('@')) text = rest;
        }

        ProxyKind? kind = null;
        var scheme = SchemePattern().Match(text);
        if (scheme.Success)
        {
            kind = NormaliseKind(scheme.Groups[1].Value);
            text = scheme.Groups[2].Value;
        }

        // With an @, credentials are on the left and the endpoint on the right.
        // Split at the last one: a password may legitimately contain @, but a
        // hostname may not.
        if (text.Contains('@'))
        {
            var at = text.LastIndexOf('@');
            var creds = text[..at];
            var (host, port) = SplitHostPort(text[(at + 1)..]);
            if (host.Length == 0) return null;

            var colon = creds.IndexOf(':');
            var username = colon == -1 ? creds : creds[..colon];
            var password = colon == -1 ? null : creds[(colon + 1)..];

            return Build(kind, host, port, username, password);
        }

        var parts = text.Split(':');
        switch (parts.Length)
        {
            case 2:
                return Build(kind, parts[0], parts[1]);

            case 4:
                // host:port:user:pass and user:pass:host:port are both in the wild
                // and are indistinguishable by shape alone, so the decision is made
                // by which half actually looks like an endpoint.
                if (LooksLikeHost(parts[0]) && IsPort(parts[1]))
                    return Build(kind, parts[0], parts[1], parts[2], parts[3]);
                if (LooksLikeHost(parts[2]) && IsPort(parts[3]))
                    return Build(kind, parts[2], parts[3], parts[0], parts[1]);

                // Genuinely ambiguous -- a numeric hostname, say. The provider
                // convention is host-first, so guessing anything else would be
                // wrong more often.
                return Build(kind, parts[0], parts[1], parts[2], parts[3]);

            case 3:
                // host:port:user, an authenticated proxy with no password.
                return IsPort(parts[1]) ? Build(kind, parts[0], parts[1], parts[2]) : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Parse a pasted block.
    /// <para>
    /// Bad lines are collected with their original line numbers rather than
    /// dropped. A user pasting two hundred proxies needs to know which three
    /// failed; "191 of 194 imported" with no detail is not actionable.
    /// </para>
    /// </summary>
    public static ProxyParseResult ParseList(string text)
    {
        var proxies = new List<ProxyConfig>();
        var failed = new List<ProxyParseFailure>();

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim('\r', ' ', '\t');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var parsed = ParseLine(trimmed);
            if (parsed is not null) proxies.Add(parsed);
            else failed.Add(new ProxyParseFailure(i + 1, trimmed));
        }

        return new ProxyParseResult(proxies, failed);
    }

    /// <summary>
    /// The full proxy URL, credentials included.
    /// <para>
    /// Only ever handed to a network client. It must not be logged or shown: the
    /// password is in it, and proxy credentials are usually account-wide.
    /// </para>
    /// </summary>
    public static string? ToUrl(ProxyConfig p)
    {
        if (!p.IsConfigured) return null;

        var scheme = p.Kind switch
        {
            ProxyKind.Socks5 => "socks5",
            ProxyKind.Https => "https",
            _ => "http",
        };

        var auth = string.IsNullOrEmpty(p.Username)
            ? ""
            : $"{Uri.EscapeDataString(p.Username)}:{Uri.EscapeDataString(p.Password ?? "")}@";

        return $"{scheme}://{auth}{p.Host}:{p.Port}";
    }

    /// <summary>
    /// The same endpoint with the credentials removed.
    /// <para>
    /// This is the form used in every message, log line and list row. Having a
    /// separate function for it means displaying a proxy safely is the easy path
    /// rather than something each call site has to remember.
    /// </para>
    /// </summary>
    public static string Describe(ProxyConfig p)
    {
        if (p.Kind == ProxyKind.None) return "Direct";
        if (string.IsNullOrWhiteSpace(p.Host)) return "Not configured";

        var scheme = p.Kind switch
        {
            ProxyKind.Socks5 => "socks5",
            ProxyKind.Https => "https",
            _ => "http",
        };

        var auth = string.IsNullOrEmpty(p.Username) ? "" : "•••@";
        return $"{scheme}://{auth}{p.Host}:{p.Port}";
    }

    /// <summary>Map a scheme token onto a supported kind.</summary>
    internal static ProxyKind NormaliseKind(string? raw)
    {
        var s = new string((raw ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        return s switch
        {
            // socks4 is folded into socks5 rather than rejected. Chromium's
            // --proxy-server speaks both, and a user who typed socks4 wants their
            // proxy used, not a validation error about a version number.
            "socks5" or "socks" or "socks5h" or "socks4" => ProxyKind.Socks5,
            "https" or "ssl" => ProxyKind.Https,
            _ => ProxyKind.Http,
        };
    }

    /// <summary>Whether a token reads as a hostname rather than a port or credential.</summary>
    internal static bool LooksLikeHost(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (token.All(char.IsAsciiDigit)) return false;      // a bare number is a port

        // The dot requirement is what separates a host from a username: providers
        // issue hostnames and IPs, both of which are dotted, while usernames
        // generally are not.
        return token.Contains('.') && HostPattern().IsMatch(token);
    }

    private static bool IsPort(string token) =>
        token.Length > 0 && token.All(char.IsAsciiDigit);

    private static (string Host, string? Port) SplitHostPort(string text)
    {
        var idx = text.LastIndexOf(':');
        return idx == -1 ? (text, null) : (text[..idx], text[(idx + 1)..]);
    }

    private static ProxyConfig? Build(
        ProxyKind? kind, string host, string? portText,
        string? username = null, string? password = null)
    {
        var h = host.Trim();
        if (h.Length == 0) return null;

        if (!int.TryParse(portText?.Trim(), out var port)) return null;
        if (port is <= 0 or > 65535) return null;

        return new ProxyConfig
        {
            Kind = kind ?? ProxyKind.Http,
            Host = h,
            Port = port,
            Username = string.IsNullOrEmpty(username) ? null : username,
            Password = string.IsNullOrEmpty(password) ? null : password,
        };
    }

    [GeneratedRegex(@"^[^|]*\|\s*(.+)$")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"^([a-z0-9]+)://(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SchemePattern();

    [GeneratedRegex(@"^[a-z0-9.\-_]+$", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();
}

/// <summary>What a pasted block produced: the proxies, and the lines that failed.</summary>
public sealed record ProxyParseResult(
    IReadOnlyList<ProxyConfig> Proxies,
    IReadOnlyList<ProxyParseFailure> Failed)
{
    public bool HasFailures => Failed.Count > 0;
}

/// <summary>A line the parser could not read, with its position in the paste.</summary>
public sealed record ProxyParseFailure(int Line, string Text);
