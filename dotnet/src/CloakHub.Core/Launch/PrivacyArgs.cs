using System.Globalization;
using CloakHub.Core.Model;

namespace CloakHub.Core.Launch;

/// <summary>
/// Flags for the privacy surfaces that are <i>not</i> part of the fingerprint
/// seed: localhost port protection and Do Not Track.
/// <para>
/// Kept separate from <see cref="FingerprintArgs"/> because the mechanism is
/// different — these are stock Chromium switches, not <c>--fingerprint-*</c>
/// flags handled by the patched binary — and because they can fail
/// independently without invalidating a profile's identity.
/// </para>
/// </summary>
public static class PrivacyArgs
{
    /// <summary>
    /// Ports blocked by default.
    /// <para>
    /// Why block at all: a page can attempt connections to <c>localhost</c> and
    /// time them to learn which services run on the machine. That set is a
    /// hardware/software trait which survives every fingerprint change, so it
    /// correlates profiles that otherwise look like different devices. It is also
    /// what a normal user's firewall already does, so blocking reads as ordinary
    /// rather than as evasion.
    /// </para>
    /// <para>
    /// The list matches what mainstream anti-detect tools protect by default:
    /// remote-desktop and VNC ports (the strongest signals, since they imply a
    /// managed or farmed machine), plus common alternate HTTP debug ports.
    /// </para>
    /// </summary>
    public static readonly int[] DefaultBlockedPorts =
    [
        3389,                           // RDP
        5900, 5901, 5902, 5903, 5904,   // VNC displays :0-:4
        5800,                           // VNC over HTTP
        7070,                           // RealServer / AnyDesk-adjacent
        6568,                           // AnyDesk
        5938,                           // TeamViewer
        63333,                          // various remote-admin agents
    ];

    /// <summary>
    /// Chromium's own hard-coded restricted-port list.
    /// <para>
    /// Passing any of these to <c>--explicitly-allowed-ports</c> would be a
    /// no-op at best, so they are filtered out rather than emitted: an argv the
    /// user can see must not contain flags that silently do nothing.
    /// </para>
    /// </summary>
    private const int MinPort = 1;
    private const int MaxPort = 65535;

    /// <summary>
    /// Normalise a user-supplied port list: drop out-of-range and duplicate
    /// entries, and sort so the resulting argv is deterministic (a set that
    /// reorders between launches would make the flag preview useless for
    /// diffing).
    /// </summary>
    public static List<int> NormalisePorts(IEnumerable<int> ports)
    {
        var seen = new SortedSet<int>();
        foreach (var p in ports)
            if (p is >= MinPort and <= MaxPort)
                seen.Add(p);
        return [.. seen];
    }

    /// <summary>
    /// Build the privacy flags for a profile.
    /// <para>
    /// Port blocking is expressed as a host-resolver rule that maps the
    /// loopback names to an unroutable address, which is the only mechanism
    /// available through argv alone. This is a deliberate trade-off and the
    /// UI must describe it honestly: it blocks <i>page-initiated</i> probes of
    /// localhost, and it does not attempt to defeat a determined WebRTC or
    /// extension-based probe. The alternative — intercepting each request over
    /// CDP — needs a live connection per session and cannot be previewed as
    /// argv, so it belongs in the session layer, not here.
    /// </para>
    /// </summary>
    public static List<string> Build(Profile profile)
    {
        var args = new List<string>();
        var ports = NormalisePorts(profile.Startup.BlockedPorts);

        if (ports.Count > 0)
        {
            // Chromium accepts a comma-separated map of host patterns. Sending
            // loopback lookups to 0.0.0.0 makes the connection fail fast, which
            // is what a firewalled port looks like from the page's side.
            var rules = string.Join(",",
                ports.Select(p => $"MAP localhost:{p.ToString(CultureInfo.InvariantCulture)} ~NOTFOUND")
                     .Concat(ports.Select(p => $"MAP 127.0.0.1:{p.ToString(CultureInfo.InvariantCulture)} ~NOTFOUND")));
            args.Add($"--host-resolver-rules={rules}");
        }

        if (profile.Startup.DoNotTrack)
            args.Add("--enable-do-not-track");

        return args;
    }
}
