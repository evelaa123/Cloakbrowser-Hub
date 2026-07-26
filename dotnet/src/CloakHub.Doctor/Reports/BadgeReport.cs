using CloakHub.Core.Branding;
using CloakHub.Core.Model;
using CloakHub.Core.Platform;
using CloakHub.Doctor.Console;

namespace CloakHub.Doctor.Reports;

/// <summary>
/// The per-instance icon badging feature, reported and exercised for real.
/// <para>
/// This is the part of the report that writes files. Everything else here can be
/// answered by inspection, but "does the badge look right on your machine" cannot
/// — it has to be rendered and opened. So the report generates the actual assets
/// the session manager would generate and prints their paths.
/// </para>
/// </summary>
public static class BadgeReport
{
    /// <summary>Ordinals rendered into the sample sheet.</summary>
    private static readonly int[] SampleOrdinals = [1, 2, 3, 7, 12, 42, 99, 137];

    public static void Run(BadgeOs os, string outputDir, bool writeAssets)
    {
        Output.Section("Instance badging");

        var stub = HostOs.FindLauncherStub();
        var plan = InstanceBadge.Plan(
            os,
            SampleProfile(),
            ordinal: 3,
            assetRoot: Path.Combine(outputDir, "branding"),
            canWriteAssets: writeAssets,
            stubExecutable: stub);

        Output.Item("Strategy", plan.Strategy.ToString());
        Output.Item("App / WM id", plan.AppId);
        Output.Item("Badge caption", $"\"{plan.BadgeText}\" (ordinal {plan.Ordinal})");
        Output.Item("Asset path", plan.AssetPath ?? "(none — nothing written)");
        Output.Item("Extra Chromium args", plan.Args.Count == 0 ? "(none)" : string.Join(" ", plan.Args));

        Output.Plain();
        if (plan.Strategy == BadgeStrategy.None) Output.Warn(plan.Reason);
        else Output.Ok(plan.Reason);

        ReportWindowsSpecifics(os, plan, stub);
        ReportLegibility();

        if (writeAssets) WriteSamples(os, outputDir);
    }

    /// <summary>
    /// The Windows-only detail: which of the two tiers is in play and why.
    /// <para>
    /// Printed because the difference is user-visible and permanent-looking. With
    /// a shim the badged icon is the window's real icon; with the overlay it is a
    /// small corner mark that vanishes when the window closes. A user who expected
    /// the first and got the second needs to be told it is a missing build
    /// artifact, not a broken feature.
    /// </para>
    /// </summary>
    private static void ReportWindowsSpecifics(BadgeOs os, BadgePlan plan, string? stub)
    {
        if (os != BadgeOs.Windows) return;

        Output.Plain();
        Output.Item("Launcher stub", stub ?? "(not shipped with this build)");
        Output.Item("Probed directory", AppContext.BaseDirectory);
        Output.Item("Taskbar overlay API", WindowsTaskbar.OverlaySupported ? "available" : "unavailable");

        if (plan.Strategy == BadgeStrategy.WindowsShim)
        {
            Output.Plain();
            Output.Ok("Full badging: each profile gets its own executable and taskbar identity.");
            return;
        }

        Output.Plain();
        Output.Warn("Running in the reduced Windows mode (taskbar overlay).");
        Output.Paragraph(
            "A per-profile executable carrying the badged icon cannot be produced at " +
            "runtime — a Windows PE has to be compiled, and the Hub does not ship a " +
            "compiler. The stub is therefore a build artifact that gets copied and " +
            "re-iconed per profile. This build does not contain one, so the number is " +
            "drawn as a taskbar overlay on the live window instead.");
        Output.Plain();
        Output.Paragraph("What you lose in overlay mode:");
        Output.Bullet("The badge marks the taskbar button, not the window's own icon.");
        Output.Bullet("It appears a moment after launch, once the window handle exists.");
        Output.Bullet("It disappears when the window closes rather than persisting.");
        Output.Bullet("All profiles still group under one taskbar entry.");
    }

    /// <summary>
    /// The legibility rule, as a table the user can check against the renders.
    /// <para>
    /// The renderer refuses to draw a caption it cannot draw legibly and falls back
    /// to a plain dot. That is deliberate, but from the outside it looks like a
    /// bug — "why is profile 12 showing a dot instead of a number?" — so the report
    /// states the rule and shows exactly which sizes are affected.
    /// </para>
    /// </summary>
    private static void ReportLegibility()
    {
        Output.Section("Badge legibility");

        Output.Paragraph(
            $"A caption is only drawn when its laid-out font size reaches " +
            $"{BadgeRenderer.MinLegibleEmPx:0.#}px. Below that a bold condensed digit " +
            "loses its stem or its counter to antialiasing and reads as a smudge, which " +
            "is worse than no number at all — so the renderer degrades to a plain dot " +
            "instead. The dot still distinguishes a Hub window from a stock browser.");
        Output.Plain();

        var captions = new[] { "1", "9", "12", "42", "99+" };
        var header = "  " + "caption".PadRight(10) + string.Join("", BadgeRenderer.IcoSizes.Select(s => $"{s,6}"));
        Output.Plain(header);
        Output.Plain("  " + new string('-', header.Length - 2));

        foreach (var caption in captions)
        {
            var cells = BadgeRenderer.IcoSizes
                .Select(size => BadgeRenderer.CaptionFits(caption, size) ? "num" : "dot")
                .Select(v => $"{v,6}");
            Output.Plain("  " + caption.PadRight(10) + string.Join("", cells));
        }

        Output.Plain();
        Output.Info("\"num\" draws the number; \"dot\" falls back to a solid dot at that size.");
        Output.Paragraph(
            "Only the smallest sizes ever fall back, and a .ico contains all of them — so " +
            "a two-digit profile shows a dot in a cramped taskbar and the real number " +
            "everywhere the icon is drawn larger.");
    }

    /// <summary>
    /// Render real assets to disk for visual inspection.
    /// <para>
    /// Writes both the host's native container and the individual PNGs. The PNGs
    /// are the point: an <c>.ico</c> or <c>.icns</c> can only be judged in a file
    /// manager at whatever size it picks, whereas a 16px PNG opened in an image
    /// viewer can be zoomed, which is the only way to actually check the small
    /// sizes that matter.
    /// </para>
    /// </summary>
    private static void WriteSamples(BadgeOs os, string outputDir)
    {
        Output.Section("Generated badge samples");

        var iconDir = Path.Combine(outputDir, "icons");
        var pngDir = Path.Combine(iconDir, "png");

        try
        {
            Directory.CreateDirectory(pngDir);
        }
        catch (Exception ex)
        {
            Output.Fail($"Cannot create {pngDir}: {ex.Message}");
            return;
        }

        var baseIcon = AppIcon.Bytes;
        var written = 0;
        long bytes = 0;

        foreach (var ordinal in SampleOrdinals)
        {
            var caption = InstanceBadge.TextFor(ordinal);
            var safe = caption.Replace("+", "plus");

            try
            {
                // The host's native container, so the user can drop it straight into
                // a shortcut (Windows) or a bundle (macOS) and see the real thing.
                switch (os)
                {
                    case BadgeOs.Windows:
                        bytes += Save(Path.Combine(iconDir, $"badge-{safe}.ico"),
                            BadgeRenderer.BuildIco(baseIcon, caption));
                        break;
                    case BadgeOs.MacOs:
                        bytes += Save(Path.Combine(iconDir, $"badge-{safe}.icns"),
                            IcnsWriter.Build(baseIcon, caption));
                        break;
                    default:
                        bytes += Save(Path.Combine(iconDir, $"badge-{safe}.png"),
                            BadgeRenderer.RenderPng(baseIcon, caption, 256));
                        break;
                }

                foreach (var size in BadgeRenderer.IcoSizes)
                    bytes += Save(Path.Combine(pngDir, $"badge-{safe}-{size}.png"),
                        BadgeRenderer.RenderPng(baseIcon, caption, size));

                written++;
            }
            catch (Exception ex)
            {
                // One bad ordinal must not abort the sheet: the remaining sizes are
                // still useful evidence, and the failure itself is information.
                Output.Fail($"Ordinal {ordinal} (\"{caption}\"): {ex.GetType().Name} — {ex.Message}");
            }
        }

        Output.Ok($"Wrote {written} badge set{(written == 1 ? "" : "s")} ({bytes:N0} bytes).");
        Output.Item("Native icons", iconDir);
        Output.Item("Individual PNGs", pngDir);
        Output.Plain();
        Output.Paragraph(
            "Open the 16px and 24px PNGs and zoom in — those are the sizes a taskbar and " +
            "a window switcher actually draw, and they are the ones worth judging. The " +
            "badge is a circle for one digit and a pill for two or three, because a " +
            "circle's usable width runs out at two digits while a pill can spend the " +
            "icon's full width.");

        WriteLaunchAssets(os, outputDir);
    }

    /// <summary>
    /// Exercise <see cref="BadgeAssetWriter"/> — the code that runs at launch.
    /// <para>
    /// Separate from the sample sheet because it proves something different. The
    /// sheet proves the badge is legible; this proves the platform plumbing works
    /// on this machine: that a <c>.desktop</c> file lands where the WM will read
    /// it, that a <c>.app</c> bundle gets its executable bit, that the target
    /// directory is writable at all under this user's permissions.
    /// </para>
    /// </summary>
    private static void WriteLaunchAssets(BadgeOs os, string outputDir)
    {
        Output.Section("Launch-time branding assets");

        var plan = InstanceBadge.Plan(
            os,
            SampleProfile(),
            ordinal: 3,
            assetRoot: Path.Combine(outputDir, "branding"),
            canWriteAssets: true,
            stubExecutable: HostOs.FindLauncherStub());

        if (plan.Strategy == BadgeStrategy.None)
        {
            Output.Info("Nothing to write for this strategy.");
            return;
        }

        // A plausible-looking path rather than a real browser: the writer only
        // embeds this string into a shim or launcher script, it never executes it,
        // so a fake path exercises exactly the same code as a real one.
        var browser = os == BadgeOs.Windows
            ? @"C:\Program Files\CloakBrowser\cloakbrowser.exe"
            : "/opt/cloakbrowser/cloakbrowser";

        var assets = new BadgeAssetWriter().Write(plan, browser, AppIcon.Bytes, "Doctor Sample");

        Output.Item("Files written", assets.Written.Count.ToString());
        Output.Item("Launch executable", assets.Executable ?? "(the browser, unchanged)");
        Output.Item("Extra args", assets.ExtraArgs.Count == 0 ? "(none)" : string.Join(" ", assets.ExtraArgs));
        Output.Item("Environment", assets.Environment.Count == 0
            ? "(none)"
            : string.Join(", ", assets.Environment.Select(kv => $"{kv.Key}={kv.Value}")));

        foreach (var path in assets.Written) Output.Plain($"    {path}");

        Output.Plain();
        if (assets.Written.Count == 0) Output.Warn(assets.Note);
        else Output.Ok(assets.Note);
    }

    private static long Save(string path, byte[] content)
    {
        File.WriteAllBytes(path, content);
        return content.Length;
    }

    /// <summary>
    /// A fixed profile, so two runs on two machines produce comparable output.
    /// <para>
    /// The id is hardcoded rather than generated because the AppUserModelID and the
    /// WM_CLASS are both derived from it. A random id would change those between
    /// runs, and the whole point of the report is to be diffable.
    /// </para>
    /// </summary>
    internal static Profile SampleProfile() => new()
    {
        Id = "doctor-sample-0001",
        Name = "Doctor Sample",
        SchemaVersion = 3,
    };
}
