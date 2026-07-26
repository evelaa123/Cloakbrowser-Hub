using System.Net.NetworkInformation;
using CloakHub.Core.Branding;
using CloakHub.Core.Network;
using CloakHub.Core.Platform;
using CloakHub.Doctor.Console;

namespace CloakHub.Doctor.Reports;

/// <summary>
/// MAC address generation and the per-OS change plan.
/// <para>
/// The report leads with the limitation rather than burying it. The feature was
/// requested as part of looking "maximally similar" to a commercial anti-detect
/// browser, and those tools do list a MAC field — but no browser API exposes a
/// MAC address to a web page, so the field cannot affect a fingerprint. Saying so
/// up front is the difference between a tool and a placebo.
/// </para>
/// </summary>
public static class NetworkReport
{
    /// <summary>Nothing here changes the system. The report only prints commands.</summary>
    public static void Run(BadgeOs os)
    {
        Output.Section("MAC address");

        Output.Paragraph(MacSpoof.BrowserVisibilityNote);
        Output.Plain();
        Output.Warn("Nothing in this tool changes your MAC address. It prints commands only.");

        ReportInterfaces();
        ReportGeneration();
        ReportPlan(os);
    }

    /// <summary>
    /// The machine's real interfaces, so the user can name one in the plan.
    /// <para>
    /// Loopback and tunnel adapters are filtered out: they have no meaningful
    /// hardware address, and offering them as candidates would invite someone to
    /// run a change command against an interface that cannot accept one.
    /// </para>
    /// </summary>
    private static void ReportInterfaces()
    {
        Output.Plain();
        Output.Item("Network interfaces", "");

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException ex)
        {
            Output.Fail($"Could not enumerate interfaces: {ex.Message}");
            return;
        }

        var shown = 0;
        foreach (var nic in interfaces)
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            var raw = nic.GetPhysicalAddress().GetAddressBytes();
            if (raw.Length != 6) continue;

            var mac = MacAddress.Format(raw);
            var vendor = MacAddress.VendorOf(raw);
            var local = MacAddress.IsLocallyAdministered(raw) ? " [locally administered]" : "";

            Output.Plain($"    {nic.Name}");
            Output.Plain($"      {mac}  {nic.NetworkInterfaceType}  {nic.OperationalStatus}" +
                         $"{(vendor is null ? "" : $"  {vendor}")}{local}");
            shown++;
        }

        if (shown == 0)
            Output.Info("No interfaces with a hardware address were found.");
    }

    /// <summary>
    /// Deterministic generation, demonstrated including its repeatability.
    /// <para>
    /// The same seed printed twice is not padding — determinism is the property that
    /// makes the feature usable. A profile's LAN identity has to survive a relaunch,
    /// or the profile looks like a different device to the router each time, which
    /// is precisely the inconsistency the tool exists to avoid.
    /// </para>
    /// </summary>
    private static void ReportGeneration()
    {
        Output.Section("Generated addresses");

        Output.Plain("    seed     with a real vendor prefix        locally administered");
        Output.Plain("    " + new string('-', 70));

        foreach (var seed in new[] { 10001, 24680, 48219, 73501, 99999 })
        {
            var vendor = MacAddress.Generate(seed, realisticVendor: true);
            var local = MacAddress.Generate(seed, realisticVendor: false);
            var name = MacAddress.VendorOf(MacAddress.TryParse(vendor)!);
            Output.Plain($"    {seed,-8} {vendor}  {name,-18} {local}");
        }

        Output.Plain();
        Output.Paragraph(
            "The vendor column uses a real OUI, so the address looks like hardware from a " +
            "company that exists. The right-hand column sets the locally-administered bit " +
            "instead, which is technically correct for a made-up address but is itself a " +
            "signal — anything auditing the LAN can see the address was assigned by " +
            "software. Vendor prefixes are the better default for blending in.");
        Output.Plain();

        var repeat = MacAddress.Generate(48219);
        var again = MacAddress.Generate(48219);
        if (repeat == again)
            Output.Ok($"Deterministic: seed 48219 gives {repeat} on every run and every machine.");
        else
            Output.Fail($"Not deterministic — {repeat} then {again}. This is a bug.");
    }

    /// <summary>The command for this OS, with its warnings and its revert.</summary>
    private static void ReportPlan(BadgeOs os)
    {
        Output.Section("Change plan for this OS");

        var nic = FirstUsableInterface();
        var name = nic?.Name ?? (os == BadgeOs.Windows ? "Ethernet" : "eth0");
        var original = nic is null ? null : MacAddress.Format(nic.GetPhysicalAddress().GetAddressBytes());
        var target = MacAddress.Generate(48219);

        var plan = MacSpoof.Plan(HostOs.ToOsLike(os), name, target, original);

        Output.Item("Interface", name + (nic is null ? " (example — none detected)" : ""));
        Output.Item("Current address", original ?? "(unknown)");
        Output.Item("Target address", target);
        Output.Item("Supported", plan.Supported ? "yes" : "no");
        Output.Item("Needs admin/root", plan.RequiresElevation ? "yes" : "no");

        if (!plan.Supported)
        {
            Output.Plain();
            Output.Warn(plan.Explanation);
            return;
        }

        Output.Plain();
        Output.Item("Command", "");
        Output.Plain($"    {Join(plan.Command)}");

        if (plan.RevertCommand.Count > 0)
        {
            Output.Plain();
            Output.Item("Revert", "");
            Output.Plain($"    {Join(plan.RevertCommand)}");
        }

        Output.Plain();
        Output.Item("Warnings", "");
        foreach (var warning in plan.Warnings) Output.Bullet(warning);
    }

    /// <summary>
    /// The first interface that could plausibly accept a MAC change.
    /// <para>
    /// Requires <c>Up</c> as well as a 6-byte address: a down adapter's stored
    /// address is not necessarily what the driver will report once it comes up, so
    /// using it to build a revert command could hand the user a command that
    /// restores the wrong value.
    /// </para>
    /// </summary>
    private static NetworkInterface? FirstUsableInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && n.OperationalStatus == OperationalStatus.Up
                && n.GetPhysicalAddress().GetAddressBytes().Length == 6);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Render argv so it can be pasted into a shell.
    /// <para>
    /// The PowerShell branch puts a whole script into one argument, and printing it
    /// space-joined would produce something that looks copyable but silently means
    /// something different. Quoting anything containing a space keeps the printed
    /// form faithful to the argv the plan describes.
    /// </para>
    /// </summary>
    private static string Join(IReadOnlyList<string> argv) =>
        string.Join(" ", argv.Select(a =>
            a.Contains(' ') || a.Contains('"') ? $"\"{a.Replace("\"", "\\\"")}\"" : a));
}
