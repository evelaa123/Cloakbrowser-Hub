using CloakHub.Core.Platform;
using CloakHub.Doctor;
using CloakHub.Doctor.Console;
using CloakHub.Doctor.Reports;

// CloakBrowser Hub diagnostics.
//
// The Hub's platform-specific behaviour cannot be verified by reading the code:
// whether a badge is legible in a real taskbar, whether a .desktop file lands
// where the window manager looks, whether a driver accepts a MAC change — all of
// those are properties of the machine, not of the source. This tool runs the same
// decision code the Hub runs and prints what it decided, then writes the actual
// assets so they can be opened and judged.
//
// It is deliberately read-only with respect to the system. It creates files in a
// directory you name and nothing else: no registry writes, no network interface
// changes, no installation. MAC commands are printed, never executed.

var options = Options.Parse(args);
if (options is null) return 2;

if (options.ShowHelp)
{
    Options.PrintUsage();
    return 0;
}

var os = options.ResolvedOs;

Output.Title("CloakBrowser Hub — diagnostics");

if (options.IsSimulated)
{
    Output.Plain();
    Output.Warn($"Reporting for {HostOs.Describe(os)} — the host is " +
                $"{HostOs.Describe(HostOs.Current)}.");
    Output.Paragraph(
        "Platform decisions are shown for the requested OS, which is exactly what the " +
        "Hub would decide there. Anything read from the machine itself — network " +
        "interfaces, the kernel sandbox knobs, whether a launcher stub is present — " +
        "still comes from this host, so treat those lines as local facts rather than " +
        "as predictions about the other platform.");
}

try
{
    HostReport.Run(os);
    BadgeReport.Run(os, options.OutputDir, options.WriteAssets);
    FingerprintReport.Run();
    NetworkReport.Run(os);
    await LauncherReport.RunAsync();
}
catch (Exception ex)
{
    // A diagnostic tool that dies with a bare stack trace has failed at its one
    // job. The trace is still printed — it is the useful part for a bug report —
    // but it is framed so the user knows what to do with it.
    Output.Plain();
    Output.Fail($"The report stopped early: {ex.GetType().Name} — {ex.Message}");
    Output.Plain();
    Output.Paragraph(
        "This is a bug in the Hub, not a problem with your machine. The detail below " +
        "identifies where it happened; please include it verbatim in a bug report.");
    Output.Plain();
    Output.Plain(ex.ToString());
    return 1;
}

Output.Section("Summary");

if (options.WriteAssets)
{
    Output.Item("Assets written to", options.OutputDir);
    Output.Plain();
    Output.Paragraph(
        "Open the icons directory. The 16px and 24px PNGs are the ones worth zooming " +
        "into — they are the sizes a taskbar and a window switcher actually draw, and " +
        "the sizes where a badge either works or turns to mush.");
}
else
{
    Output.Info("Read-only run; no files were created.");
}

Output.Plain();
Output.Paragraph(
    "Nothing above changed a system setting. The MAC section prints commands for you " +
    "to review; it never runs them.");
Output.Plain();

return 0;
