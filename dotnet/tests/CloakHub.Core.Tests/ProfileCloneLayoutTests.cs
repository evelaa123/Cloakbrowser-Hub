using CloakHub.Core.Import;

namespace CloakHub.Core.Tests;

/// <summary>
/// Tests for where a cloned profile's session data lands.
/// <para>
/// These exist because profile import silently did nothing. Chromium separates
/// <c>--user-data-dir</c> (the container) from the profile inside it
/// (<c>Default/</c>). The cloner wrote <c>Cookies</c>, <c>Login Data</c> and the
/// rest into the container root, while the browser read one level down — found
/// an empty profile, created a fresh one, and started logged out.
/// </para>
/// <para>
/// Nothing failed loudly: the import reported success and the megabytes it had
/// copied. The only symptom was a profile that was not signed in to anything,
/// which is indistinguishable from importing a profile that was already signed
/// out. That is what makes it worth pinning here.
/// </para>
/// </summary>
public class ProfileCloneLayoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "cloakhub-clone-" + Guid.NewGuid().ToString("N")[..8]);

    private string Source
    {
        get
        {
            var dir = Path.Combine(_root, "source");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* a temp dir we cannot remove is not a test failure */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_clone_target_is_the_profile_subdirectory_not_the_user_data_root()
    {
        // The single assertion that would have caught the bug.
        Assert.Equal(
            Path.Combine("/hub/profiles/abc", "Default"),
            ProfileCloner.TargetFor("/hub/profiles/abc"));
    }

    [Fact]
    public void The_target_matches_the_directory_the_browser_is_told_to_read()
    {
        // ChromiumLauncher passes --profile-directory=<this>. If the two ever
        // disagree again, imported profiles go back to starting logged out.
        Assert.EndsWith(
            ProfileCloner.ChromiumProfileDir,
            ProfileCloner.TargetFor("/hub/profiles/abc"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Session_files_land_where_chromium_will_look_for_them()
    {
        var source = Source;
        File.WriteAllText(Path.Combine(source, "Cookies"), "cookie-bytes");
        File.WriteAllText(Path.Combine(source, "Login Data"), "logins");

        var userDataDir = Path.Combine(_root, "hub-profile");
        var result = ProfileCloner.Clone(source, ProfileCloner.TargetFor(userDataDir));

        Assert.True(result.Ok, result.Error);

        // Present one level down, where the browser reads it...
        Assert.True(File.Exists(Path.Combine(userDataDir, "Default", "Cookies")));
        Assert.True(File.Exists(Path.Combine(userDataDir, "Default", "Login Data")));

        // ...and absent from the root, where it used to be stranded.
        Assert.False(File.Exists(Path.Combine(userDataDir, "Cookies")));
    }

    [Fact]
    public void Nested_session_directories_keep_their_structure()
    {
        // Local Storage is a directory, and a flattened copy is as useless as a
        // misplaced one.
        var source = Source;
        var leveldb = Path.Combine(source, "Local Storage", "leveldb");
        Directory.CreateDirectory(leveldb);
        File.WriteAllText(Path.Combine(leveldb, "000003.log"), "data");

        var userDataDir = Path.Combine(_root, "hub-profile");
        Assert.True(ProfileCloner.Clone(source, ProfileCloner.TargetFor(userDataDir)).Ok);

        Assert.True(File.Exists(
            Path.Combine(userDataDir, "Default", "Local Storage", "leveldb", "000003.log")));
    }

    [Fact]
    public void A_source_with_nothing_worth_copying_fails_rather_than_reporting_success()
    {
        // An empty "success" is what made the original bug invisible, so the
        // no-op case must stay loud.
        var source = Source;
        File.WriteAllText(Path.Combine(source, "Cache"), "not session state");

        var result = ProfileCloner.Clone(source, ProfileCloner.TargetFor(Path.Combine(_root, "hub")));

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }
}
