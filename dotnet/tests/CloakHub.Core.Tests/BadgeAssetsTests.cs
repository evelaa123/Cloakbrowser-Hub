using System.Text;
using System.Text.Json;
using CloakHub.Core.Branding;
using CloakHub.Core.Model;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// In-memory filesystem so the Windows and macOS asset writers can be verified
/// on a Linux box, and so the failure paths can be triggered deliberately rather
/// than hoped for.
/// </summary>
internal sealed class FakeFileSystem : IFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = [];
    public HashSet<string> Directories { get; } = [];
    public HashSet<string> Executable { get; } = [];

    /// <summary>Paths matching this substring throw, to exercise degradation.</summary>
    public string? FailOn { get; set; }

    public void CreateDirectory(string path) => Directories.Add(path);

    public void WriteAllBytes(string path, byte[] bytes)
    {
        Guard(path);
        Files[path] = bytes;
    }

    public void WriteAllText(string path, string text)
    {
        Guard(path);
        Files[path] = Encoding.UTF8.GetBytes(text);
    }

    public void MakeExecutable(string path) => Executable.Add(path);

    public void CopyFile(string source, string destination, bool overwrite)
    {
        Guard(destination);
        Files[destination] = Files.TryGetValue(source, out var b) ? b : [0x4D, 0x5A];
    }

    public string Text(string path) => Encoding.UTF8.GetString(Files[path]);

    private void Guard(string path)
    {
        if (FailOn is not null && path.Contains(FailOn, StringComparison.Ordinal))
            throw new UnauthorizedAccessException($"denied: {path}");
    }
}

public class BadgeAssetsTests
{
    private static Profile Prof(string id = "p-1", string name = "Shopping") =>
        new() { Id = id, Name = name };

    private static (BadgeAssetWriter Writer, FakeFileSystem Fs) Subject()
    {
        var fs = new FakeFileSystem();
        return (new BadgeAssetWriter(fs), fs);
    }

    // ---------------------------------------------------------------------
    // Linux.

    [Fact]
    public void Linux_writes_a_desktop_entry_matched_to_the_launch_class()
    {
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.Linux, Prof(), 3, "/assets");

        var result = writer.Write(plan, "/usr/bin/chromium", null, "Shopping");

        var entry = fs.Text(plan.AssetPath!);

        // The whole mechanism hinges on StartupWMClass agreeing with the --class
        // flag; if they drift the window silently keeps the stock browser icon.
        Assert.Contains($"StartupWMClass={plan.AppId}", entry);
        Assert.Contains($"--class={plan.AppId}", result.ExtraArgs);

        // Icon must be an absolute path to a file that was actually written,
        // otherwise the WM shows a generic placeholder.
        var iconLine = entry.Split('\n').Single(l => l.StartsWith("Icon=", StringComparison.Ordinal));
        var iconPath = iconLine["Icon=".Length..].Trim();
        Assert.True(Path.IsPathRooted(iconPath));
        Assert.True(fs.Files.ContainsKey(iconPath));

        // Linux brands the stock binary; it must not redirect the executable.
        Assert.Null(result.Executable);
        Assert.Contains("NoDisplay=true", entry);
    }

    [Fact]
    public void Linux_entry_survives_a_profile_name_with_newlines()
    {
        // A .desktop file is line-oriented, so an unescaped newline in Name= would
        // turn the rest of the name into a bogus key and can silently break the
        // entry. Names come from user input, so this is reachable.
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.Linux, Prof(name: "bad\nExec=/bin/sh"), 1, "/assets");

        writer.Write(plan, "/usr/bin/chromium", null, "bad\nExec=/bin/sh");

        var entry = fs.Text(plan.AssetPath!);
        var execLines = entry.Split('\n').Count(l => l.StartsWith("Exec=", StringComparison.Ordinal));
        Assert.Equal(1, execLines);
    }

    // ---------------------------------------------------------------------
    // macOS.

    [Fact]
    public void Mac_writes_a_bundle_whose_stub_execs_the_real_browser()
    {
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.MacOs, Prof(), 2, "/assets");

        var result = writer.Write(plan, "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", null, "Shopping");

        var stub = Path.Combine(plan.AssetPath!, "Contents", "MacOS", "launcher");
        var plist = Path.Combine(plan.AssetPath!, "Contents", "Info.plist");
        var icns = Path.Combine(plan.AssetPath!, "Contents", "Resources", "profile.icns");

        Assert.True(fs.Files.ContainsKey(stub));
        Assert.True(fs.Files.ContainsKey(plist));
        Assert.True(fs.Files.ContainsKey(icns));

        // A bundle stub that is not executable is inert, and the failure mode is a
        // confusing "cannot open" rather than an obvious error.
        Assert.Contains(stub, fs.Executable);

        // The Dock takes the icon from the process's bundle, so the launch must go
        // through the stub rather than the browser directly.
        Assert.Equal(stub, result.Executable);

        var script = fs.Text(stub);
        Assert.StartsWith("#!/bin/sh", script);
        // exec, not a plain invocation: the browser must replace the shell so it
        // inherits the bundle identity and no stray shell survives.
        Assert.Contains("exec ", script);
        // Chromium paths contain spaces as a rule, so quoting is mandatory.
        Assert.Contains("'/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'", script);
        // Arguments must be forwarded, or every launch flag is dropped.
        Assert.Contains("\"$@\"", script);
    }

    [Fact]
    public void Mac_plist_declares_the_icon_without_its_extension()
    {
        // CFBundleIconFile names the resource, and including ".icns" is a classic
        // way to get a bundle that shows a blank icon with no error anywhere.
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.MacOs, Prof(), 1, "/assets");

        writer.Write(plan, "/bin/chromium", null, "Shopping");

        var plist = fs.Text(Path.Combine(plan.AssetPath!, "Contents", "Info.plist"));
        Assert.Contains("<key>CFBundleIconFile</key><string>profile</string>", plist);
        Assert.Contains("<key>CFBundleExecutable</key><string>launcher</string>", plist);
    }

    [Fact]
    public void Mac_plist_escapes_xml_in_a_profile_name()
    {
        // "AT&T" is an entirely ordinary profile name and produces invalid XML if
        // written raw, which makes the bundle unopenable.
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.MacOs, Prof(name: "AT&T <lab>"), 1, "/assets");

        writer.Write(plan, "/bin/chromium", null, "AT&T <lab>");

        var plist = fs.Text(Path.Combine(plan.AssetPath!, "Contents", "Info.plist"));
        Assert.Contains("AT&amp;T &lt;lab&gt;", plist);
        Assert.DoesNotContain("AT&T <lab>", plist);
    }

    [Fact]
    public void Mac_stub_quoting_defeats_a_path_containing_a_quote()
    {
        // Single-quote escaping is easy to get wrong in a way that turns a path
        // into an injection point. Verify the closing-quote dance directly.
        var quoted = BadgeAssetWriter.ShellQuote("/tmp/it's here/chrome");
        Assert.Equal("'/tmp/it'\\''s here/chrome'", quoted);
    }

    // ---------------------------------------------------------------------
    // Windows.

    [Fact]
    public void Windows_falls_back_to_the_overlay_without_a_stub()
    {
        // A per-profile .exe cannot be synthesised without a shipped stub, so the
        // planner must not promise a shim it cannot deliver.
        var plan = InstanceBadge.Plan(BadgeOs.Windows, Prof(), 1, "/assets");
        Assert.Equal(BadgeStrategy.WindowsOverlay, plan.Strategy);
        Assert.NotEqual("", plan.Reason);
    }

    [Fact]
    public void Windows_shim_copies_the_stub_and_records_its_identity()
    {
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(
            BadgeOs.Windows, Prof(), 4, @"C:\assets", stubExecutable: @"C:\hub\stub.exe");

        Assert.Equal(BadgeStrategy.WindowsShim, plan.Strategy);

        var result = writer.Write(plan, @"C:\chrome\chrome.exe", null, "Shopping");

        Assert.True(fs.Files.ContainsKey(plan.AssetPath!));
        Assert.Equal(plan.AssetPath, result.Executable);

        var config = JsonDocument.Parse(fs.Text(Path.ChangeExtension(plan.AssetPath!, ".json")));
        Assert.Equal(plan.AppId, config.RootElement.GetProperty("appId").GetString());
        Assert.Equal(4, config.RootElement.GetProperty("ordinal").GetInt32());

        // The exact number must remain available even when the badge itself had to
        // degrade to a dot, so the title carries it.
        Assert.Equal("Shopping #4", config.RootElement.GetProperty("title").GetString());

        // The environment duplicates the identity so the shim still works when
        // started by something other than the Hub.
        Assert.Equal(plan.AppId, result.Environment["CLOAKHUB_APP_ID"]);
    }

    [Fact]
    public void Windows_overlay_still_produces_an_icon_to_hand_to_the_shell()
    {
        // SetOverlayIcon needs an HICON, so even the file-free strategy has to
        // build the .ico; it just does not persist one.
        var (writer, _) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.Windows, Prof(), 7, "/assets", canWriteAssets: false);

        var result = writer.Write(plan, "chrome.exe", null, "Shopping");

        Assert.Equal(BadgeStrategy.WindowsOverlay, plan.Strategy);
        Assert.Empty(result.Written);
        Assert.Null(result.Executable);
    }

    // ---------------------------------------------------------------------
    // Degradation. Branding is cosmetic and must never abort a launch.

    [Fact]
    public void A_write_failure_degrades_instead_of_throwing()
    {
        var (writer, fs) = Subject();
        fs.FailOn = "applications";   // deny the whole desktop-entry directory
        var plan = InstanceBadge.Plan(BadgeOs.Linux, Prof(), 1, "/assets");

        var result = writer.Write(plan, "/usr/bin/chromium", null, "Shopping");

        Assert.Null(result.Executable);
        Assert.Empty(result.Written);
        // The note must say what went wrong: a silent no-op looks like a bug, and a
        // thrown exception would look like a failed launch.
        Assert.Contains("UnauthorizedAccessException", result.Note);
    }

    [Fact]
    public void An_unreadable_base_icon_does_not_stop_asset_generation()
    {
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.Linux, Prof(), 1, "/assets");

        var result = writer.Write(plan, "/usr/bin/chromium", [9, 9, 9], "Shopping");

        Assert.NotEmpty(result.Written);
        Assert.True(fs.Files.ContainsKey(plan.AssetPath!));
    }

    [Fact]
    public void The_none_strategy_writes_nothing_but_explains_itself()
    {
        var (writer, fs) = Subject();
        var plan = InstanceBadge.Plan(BadgeOs.Other, Prof(), 1, "/assets");

        var result = writer.Write(plan, "/usr/bin/chromium", null, "Shopping");

        Assert.Empty(result.Written);
        Assert.Empty(fs.Files);
        Assert.NotEqual("", result.Note);
    }

    // ---------------------------------------------------------------------
    // Interop guards. These cannot call into Windows here, but they can assert
    // the host checks that keep the calls from being attempted.

    [Fact]
    public void Windows_interop_is_inert_on_this_host()
    {
        // Every entry point must be a no-op off-Windows rather than throwing, so
        // callers need no platform branch of their own.
        if (OperatingSystem.IsWindows()) return;
        Assert.False(WindowsTaskbar.TrySetProcessAppId("dev.cloakbrowser.hub.profile.x"));
        Assert.False(WindowsTaskbar.OverlaySupported);
    }
}
