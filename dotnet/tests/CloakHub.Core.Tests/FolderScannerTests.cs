using System.Text.Json;
using CloakHub.Core.Import;

namespace CloakHub.Core.Tests;

/// <summary>
/// The scanner's failures are all silent by construction.
/// <para>
/// A scan that misses a real profile returns a perfectly ordinary "nothing found"
/// page, and the user concludes their browser is not supported. A scan that walks
/// into a cache directory returns the right answer eventually and looks like a hang.
/// Neither raises anything. So the cases here are the layouts that must be found,
/// the ones that must not be offered, and the bounds that stop a walk running away.
/// </para>
/// </summary>
public class FolderScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"cloakhub-scan-test-{Guid.NewGuid():N}");

    public FolderScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // A leftover temp directory is not worth failing a test run over.
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Layouts that must be found
    // ------------------------------------------------------------------

    [Fact]
    public void A_chromium_profile_directly_inside_the_picked_folder_is_found()
    {
        // The user picks the profile folder itself. A scanner that only ever looks at
        // children would return nothing here and read as "your browser is not
        // supported".
        Chromium(_root, "Work");

        var scan = FolderScanner.Scan(_root);

        Assert.Single(scan.Profiles);

        // Contains, not equals. The label carries the browser as a suffix -- "Work —
        // Chrome" -- so that two profiles both called "Default", from Chrome and from
        // Brave, stay distinguishable in one result list. Asserting the bare label
        // here would pin down a decision this test is not about.
        Assert.Contains("Work", scan.Profiles[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_under_a_user_data_parent_are_found()
    {
        // The far more common case: the user picks "User Data", not one profile.
        Chromium(Path.Combine(_root, "Default"), "Default");
        Chromium(Path.Combine(_root, "Profile 1"), "Second");

        var scan = FolderScanner.Scan(_root);

        Assert.Equal(2, scan.Profiles.Count);
        Assert.Contains(scan.Profiles, p => p.Name.Contains("Default", StringComparison.Ordinal));
        Assert.Contains(scan.Profiles, p => p.Name.Contains("Second", StringComparison.Ordinal));
    }

    [Fact]
    public void A_profile_several_folders_down_is_still_found()
    {
        // Users pick a backup folder, or their home directory. Anything within the
        // depth budget has to be reached, or the scan is only useful to someone who
        // already knew the exact path.
        Chromium(Path.Combine(_root, "backup", "chrome", "User Data", "Default"), "Deep");

        var scan = FolderScanner.Scan(_root);

        Assert.Single(scan.Profiles);
        Assert.Contains("Deep", scan.Profiles[0].Name, StringComparison.Ordinal);
    }

    [Fact]
    public void A_firefox_profile_is_found_by_its_own_marker()
    {
        // Firefox has no Preferences file. A scanner written against Chromium alone
        // reports "nothing found" for a directory that plainly holds a profile.
        var dir = Path.Combine(_root, "abc123.default-release");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "prefs.js"), "// prefs");

        var scan = FolderScanner.Scan(_root);

        Assert.Single(scan.Profiles);
        Assert.Equal(ProfileFamily.Firefox, scan.Profiles[0].Family);
    }

    // ------------------------------------------------------------------
    // Cookie detection — the field that decides whether the import is worth doing
    // ------------------------------------------------------------------

    [Fact]
    public void Cookies_are_detected_in_both_places_chromium_puts_them()
    {
        // Modern Chromium moved the jar to Network/Cookies. A check that only looks
        // at the old location reports "None" for a profile full of live sessions,
        // and the user imports without the one thing they wanted.
        var legacy = Path.Combine(_root, "Legacy");
        Chromium(legacy, "Legacy");
        File.WriteAllText(Path.Combine(legacy, "Cookies"), "");

        var modern = Path.Combine(_root, "Modern");
        Chromium(modern, "Modern");
        Directory.CreateDirectory(Path.Combine(modern, "Network"));
        File.WriteAllText(Path.Combine(modern, "Network", "Cookies"), "");

        var scan = FolderScanner.Scan(_root);

        Assert.All(scan.Profiles, p => Assert.True(p.HasCookies));
    }

    [Fact]
    public void A_profile_without_a_cookie_jar_reports_none_rather_than_assuming()
    {
        Chromium(Path.Combine(_root, "Fresh"), "Fresh");

        var scan = FolderScanner.Scan(_root);

        Assert.False(scan.Profiles[0].HasCookies);
    }

    // ------------------------------------------------------------------
    // Things that must not be offered
    // ------------------------------------------------------------------

    [Fact]
    public void The_scanner_does_not_descend_into_a_profile_it_has_already_matched()
    {
        // A profile's own subfolders are never profiles, and walking Cache or
        // IndexedDB is where a scan goes to die -- a warm profile holds tens of
        // thousands of cache files. The symptom is a hang, not an error.
        var profile = Path.Combine(_root, "Default");
        Chromium(profile, "Default");
        Chromium(Path.Combine(profile, "Cache", "Nested"), "Should not appear");

        var scan = FolderScanner.Scan(_root);

        Assert.Single(scan.Profiles);
        Assert.Contains("Default", scan.Profiles[0].Name, StringComparison.Ordinal);
        Assert.DoesNotContain(scan.Profiles, p => p.Path.Contains("Cache", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_folder_explains_what_to_look_for_instead_of_saying_nothing()
    {
        // "No profiles found" with no guidance is indistinguishable from a broken
        // scanner. Naming the marker file lets the user check the folder themselves.
        var scan = FolderScanner.Scan(_root);

        Assert.Empty(scan.Profiles);
        Assert.NotNull(scan.Note);
        Assert.Contains("Preferences", scan.Note);
    }

    // ------------------------------------------------------------------
    // Bad input
    // ------------------------------------------------------------------

    [Fact]
    public void A_missing_folder_reports_the_reason_rather_than_an_empty_result()
    {
        var scan = FolderScanner.Scan(Path.Combine(_root, "nope"));

        Assert.Empty(scan.Profiles);
        Assert.NotNull(scan.Note);
    }

    [Fact]
    public void A_file_picked_instead_of_a_folder_says_so()
    {
        // Distinct from "does not exist": the user picked something real and needs to
        // know why it was refused, or they will pick it again.
        var file = Path.Combine(_root, "profile.zip");
        File.WriteAllText(file, "x");

        var scan = FolderScanner.Scan(file);

        Assert.Empty(scan.Profiles);
        Assert.Contains("file", scan.Note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_folder_at_all_is_handled_rather_than_throwing()
    {
        // The picker returns null when cancelled, and that reaches this method.
        Assert.Empty(FolderScanner.Scan(null).Profiles);
        Assert.Empty(FolderScanner.Scan("").Profiles);
        Assert.Empty(FolderScanner.Scan("   ").Profiles);
    }

    // ------------------------------------------------------------------
    // Labelling
    // ------------------------------------------------------------------

    [Fact]
    public void The_label_prefers_the_name_the_browser_shows_over_the_folder_name()
    {
        // "Profile 3" is what the folder is called; "Marketing" is what the user
        // knows it as. Offering the folder name makes the picker unusable for anyone
        // with more than two profiles.
        Chromium(Path.Combine(_root, "Profile 3"), "Marketing");

        var scan = FolderScanner.Scan(_root);

        Assert.Contains("Marketing", scan.Profiles[0].Name);
    }

    [Fact]
    public void A_profile_whose_preferences_are_corrupt_still_appears()
    {
        // Falling back to the folder name matters: a profile with unreadable
        // Preferences is exactly the one a user is trying to rescue, and dropping it
        // from the list gives them no way to.
        var dir = Path.Combine(_root, "Profile 9");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Preferences"), "{ this is not json");

        var scan = FolderScanner.Scan(_root);

        Assert.Single(scan.Profiles);
        Assert.Contains("Profile 9", scan.Profiles[0].Name);
    }

    /// <summary>Create a Chromium profile folder with the given display name.</summary>
    private static void Chromium(string dir, string displayName)
    {
        Directory.CreateDirectory(dir);

        var prefs = new { profile = new { name = displayName } };
        File.WriteAllText(Path.Combine(dir, "Preferences"), JsonSerializer.Serialize(prefs));
    }
}
