using System.Text;

namespace CloakHub.Core.Branding;

/// <summary>
/// The files a <see cref="BadgePlan"/> produced, and how to launch with them.
/// </summary>
/// <param name="Written">Absolute paths created, for logging and cleanup.</param>
/// <param name="Executable">
/// Program the session manager should start. Null means "use the browser
/// executable unchanged" — the plan brands the window some other way.
/// </param>
/// <param name="ExtraArgs">Flags the strategy needs appended to the launch.</param>
/// <param name="Environment">Environment variables the child process needs.</param>
/// <param name="Note">What actually happened, for the session log.</param>
public sealed record BadgeAssets(
    IReadOnlyList<string> Written,
    string? Executable,
    IReadOnlyList<string> ExtraArgs,
    IReadOnlyDictionary<string, string> Environment,
    string Note)
{
    public static BadgeAssets Nothing(string note) =>
        new([], null, [], new Dictionary<string, string>(), note);
}

/// <summary>
/// Writes the per-instance branding assets described by a <see cref="BadgePlan"/>.
/// <para>
/// Split from <see cref="InstanceBadge"/> on purpose: choosing the strategy is
/// pure and unit-tested for every OS, while this class touches the filesystem.
/// The split is what lets the decision be verified on a Linux CI box for the
/// Windows and macOS branches too.
/// </para>
/// <para>
/// Every method here treats failure as non-fatal. Branding is cosmetic: a
/// read-only install, a full disk or a locked file must degrade to an unbadged
/// window with an explanatory note, never abort the launch the user asked for.
/// </para>
/// </summary>
public sealed class BadgeAssetWriter(IFileSystem? fs = null)
{
    private readonly IFileSystem _fs = fs ?? new PhysicalFileSystem();

    /// <summary>
    /// Materialise the plan.
    /// </summary>
    /// <param name="plan">Strategy and paths, from <see cref="InstanceBadge.Plan"/>.</param>
    /// <param name="browserExecutable">Real Chromium binary the shim must invoke.</param>
    /// <param name="baseIcon">Source app icon PNG, or null to draw the badge alone.</param>
    /// <param name="profileName">Shown in the taskbar / Dock / switcher.</param>
    public BadgeAssets Write(
        BadgePlan plan,
        string browserExecutable,
        byte[]? baseIcon,
        string profileName)
    {
        try
        {
            return plan.Strategy switch
            {
                BadgeStrategy.LinuxDesktopEntry => WriteLinux(plan, browserExecutable, baseIcon, profileName),
                BadgeStrategy.MacAppBundle => WriteMac(plan, browserExecutable, baseIcon, profileName),
                BadgeStrategy.WindowsShim => WriteWindowsShim(plan, baseIcon, profileName),
                BadgeStrategy.WindowsOverlay => WriteWindowsIcon(plan, baseIcon),
                _ => BadgeAssets.Nothing(plan.Reason),
            };
        }
        catch (Exception ex)
        {
            // Deliberately broad. The set of things that can fail while writing to
            // an arbitrary user directory is open-ended (permissions, quota, path
            // length, antivirus locks), and none of them justify refusing to open
            // a browser. The note carries the reason to the session log.
            return BadgeAssets.Nothing(
                $"Instance badge skipped: {ex.GetType().Name} while writing branding assets " +
                $"({ex.Message}). The browser starts with its stock icon.");
        }
    }

    // -----------------------------------------------------------------------
    // Linux: .desktop entry keyed to WM_CLASS.

    private BadgeAssets WriteLinux(
        BadgePlan plan, string browserExecutable, byte[]? baseIcon, string profileName)
    {
        if (plan.AssetPath is null) return BadgeAssets.Nothing(plan.Reason);

        var dir = Path.GetDirectoryName(plan.AssetPath)!;
        _fs.CreateDirectory(dir);

        // The icon goes next to the entry as a plain PNG. Freedesktop icon themes
        // want a sized directory tree, but Icon= also accepts an absolute path,
        // which avoids installing into the user's theme and needing a cache
        // refresh — the WM picks it up on the next window map either way.
        var iconPath = Path.ChangeExtension(plan.AssetPath, ".png");
        _fs.WriteAllBytes(iconPath, BadgeRenderer.RenderPng(baseIcon, plan.BadgeText, 256));

        // StartupWMClass is the hinge: it associates the window Chromium creates
        // under --class=<wmClass> with this entry, which is where the icon and the
        // display name come from. Without it the WM falls back to the browser's
        // own entry and the badge is never shown.
        var entry = new StringBuilder()
            .AppendLine("[Desktop Entry]")
            .AppendLine("Type=Application")
            .AppendLine("Version=1.0")
            .AppendLine($"Name={Escape(DisplayName(profileName, plan.Ordinal))}")
            .AppendLine($"Icon={iconPath}")
            .AppendLine($"Exec={Escape(browserExecutable)}")
            .AppendLine($"StartupWMClass={plan.AppId}")
            .AppendLine("NoDisplay=true")   // a launch helper, not a menu item
            .AppendLine("Terminal=false")
            .ToString();

        _fs.WriteAllText(plan.AssetPath, entry);

        return new BadgeAssets(
            Written: [plan.AssetPath, iconPath],
            Executable: null,          // the stock binary, plus --class
            ExtraArgs: plan.Args,
            Environment: new Dictionary<string, string>(),
            Note: plan.Reason);
    }

    // -----------------------------------------------------------------------
    // macOS: .app bundle whose executable execs the real browser.

    private BadgeAssets WriteMac(
        BadgePlan plan, string browserExecutable, byte[]? baseIcon, string profileName)
    {
        if (plan.AssetPath is null) return BadgeAssets.Nothing(plan.Reason);

        var contents = Path.Combine(plan.AssetPath, "Contents");
        var macOs = Path.Combine(contents, "MacOS");
        var resources = Path.Combine(contents, "Resources");
        _fs.CreateDirectory(macOs);
        _fs.CreateDirectory(resources);

        var icnsPath = Path.Combine(resources, "profile.icns");
        _fs.WriteAllBytes(icnsPath, IcnsWriter.Build(baseIcon, plan.BadgeText));

        var plistPath = Path.Combine(contents, "Info.plist");
        _fs.WriteAllText(plistPath, MacPlist(plan, profileName));

        // The bundle executable is a shell stub rather than a compiled binary so
        // the Hub can generate it on any host without a toolchain. exec (not a
        // plain call) matters twice over: the browser inherits the bundle's
        // process identity, which is what makes the Dock show the badged icon,
        // and no shell lingers to confuse process bookkeeping.
        var stubPath = Path.Combine(macOs, "launcher");
        _fs.WriteAllText(stubPath, MacStub(browserExecutable));
        _fs.MakeExecutable(stubPath);

        return new BadgeAssets(
            Written: [plan.AssetPath, plistPath, icnsPath, stubPath],
            Executable: stubPath,
            ExtraArgs: plan.Args,
            Environment: new Dictionary<string, string>(),
            Note: plan.Reason);
    }

    private static string MacPlist(BadgePlan plan, string profileName) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>CFBundleName</key><string>{XmlEscape(DisplayName(profileName, plan.Ordinal))}</string>
          <key>CFBundleDisplayName</key><string>{XmlEscape(DisplayName(profileName, plan.Ordinal))}</string>
          <key>CFBundleIdentifier</key><string>{XmlEscape(plan.AppId)}</string>
          <key>CFBundleExecutable</key><string>launcher</string>
          <key>CFBundleIconFile</key><string>profile</string>
          <key>CFBundlePackageType</key><string>APPL</string>
          <key>CFBundleVersion</key><string>1</string>
          <key>CFBundleShortVersionString</key><string>1.0</string>
          <key>LSMinimumSystemVersion</key><string>10.13</string>
          <!-- Not a background app: the window must appear in the Dock, which is
               the whole point of the bundle. -->
          <key>LSUIElement</key><false/>
        </dict>
        </plist>

        """;

    private static string MacStub(string browserExecutable) =>
        $"""
        #!/bin/sh
        # Generated by CloakBrowser Hub. Gives one browser window its own Dock
        # icon by owning the process identity of a per-profile bundle.
        exec {ShellQuote(browserExecutable)} "$@"

        """;

    // -----------------------------------------------------------------------
    // Windows: the badged .ico both strategies need.

    /// <summary>
    /// Copy the launcher stub to a per-profile <c>.exe</c> and give it the badged
    /// icon and its AppUserModelID.
    /// <para>
    /// The icon is not patched into the PE resource table. Rewriting
    /// <c>RT_GROUP_ICON</c> in place means recomputing section sizes and
    /// checksums, and getting it subtly wrong yields an executable Windows
    /// refuses to start — a cosmetic feature must not be able to do that. Instead
    /// the stub reads its icon and identity from a sidecar config file next to it,
    /// which is inspectable, repairable by hand, and cannot corrupt a binary.
    /// </para>
    /// </summary>
    private BadgeAssets WriteWindowsShim(BadgePlan plan, byte[]? baseIcon, string profileName)
    {
        if (plan.AssetPath is null || plan.StubExecutable is null)
            return BadgeAssets.Nothing(plan.Reason);

        var dir = Path.GetDirectoryName(plan.AssetPath)!;
        _fs.CreateDirectory(dir);

        var icoPath = Path.ChangeExtension(plan.AssetPath, ".ico");
        _fs.WriteAllBytes(icoPath, BadgeRenderer.BuildIco(baseIcon, plan.BadgeText));

        _fs.CopyFile(plan.StubExecutable, plan.AssetPath, overwrite: true);

        // The stub is generic; everything profile-specific lives here. Written as
        // JSON rather than as command-line arguments baked into a shortcut so the
        // values survive a user copying the shim somewhere else.
        var configPath = Path.ChangeExtension(plan.AssetPath, ".json");
        _fs.WriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(new
        {
            appId = plan.AppId,
            icon = icoPath,
            title = DisplayName(profileName, plan.Ordinal),
            ordinal = plan.Ordinal,
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return new BadgeAssets(
            Written: [plan.AssetPath, icoPath, configPath],
            Executable: plan.AssetPath,
            ExtraArgs: plan.Args,
            // The stub also honours the environment, which is what makes the shim
            // work when it is started by something other than the Hub.
            Environment: new Dictionary<string, string>
            {
                ["CLOAKHUB_APP_ID"] = plan.AppId,
                ["CLOAKHUB_ICON"] = icoPath,
            },
            Note: plan.Reason);
    }

    private BadgeAssets WriteWindowsIcon(BadgePlan plan, byte[]? baseIcon)
    {
        // The overlay strategy has no AssetPath because it patches a live window,
        // but it still needs an icon to hand to ITaskbarList3, so both branches
        // write the .ico and differ only in what consumes it.
        var target = plan.AssetPath is null
            ? null
            : Path.ChangeExtension(plan.AssetPath, ".ico");

        var ico = BadgeRenderer.BuildIco(baseIcon, plan.BadgeText);

        if (target is null)
            return new BadgeAssets([], null, plan.Args, new Dictionary<string, string>(), plan.Reason);

        _fs.CreateDirectory(Path.GetDirectoryName(target)!);
        _fs.WriteAllBytes(target, ico);

        return new BadgeAssets(
            Written: [target],
            Executable: null,
            ExtraArgs: plan.Args,
            Environment: new Dictionary<string, string>(),
            Note: plan.Reason);
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Window label. The ordinal is included because it is the one place the
    /// exact number survives even when the badge had to degrade to a dot.
    /// </summary>
    internal static string DisplayName(string profileName, int ordinal)
    {
        var name = string.IsNullOrWhiteSpace(profileName) ? "Profile" : profileName.Trim();
        return $"{name} #{ordinal}";
    }

    /// <summary>Escape a value for a freedesktop .desktop file.</summary>
    private static string Escape(string value) => value
        .Replace("\\", "\\\\")
        .Replace("\n", "\\n")
        .Replace("\r", "")
        .Replace("\t", "\\t");

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    /// <summary>
    /// Single-quote a path for /bin/sh. Chromium install paths routinely contain
    /// spaces ("Google Chrome.app"), so an unquoted exec would split the word and
    /// the bundle would fail to launch anything.
    /// </summary>
    public static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\\''") + "'";
}

/// <summary>
/// Filesystem seam, so asset writing can be tested without touching a disk and
/// so the failure paths can be exercised deliberately.
/// </summary>
public interface IFileSystem
{
    void CreateDirectory(string path);
    void WriteAllBytes(string path, byte[] bytes);
    void WriteAllText(string path, string text);
    void MakeExecutable(string path);
    void CopyFile(string source, string destination, bool overwrite);
}

public sealed class PhysicalFileSystem : IFileSystem
{
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllBytes(string path, byte[] bytes) => File.WriteAllBytes(path, bytes);

    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);

    public void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);

    public void MakeExecutable(string path)
    {
        // No-op on Windows, where executability is not a file mode. Guarded rather
        // than attempted-and-caught so a genuine chmod failure on Unix still
        // surfaces instead of hiding behind a platform check.
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
