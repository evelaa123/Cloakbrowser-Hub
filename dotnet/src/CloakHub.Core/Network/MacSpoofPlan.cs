namespace CloakHub.Core.Network;

/// <summary>What a MAC change would require, and what it would actually achieve.</summary>
public sealed record MacSpoofPlan
{
    /// <summary>Whether the change can be attempted on this host at all.</summary>
    public bool Supported { get; init; }

    /// <summary>True when the command needs root / Administrator.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Command and arguments the user (not the Hub) would run.</summary>
    public IReadOnlyList<string> Command { get; init; } = [];

    /// <summary>Command that restores the hardware address.</summary>
    public IReadOnlyList<string> RevertCommand { get; init; } = [];

    /// <summary>
    /// What this does and does not accomplish, shown verbatim in the UI.
    /// <para>
    /// Not optional and not decoration. The single most likely way this feature
    /// harms a user is by letting them believe a MAC change hides them from a
    /// website, then acting on that belief.
    /// </para>
    /// </summary>
    public string Explanation { get; init; } = "";

    /// <summary>Consequences worth knowing before running the command.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Plans an OS-level MAC address change.
/// <para>
/// This class deliberately does not execute anything, and that is the whole
/// design. Changing a MAC means reconfiguring a network interface as root: it
/// drops the link, invalidates the DHCP lease, and on a wireless interface
/// disconnects the machine — occasionally requiring physical access to recover,
/// for instance when the change happens over the only available connection, or
/// when a captive portal or MAC-filtered AP then refuses the new address.
/// </para>
/// <para>
/// An app that silently takes root and cuts the user's network to make a browser
/// icon marginally less identifiable has made a decision that is not its to
/// make. So the Hub generates the address, explains precisely what changing it
/// achieves, prints the exact reversible command, and stops. The user runs it if
/// they want it.
/// </para>
/// <para>
/// The honest headline, repeated here because it is the point: a MAC address is
/// invisible to websites. This affects the local network, not browser
/// fingerprinting.
/// </para>
/// </summary>
public static class MacSpoof
{
    /// <summary>The explanation shown with every plan.</summary>
    public const string BrowserVisibilityNote =
        "A MAC address is not visible to websites. No browser API exposes it — not " +
        "navigator, not WebRTC, not WebGL — so changing it does not alter your " +
        "browser fingerprint and will not affect how a site identifies this " +
        "profile. It changes what your local network sees: the DHCP server, the " +
        "router's device list, captive portals, and MAC-based device recognition " +
        "on the LAN.";

    /// <summary>
    /// Build the plan for one interface.
    /// </summary>
    /// <param name="os">Target OS, injected so every branch is testable anywhere.</param>
    /// <param name="interfaceName">Interface to change, e.g. <c>eth0</c> or <c>en0</c>.</param>
    /// <param name="newMac">Desired address; must be a valid station address.</param>
    /// <param name="originalMac">Hardware address, used to build the revert command.</param>
    public static MacSpoofPlan Plan(
        BadgeOsLike os, string interfaceName, string newMac, string? originalMac = null)
    {
        var parsed = MacAddress.TryParse(newMac);
        if (parsed is null)
            return Unsupported($"\"{newMac}\" is not a MAC address.");

        if (!MacAddress.IsValidStationAddress(parsed))
            return Unsupported(
                "That address has the multicast bit set, which is not valid for a network " +
                "interface; most drivers reject it outright.");

        if (string.IsNullOrWhiteSpace(interfaceName))
            return Unsupported("No network interface was named.");

        var mac = MacAddress.Format(parsed);
        var warnings = BaseWarnings(interfaceName);

        return os switch
        {
            // ip(8) rather than the deprecated ifconfig: ifconfig is absent by
            // default on current distributions, and the two disagree about whether
            // the link must be brought down first.
            BadgeOsLike.Linux => new MacSpoofPlan
            {
                Supported = true,
                RequiresElevation = true,
                Command = ["sudo", "ip", "link", "set", "dev", interfaceName, "address", mac],
                RevertCommand = originalMac is null
                    ? []
                    : ["sudo", "ip", "link", "set", "dev", interfaceName, "address", originalMac],
                Explanation = BrowserVisibilityNote,
                Warnings = [.. warnings,
                    "The change is lost on reboot, which is the safe default: a bad address " +
                    "cannot lock you out permanently.",
                    "Some drivers refuse a change while the link is up. If it fails, bring the " +
                    $"interface down first: sudo ip link set dev {interfaceName} down"],
            },

            // macOS keeps the hardware address across reboots but silently reverts
            // Wi-Fi on rejoin, which surprises people into thinking it did not work.
            BadgeOsLike.MacOs => new MacSpoofPlan
            {
                Supported = true,
                RequiresElevation = true,
                Command = ["sudo", "ifconfig", interfaceName, "ether", mac],
                RevertCommand = originalMac is null
                    ? []
                    : ["sudo", "ifconfig", interfaceName, "ether", originalMac],
                Explanation = BrowserVisibilityNote,
                Warnings = [.. warnings,
                    "On Wi-Fi the address usually reverts when you rejoin a network or wake from " +
                    "sleep; disassociate first with: sudo /System/Library/PrivateFrameworks/" +
                    "Apple80211.framework/Versions/Current/Resources/airport -z",
                    "System Integrity Protection does not block this, but some Apple silicon Wi-Fi " +
                    "drivers ignore the change without reporting an error."],
            },

            // Windows has no supported CLI for this. netsh cannot do it; the address
            // lives in a per-adapter registry value read only at driver init, so the
            // adapter must be restarted for it to take effect.
            BadgeOsLike.Windows => new MacSpoofPlan
            {
                Supported = true,
                RequiresElevation = true,
                Command =
                [
                    "powershell", "-NoProfile", "-Command",
                    $"Set-NetAdapterAdvancedProperty -Name '{PsQuote(interfaceName)}' " +
                    $"-RegistryKeyword 'NetworkAddress' -RegistryValue '{mac.Replace(":", "")}'; " +
                    $"Restart-NetAdapter -Name '{PsQuote(interfaceName)}'",
                ],
                RevertCommand = ["powershell", "-NoProfile", "-Command",
                    $"Set-NetAdapterAdvancedProperty -Name '{PsQuote(interfaceName)}' " +
                    $"-RegistryKeyword 'NetworkAddress' -RegistryValue ''; " +
                    $"Restart-NetAdapter -Name '{PsQuote(interfaceName)}'"],
                Explanation = BrowserVisibilityNote,
                Warnings = [.. warnings,
                    "Windows stores this in the registry, so unlike Linux it persists across " +
                    "reboots — note the revert command before you run it.",
                    "Many drivers ignore the NetworkAddress keyword entirely, and the command " +
                    "still reports success. Verify with: Get-NetAdapter | Format-List MacAddress",
                    "Restart-NetAdapter drops the link for several seconds."],
            },

            _ => Unsupported("MAC address changes are not supported on this platform."),
        };
    }

    private static List<string> BaseWarnings(string interfaceName) =>
    [
        $"This reconfigures {interfaceName} and will briefly drop the connection.",
        "Your DHCP lease is invalidated, so the machine will get a new IP address.",
        "If you are connected through this interface remotely, you will lose access.",
        "A network that filters by MAC, or a captive portal, may refuse the new address.",
    ];

    private static MacSpoofPlan Unsupported(string reason) => new()
    {
        Supported = false,
        Explanation = $"{reason} {BrowserVisibilityNote}",
    };

    /// <summary>
    /// Escape a single-quoted PowerShell string. Adapter names routinely contain
    /// spaces ("Wi-Fi 2") and apostrophes are possible, which would otherwise
    /// terminate the literal early and change the meaning of the command.
    /// </summary>
    private static string PsQuote(string value) => value.Replace("'", "''");
}

/// <summary>
/// Host OS for network planning. Mirrors <c>BadgeOs</c> but kept separate so the
/// network layer does not depend on the branding layer.
/// </summary>
public enum BadgeOsLike { Windows, Linux, MacOs, Other }
