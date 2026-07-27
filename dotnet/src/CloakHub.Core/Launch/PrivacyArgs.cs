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
    /// Port blocking no longer emits <c>--host-resolver-rules</c>. That flag was
    /// removed for two independent reasons, either of which would be enough:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///   <b>It is on Chromium's bad-flags list.</b> Passing it raises the yellow
    ///   "You are using an unsupported command-line flag" banner on every launch.
    ///   That banner is a far worse outcome than the probe it was defending
    ///   against: it steals ~40px of viewport, so <c>innerHeight</c> stops
    ///   matching what a real maximised Chrome on the spoofed screen size would
    ///   report. A profile that carefully spoofs 1920x1080 and then reports an
    ///   off-by-40 viewport is <i>more</i> identifiable, not less — the same
    ///   reasoning <see cref="SandboxArgs"/> already applies to <c>--no-sandbox</c>.
    ///   </item>
    ///   <item>
    ///   <b>Half of it never worked.</b> Host-resolver rules run in the DNS
    ///   resolver, and the resolver is not consulted for IP literals. The
    ///   <c>MAP 127.0.0.1:&lt;port&gt;</c> half of the rule set was therefore a
    ///   no-op, and a page could reach any "blocked" port simply by asking for
    ///   <c>http://127.0.0.1:3389</c> instead of <c>http://localhost:3389</c>.
    ///   The setting promised protection it did not provide.
    ///   </item>
    /// </list>
    /// <para>
    /// Blocking loopback properly needs request interception over CDP, which
    /// requires a live per-session connection and cannot be expressed as argv.
    /// That belongs in the session layer, not here. Until it exists, the
    /// honest thing is to emit nothing and say so — see
    /// <see cref="PortBlockingNotice"/>, which the UI surfaces so the setting
    /// cannot quietly look active while doing nothing.
    /// </para>
    /// </summary>
    public static List<string> Build(Profile profile)
    {
        var args = new List<string>();

        if (profile.Startup.DoNotTrack)
            args.Add("--enable-do-not-track");

        return args;
    }

    /// <summary>
    /// Explanation for a profile that asks for blocked ports, or null when it
    /// does not.
    /// <para>
    /// Returned rather than logged here so the caller decides where it belongs —
    /// the session log on launch, and the editor beside the field. A security
    /// control that silently does nothing is worse than one that is absent,
    /// because the user stops looking for the risk.
    /// </para>
    /// </summary>
    public static string? PortBlockingNotice(Profile profile)
    {
        var ports = NormalisePorts(profile.Startup.BlockedPorts);
        if (ports.Count == 0) return null;

        return
            $"Localhost port blocking ({string.Join(", ", ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}) " +
            "is not applied to this session. It relied on --host-resolver-rules, which Chromium flags as " +
            "unsupported — the resulting banner shrinks the viewport and makes the profile easier to " +
            "identify than the port probe it blocked, and the rule never covered 127.0.0.1 in the first place.";
    }
}
