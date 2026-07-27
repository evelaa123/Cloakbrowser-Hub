using CloakHub.Core.Model;

namespace CloakHub.Core.Network;

/// <summary>
/// Turns a proxy into Chromium flags.
/// <para>
/// One flag carries the whole thing — <c>--proxy-server</c> — but the encoding
/// matters. Chromium's own parser truncates a password at certain characters, so
/// credentials are percent-encoded rather than passed through as typed; a password
/// containing <c>=</c> or <c>@</c> otherwise silently becomes a shorter, wrong one
/// and the proxy answers 407.
/// </para>
/// </summary>
public static class ProxyArgs
{
    /// <summary>
    /// Flags for a launch, and the relay that must stay alive alongside it.
    /// <para>
    /// The relay is returned rather than started and forgotten because its lifetime
    /// is the session's: it has to be disposed when the browser closes, or a
    /// listening socket holding proxy credentials outlives the thing that needed it.
    /// </para>
    /// </summary>
    public static ProxyLaunch Build(ProxyConfig proxy)
    {
        if (!proxy.IsConfigured) return new ProxyLaunch([], null);

        var args = new List<string>();

        // SOCKS5 takes credentials inline and always has. HTTP proxies are the ones
        // where inline auth depends on the binary's age, so those go through the
        // relay when they need authenticating -- which makes the behaviour the same
        // on every build rather than quietly correct on some and broken on others.
        if (proxy.Kind == ProxyKind.Socks5 || string.IsNullOrEmpty(proxy.Username))
        {
            args.Add($"--proxy-server={Encode(proxy)}");
            AddBypass(args, proxy);
            return new ProxyLaunch(args, null);
        }

        var relay = new ProxyRelay(proxy);
        relay.Start();

        args.Add($"--proxy-server=http://{relay.Endpoint}");
        AddBypass(args, proxy);

        return new ProxyLaunch(args, relay);
    }

    /// <summary>
    /// The <c>--proxy-server</c> value, credentials percent-encoded.
    /// <para>
    /// Internal so the exact string can be asserted: this is the difference between
    /// a working proxy and a 407 the user cannot explain.
    /// </para>
    /// </summary>
    internal static string Encode(ProxyConfig proxy)
    {
        var scheme = proxy.Kind switch
        {
            ProxyKind.Socks5 => "socks5",
            ProxyKind.Https => "https",
            _ => "http",
        };

        var auth = string.IsNullOrEmpty(proxy.Username)
            ? ""
            : $"{Uri.EscapeDataString(proxy.Username)}:{Uri.EscapeDataString(proxy.Password ?? "")}@";

        return $"{scheme}://{auth}{proxy.Host}:{proxy.Port}";
    }

    private static void AddBypass(List<string> args, ProxyConfig proxy)
    {
        if (string.IsNullOrWhiteSpace(proxy.Bypass)) return;

        // Loopback is always added. Without it the automation API and any local
        // tooling would be routed out through the proxy and back, which fails and
        // is slow when it does not.
        var entries = proxy.Bypass
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!entries.Contains("<-loopback>", StringComparer.Ordinal))
            entries.Add("<-loopback>");

        args.Add($"--proxy-bypass-list={string.Join(",", entries)}");
    }
}

/// <summary>
/// The flags for a proxied launch, plus the relay backing them if there is one.
/// </summary>
public sealed record ProxyLaunch(IReadOnlyList<string> Args, ProxyRelay? Relay) : IDisposable
{
    public void Dispose() => Relay?.Dispose();
}
