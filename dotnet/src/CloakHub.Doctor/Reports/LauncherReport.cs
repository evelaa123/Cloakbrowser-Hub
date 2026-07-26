using CloakHub.Core.Launch;
using CloakHub.Core.Licensing;
using CloakHub.Core.Platform;
using CloakHub.Doctor.Console;

namespace CloakHub.Doctor.Reports;

/// <summary>
/// Launch plumbing: the binary redirect, the licence key, the session limit.
/// <para>
/// These three are grouped because they are the things that stop a launch before
/// the browser is ever reached, and each of them fails in a way that is hard to
/// diagnose from the symptom alone: a redirect that silently pointed at the wrong
/// binary, a licence file the wrapper rejects for an invisible reason, a session
/// limit that looks like a crash when the fifth window refuses to open.
/// </para>
/// </summary>
public static class LauncherReport
{
    /// <summary>
    /// Async because the redirect probe genuinely is.
    /// <para>
    /// <see cref="BinaryOverride"/> is <c>IAsyncDisposable</c> and not
    /// <c>IDisposable</c>, which is not an oversight: releasing it restores an
    /// environment variable and frees a gate that is held across the browser
    /// launch's await, so there is no correct synchronous release. Blocking on it
    /// here with <c>GetAwaiter().GetResult()</c> would work in a console app but
    /// would set the wrong example for the UI, where that pattern deadlocks.
    /// </para>
    /// </summary>
    public static async Task RunAsync()
    {
        await ReportBinaryOverrideAsync();
        ReportLicence();
        ReportSessionLimit();
    }

    /// <summary>
    /// The environment-variable redirect, tested live.
    /// <para>
    /// Actually exercised rather than described, because the mechanism is unusual
    /// enough to deserve proof. The wrapper exposes no <c>ExecutablePath</c> and no
    /// <c>Env</c> on its options — established by reflecting over the assembly — so
    /// <c>CLOAKBROWSER_BINARY_PATH</c> is the only way to launch a shim or an
    /// <c>.app</c> bundle instead of the browser directly. Two of the three badge
    /// strategies depend on it.
    /// </para>
    /// </summary>
    private static async Task ReportBinaryOverrideAsync()
    {
        Output.Section("Browser binary redirect");

        Output.Item("Variable", BinaryOverride.EnvironmentVariable);
        var ambient = Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable);
        Output.Item("Currently set to", ambient ?? "(not set)");

        var probe = Path.Combine(Path.GetTempPath(), "cloakhub-doctor-probe");
        await using (await BinaryOverride.AcquireAsync(probe))
        {
            var during = Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable);
            if (during == probe) Output.Ok("Redirect applies while the gate is held.");
            else Output.Fail($"Expected {probe}, saw {during ?? "(null)"}. This is a bug.");
        }

        var after = Environment.GetEnvironmentVariable(BinaryOverride.EnvironmentVariable);
        if (after == ambient) Output.Ok("Previous value restored on release.");
        else Output.Fail($"Leaked: expected {ambient ?? "(null)"}, saw {after ?? "(null)"}.");

        Output.Plain();
        Output.Paragraph(
            "The redirect is process-wide, so concurrent launches are serialised behind a " +
            "gate held across each launch. Two profiles starting at once would otherwise " +
            "race and one could end up running the other's launcher — a wrong-binary " +
            "launch, which is a correctness failure, whereas a marginally slower launch " +
            "is not.");
    }

    /// <summary>
    /// The licence key file, if one is present.
    /// <para>
    /// The reader tolerates a BOM, CRLF and surrounding whitespace and reports
    /// whether the file needs repair. That leniency matters because the usual way a
    /// key arrives is a copy-paste into Notepad, which adds exactly those things,
    /// and the wrapper's rejection message does not mention them.
    /// </para>
    /// </summary>
    private static void ReportLicence()
    {
        Output.Section("Licence key");

        var path = Path.Combine(HostOs.HubDataDir(), "license.key");
        Output.Item("Expected at", path);

        byte[] bytes;
        try
        {
            if (!File.Exists(path))
            {
                Output.Info("No licence key file. The Hub runs with the free-tier session limit.");
                return;
            }
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Output.Fail($"Cannot read the file: {ex.Message}");
            return;
        }

        var (key, needsRepair) = LicenseKeyFile.ReadFile(bytes);

        // Only ever show the ends. A diagnostic report is the sort of thing users
        // paste into a support chat, and a full key in that paste is a leaked
        // credential.
        var masked = key.Length <= 8 ? new string('*', key.Length) : $"{key[..4]}...{key[^4..]}";
        Output.Item("Key", $"{masked} ({key.Length} characters)");

        if (needsRepair)
        {
            Output.Warn("The file has stray bytes (a BOM, CRLF or whitespace).");
            Output.Paragraph(
                "The Hub strips them when reading, so the key still works. Rewriting the " +
                "file in canonical form silences this.");
        }
        else
        {
            Output.Ok("The file is already in canonical form.");
        }
    }

    /// <summary>
    /// How many browsers can run at once, and which constraint decides it.
    /// <para>
    /// Both numbers are printed because the interesting case is the disagreement:
    /// a user who set 20 in preferences and can only open 5 needs to see that the
    /// plan is the binding limit, not the setting they already changed.
    /// </para>
    /// </summary>
    private static void ReportSessionLimit()
    {
        Output.Section("Concurrent sessions");

        var resolution = SessionLimit.Resolve(preference: null, planSeats: null);
        Output.Item("Effective limit", resolution.Limit.ToString());
        Output.Item("Your preference", resolution.Preference.ToString());
        Output.Item("Plan seats", resolution.PlanSeats?.ToString() ?? "(unknown)");
        Output.Item("Capped by plan", resolution.CappedByPlan ? "yes" : "no");
        Output.Plain();
        Output.Info($"Limited by {resolution.Reason}.");

        Output.Plain();
        Output.Paragraph(
            "This report cannot see your real plan — that needs an activated licence and a " +
            "server round-trip — so the numbers above are the defaults. When a licence is " +
            "active the lower of your preference and your plan's seat count wins, and the " +
            "Hub says which one applied rather than silently refusing to open a window.");
    }
}
