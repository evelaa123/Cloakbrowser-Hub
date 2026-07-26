using System.Globalization;
using System.Text;

namespace CloakHub.Core.Network;

/// <summary>
/// MAC address generation and OS-level spoof planning.
/// <para>
/// <b>Read this before using it.</b> A MAC address is not visible to a web page.
/// There is no Web API that exposes it: not <c>navigator</c>, not WebRTC, not
/// WebGL. Every "get the visitor's MAC in JavaScript" answer on the internet is
/// either about a local intranet with a native helper, or wrong. That is why
/// CloakBrowser has no <c>--fingerprint-mac</c> flag and why adding one would be
/// meaningless — there is nothing on the browser side to lie to.
/// </para>
/// <para>
/// So changing the MAC does not affect a site's fingerprint of the browser. What
/// it does affect is the local network segment: the DHCP server, the router's
/// client table, captive portals, and anything doing MAC-based device
/// recognition on the LAN. Those are real, but they are a different threat model
/// from browser fingerprinting, and conflating the two is how people end up
/// believing they are protected when they are not.
/// </para>
/// <para>
/// This type therefore does two narrow, honest things: it generates plausible
/// addresses, and it plans the OS command that would apply one. It never applies
/// anything itself — see <see cref="MacSpoofPlan"/> for why.
/// </para>
/// </summary>
public static class MacAddress
{
    /// <summary>
    /// Vendor prefixes (OUIs) of common consumer network hardware.
    /// <para>
    /// A random 48-bit value is a poor spoof: the first three octets are an
    /// IEEE-assigned vendor id, so an unassigned prefix is as distinctive as no
    /// spoof at all to anything that checks. These are real, widely deployed
    /// OUIs, which is what makes a generated address unremarkable.
    /// </para>
    /// </summary>
    public static readonly (string Oui, string Vendor)[] KnownOuis =
    [
        ("00:1A:2B", "Intel"),
        ("3C:5A:B4", "Google"),
        ("00:1B:44", "Apple"),
        ("D8:9E:F3", "Dell"),
        ("00:26:B9", "Dell"),
        ("B4:2E:99", "ASUSTek"),
        ("00:E0:4C", "Realtek"),
        ("00:50:56", "VMware"),
        ("08:00:27", "VirtualBox"),
        ("00:15:5D", "Hyper-V"),
    ];

    /// <summary>
    /// Generate a MAC deterministically from a seed.
    /// <para>
    /// Deterministic on purpose, matching the fingerprint seed doctrine: a
    /// profile that reports a different address on every launch is more
    /// suspicious than one that never changes, and it makes bug reports
    /// impossible to reproduce.
    /// </para>
    /// </summary>
    /// <param name="seed">Stable per-profile seed.</param>
    /// <param name="realisticVendor">
    /// When true the address starts with a real vendor OUI. When false a locally
    /// administered address is produced instead (see <see cref="IsLocallyAdministered"/>),
    /// which is the honest choice on a network that may validate OUIs against a
    /// registry, because it is unambiguously self-assigned rather than a forged
    /// claim to be Intel hardware.
    /// </param>
    public static string Generate(int seed, bool realisticVendor = true)
    {
        // A small deterministic PRNG rather than Random(seed): the .NET
        // implementation is explicitly not guaranteed stable across runtime
        // versions, and this value has to survive a framework upgrade unchanged.
        var state = unchecked((uint)seed * 2654435761u + 0x9E3779B9u);
        uint Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return state;
        }

        var octets = new byte[6];
        for (var i = 0; i < 6; i++) octets[i] = (byte)(Next() & 0xFF);

        if (realisticVendor)
        {
            var oui = KnownOuis[Next() % (uint)KnownOuis.Length].Oui;
            var parts = oui.Split(':');
            for (var i = 0; i < 3; i++)
                octets[i] = byte.Parse(parts[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        else
        {
            // Set the locally-administered bit and clear the multicast bit. A
            // multicast MAC as a station address is invalid and many drivers
            // reject it outright, which is a common failure in naive generators.
            octets[0] = (byte)((octets[0] | 0x02) & 0xFE);
        }

        return Format(octets);
    }

    /// <summary>Format six octets as colon-separated uppercase hex.</summary>
    public static string Format(byte[] octets)
    {
        if (octets.Length != 6) throw new ArgumentException("A MAC has six octets.", nameof(octets));
        var sb = new StringBuilder(17);
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) sb.Append(':');
            sb.Append(octets[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parse a MAC in any of the common notations, or null if it is not one.
    /// <para>
    /// Accepts colon, hyphen, dot and bare forms because users paste addresses
    /// from router pages, <c>ipconfig</c> and Cisco output interchangeably, and
    /// rejecting a valid address on formatting alone is a pointless obstacle.
    /// </para>
    /// </summary>
    public static byte[]? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var hex = new StringBuilder(12);
        foreach (var c in text)
        {
            if (Uri.IsHexDigit(c)) hex.Append(c);
            else if (c is ':' or '-' or '.' or ' ') continue;
            else return null;   // an unexpected character means this is not a MAC
        }

        if (hex.Length != 12) return null;

        var octets = new byte[6];
        for (var i = 0; i < 6; i++)
            octets[i] = byte.Parse(hex.ToString(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return octets;
    }

    /// <summary>Whether the address is valid as a station (interface) address.</summary>
    public static bool IsValidStationAddress(byte[] octets) =>
        octets.Length == 6 && (octets[0] & 0x01) == 0;   // multicast bit must be clear

    /// <summary>Whether the address is in the locally-administered range.</summary>
    public static bool IsLocallyAdministered(byte[] octets) =>
        octets.Length == 6 && (octets[0] & 0x02) != 0;

    /// <summary>Vendor for an address, or null when the OUI is not one we know.</summary>
    public static string? VendorOf(byte[] octets)
    {
        if (octets.Length != 6) return null;
        var prefix = Format(octets)[..8];
        foreach (var (oui, vendor) in KnownOuis)
            if (string.Equals(oui, prefix, StringComparison.OrdinalIgnoreCase)) return vendor;
        return null;
    }
}
