using System.Runtime.InteropServices;
using CloakHub.Core.Launch;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// Binary resolution.
/// <para>
/// Worth testing hard because the failure mode is quiet: picking the wrong cached
/// build launches a browser whose version does not match what the user believes
/// they are running, and version is part of the fingerprint.
/// </para>
/// </summary>
public sealed class ChromiumBinaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cloakhub-binary-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* the temp directory is the OS's problem after this */ }
    }

    /// <summary>Create a cache entry complete with a runnable-looking executable.</summary>
    private string Build(string name)
    {
        var dir = Path.Combine(_root, name);
        var exe = ChromiumBinary.ExecutableIn(dir);
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "#!/bin/sh\n");
        return exe;
    }

    private Dictionary<string, string> Env(params (string Key, string Value)[] pairs)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChromiumBinary.CacheVariable] = _root,
        };
        foreach (var (key, value) in pairs) env[key] = value;
        return env;
    }

    // ------------------------------------------------------------------
    // Version ordering
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("chromium-140.0.7339.207", new[] { 140, 0, 7339, 207 })]
    [InlineData("chromium-99.0.1.2", new[] { 99, 0, 1, 2 })]
    [InlineData("chromium-140.0.7339.207-pro", new[] { 140, 0, 7339, 207 })]
    [InlineData("chromium-131.0", new[] { 131, 0 })]
    public void Parses_the_version_out_of_a_cache_directory_name(string dir, int[] expected) =>
        Assert.Equal(expected, ChromiumBinary.ParseVersion(dir));

    [Theory]
    [InlineData("chromium-148.0.7778.215.2-pro", new[] { 148, 0, 7778, 215, 2 })]
    [InlineData("chromium-150.0.7871.114.3-pro", new[] { 150, 0, 7871, 114, 3 })]
    public void Parses_the_five_component_versions_CloakBrowser_actually_ships(
        string dir, int[] expected)
    {
        // The shipped naming carries a fifth component -- the stealth patch level --
        // and System.Version accepts at most four. Every real directory name failed
        // to parse, so this is the case the production code lives on, not an edge.
        Assert.Equal(expected, ChromiumBinary.ParseVersion(dir));
    }

    [Fact]
    public void An_unparseable_directory_name_sorts_last_rather_than_throwing()
    {
        // A stray directory in the cache -- a partial download, a user's own folder --
        // must not take the whole resolution down with it.
        Assert.Empty(ChromiumBinary.ParseVersion("chromium-nightly"));
    }

    [Fact]
    public void A_trailing_non_numeric_component_stops_the_parse_without_losing_the_prefix()
    {
        // "148.0-rc1" splits into "148" and "0-rc1", so the parse keeps 148 and
        // stops -- the point is that the recognisable prefix survives and the
        // suffix contributes nothing, not that every component is recovered.
        Assert.Equal(new[] { 148 }, ChromiumBinary.ParseVersion("chromium-148.0-rc1"));

        // Truncation costs no ordering accuracy here: absent components compare as
        // zero, so the short parse still ranks exactly where 148.0 would.
        Assert.Equal(0, ChromiumBinary.CompareVersions(
            ChromiumBinary.ParseVersion("chromium-148.0-rc1"),
            ChromiumBinary.ParseVersion("chromium-148.0")));
        Assert.True(ChromiumBinary.CompareVersions(
            ChromiumBinary.ParseVersion("chromium-148.0-rc1"),
            ChromiumBinary.ParseVersion("chromium-147.9")) > 0);
    }

    [Fact]
    public void Picks_the_highest_version_not_the_alphabetically_last()
    {
        // The regression this guards: as strings, "99" > "140", so a naive comparison
        // downgrades the user by 41 major versions the moment the major hits three
        // digits -- which it already has.
        Build("chromium-99.0.1.2");
        var newest = Build("chromium-140.0.7339.207");

        var resolved = ChromiumBinary.Resolve(Env());

        Assert.True(resolved.Found);
        Assert.Equal(newest, resolved.Path);
    }

    [Fact]
    public void Launches_the_newer_build_when_both_versions_have_five_components()
    {
        // Reported from the field: the settings page showed 148 installed and
        // announced 150 as available, and launching still started 148. The banner
        // compares version strings for inequality so it was right; the resolver
        // sorted on a parse that failed identically for both, so every candidate
        // tied and enumeration order decided. Same shape as the screenshot.
        Build("chromium-148.0.7778.215.2-pro");
        var newest = Build("chromium-150.0.7871.114.3-pro");

        Assert.Equal(newest, ChromiumBinary.Resolve(Env()).Path);
    }

    [Fact]
    public void A_fifth_component_makes_a_build_newer_than_the_same_build_without_one()
    {
        // 148.0.7778.215.2 is a respin of 148.0.7778.215, not a different release,
        // so the one carrying the patch level has to win.
        Build("chromium-148.0.7778.215");
        var patched = Build("chromium-148.0.7778.215.2");

        Assert.Equal(patched, ChromiumBinary.Resolve(Env()).Path);
    }

    [Fact]
    public void Prefers_a_pro_build_over_a_newer_standard_one()
    {
        // Someone holding a licence expects to be running the build they paid for,
        // even when a newer free build happens to be sitting beside it. The tiers
        // are different patch sets rather than two points on one timeline, so
        // crossing between them to gain a version would move a profile onto a
        // fingerprint surface it was never built against.
        var pro = Build("chromium-131.0.0.1-pro");
        Build("chromium-140.0.7339.207");

        var resolved = ChromiumBinary.Resolve(Env());

        Assert.Equal(pro, resolved.Path);
    }

    [Fact]
    public void Ignores_a_directory_whose_executable_is_missing()
    {
        // An interrupted download leaves the directory but not the binary. Selecting
        // it would fail at launch with a confusing error instead of falling through
        // to the build that actually works.
        Directory.CreateDirectory(Path.Combine(_root, "chromium-141.0.0.0"));
        var usable = Build("chromium-140.0.7339.207");

        Assert.Equal(usable, ChromiumBinary.Resolve(Env()).Path);
    }

    // ------------------------------------------------------------------
    // Overrides
    // ------------------------------------------------------------------

    [Fact]
    public void An_explicit_path_wins_over_a_newer_cached_build()
    {
        var pinned = Build("chromium-100.0.0.0");
        Build("chromium-140.0.7339.207");

        var resolved = ChromiumBinary.Resolve(
            Env((ChromiumBinary.PathVariable, pinned)));

        Assert.Equal(pinned, resolved.Path);
    }

    [Fact]
    public void An_explicit_path_that_does_not_exist_is_an_error_not_a_fallback()
    {
        // Falling back would silently launch a different browser than the one the
        // user pinned, which defeats the point of pinning it.
        Build("chromium-140.0.7339.207");

        var missing = Path.Combine(_root, "nowhere", "chrome");
        var resolved = ChromiumBinary.Resolve(
            Env((ChromiumBinary.PathVariable, missing)));

        Assert.False(resolved.Found);
        Assert.Contains(ChromiumBinary.PathVariable, resolved.Error);
        Assert.Contains(missing, resolved.Error);
    }

    [Fact]
    public void The_cache_directory_can_be_relocated()
    {
        var exe = Build("chromium-140.0.7339.207");
        Assert.Equal(exe, ChromiumBinary.Resolve(Env()).Path);
    }

    [Fact]
    public void Defaults_to_a_dot_cloakbrowser_folder_in_the_home_directory()
    {
        var cache = ChromiumBinary.CacheDir(new Dictionary<string, string>(StringComparer.Ordinal));
        Assert.Equal(".cloakbrowser", Path.GetFileName(cache));
    }

    // ------------------------------------------------------------------
    // Not-installed messaging
    // ------------------------------------------------------------------

    [Fact]
    public void A_missing_cache_reports_where_it_looked()
    {
        var resolved = ChromiumBinary.Resolve(Env());

        Assert.False(resolved.Found);
        Assert.Contains(_root, resolved.Error);
    }

    [Fact]
    public void An_empty_cache_reports_the_install_command()
    {
        // First run is the most common way to reach this branch, so the message has
        // to be an instruction rather than a diagnosis.
        Directory.CreateDirectory(_root);

        var resolved = ChromiumBinary.Resolve(Env());

        Assert.False(resolved.Found);
        Assert.Contains("cloakbrowser install", resolved.Error);
    }

    // ------------------------------------------------------------------
    // Platform layout
    // ------------------------------------------------------------------

    [Fact]
    public void The_executable_name_matches_the_host_platform()
    {
        var path = ChromiumBinary.ExecutableIn("/builds/chromium-140");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Assert.Contains(Path.Combine("Chromium.app", "Contents", "MacOS", "Chromium"), path);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Assert.EndsWith("chrome.exe", path);
        else
            Assert.EndsWith("chrome", path);
    }
}
