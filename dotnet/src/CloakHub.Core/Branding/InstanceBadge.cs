using System.Globalization;
using CloakHub.Core.Model;

namespace CloakHub.Core.Branding;

/// <summary>
/// Which mechanism gives a launched Chromium its own icon and instance number.
/// <para>
/// There is no cross-platform switch for this, so each OS gets the approach that
/// actually works there. Chromium has no <c>--icon</c> flag, and the window icon
/// comes from the executable's resources (Windows), the <c>.app</c> bundle
/// (macOS), or the WM_CLASS-to-<c>.desktop</c> association (Linux).
/// </para>
/// </summary>
public enum BadgeStrategy
{
    /// <summary>No badging attempted (or unavailable on this host).</summary>
    None,

    /// <summary>
    /// Windows: a per-profile launcher <c>.exe</c> whose resources carry the
    /// badged icon. It sets an explicit AppUserModelID and then starts the real
    /// Chromium, so the taskbar groups and labels the window under the shim
    /// rather than under the shared browser binary.
    /// </summary>
    WindowsShim,

    /// <summary>
    /// Windows fallback: keep the stock binary but attach an overlay to the live
    /// window via <c>ITaskbarList3::SetOverlayIcon</c>. Cheaper (no file to
    /// write) but the badge only exists while the window does, and it needs the
    /// window handle, so it cannot be applied before launch.
    /// </summary>
    WindowsOverlay,

    /// <summary>
    /// Linux: launch with <c>--class=&lt;wmclass&gt;</c> and install a matching
    /// <c>.desktop</c> file whose <c>Icon=</c> points at the badged PNG. The WM
    /// resolves the icon through <c>StartupWMClass</c>.
    /// </summary>
    LinuxDesktopEntry,

    /// <summary>
    /// macOS: a tiny <c>.app</c> bundle per profile with its own
    /// <c>CFBundleIconFile</c>, whose executable <c>exec</c>s the real binary.
    /// The Dock reads the icon from the bundle that owns the process.
    /// </summary>
    MacAppBundle,
}

/// <summary>
/// A resolved plan for branding one launched browser window.
/// <para>
/// Deliberately a plain data record produced by pure code: the platform-specific
/// side effects (writing a shim, calling into COM) live in the platform layer
/// and are driven by this plan, so the decision itself stays unit-testable.
/// </para>
/// </summary>
public sealed record BadgePlan
{
    public BadgeStrategy Strategy { get; init; } = BadgeStrategy.None;

    /// <summary>1-based ordinal shown on the icon.</summary>
    public int Ordinal { get; init; }

    /// <summary>Text drawn on the badge — the ordinal, or "99+" past the cap.</summary>
    public string BadgeText { get; init; } = "";

    /// <summary>
    /// Windows AppUserModelID / Linux WM_CLASS / macOS CFBundleIdentifier.
    /// Stable per profile so a relaunch reuses the same taskbar slot.
    /// </summary>
    public string AppId { get; init; } = "";

    /// <summary>Where the generated asset (icon, shim, bundle) should live.</summary>
    public string? AssetPath { get; init; }

    /// <summary>
    /// Windows only: the shipped launcher stub to copy to <see cref="AssetPath"/>.
    /// Null for every other strategy.
    /// </summary>
    public string? StubExecutable { get; init; }

    /// <summary>Extra Chromium flags this strategy requires.</summary>
    public List<string> Args { get; init; } = [];

    /// <summary>
    /// Human-readable note for the session log. A branding step that silently
    /// does nothing is worse than one that says why — the badge is cosmetic, so
    /// it must never look like a launch failure.
    /// </summary>
    public string Reason { get; init; } = "";
}

public static class InstanceBadge
{
    /// <summary>
    /// Highest ordinal drawn as a number. Beyond this the badge reads "99+",
    /// because three digits stop being legible at 16x16 — the size that actually
    /// matters in a taskbar.
    /// </summary>
    public const int MaxOrdinal = 99;

    /// <summary>
    /// Reverse-DNS-ish identifier for one profile's windows.
    /// <para>
    /// Windows requires an AppUserModelID of at most 128 characters with no
    /// spaces; Linux WM_CLASS should be a simple token. Sanitising to
    /// <c>[A-Za-z0-9._-]</c> and prefixing satisfies both, and deriving it from
    /// the profile id (not the name) keeps it stable when the user renames a
    /// profile — a renamed profile that jumps to a new taskbar group would look
    /// like a different app.
    /// </para>
    /// </summary>
    public static string AppIdFor(string profileId)
    {
        var chars = profileId
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            .ToArray();
        var token = new string(chars);
        if (token.Length == 0) token = "unknown";
        if (token.Length > 64) token = token[..64];
        return $"dev.cloakbrowser.hub.profile.{token}";
    }

    /// <summary>Badge caption for an ordinal.</summary>
    public static string TextFor(int ordinal) =>
        ordinal > MaxOrdinal
            ? $"{MaxOrdinal}+"
            : Math.Max(1, ordinal).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Pick the strategy for a host OS and build the plan.
    /// </summary>
    /// <param name="os">Target OS, injected so every branch is testable anywhere.</param>
    /// <param name="profile">Profile being launched.</param>
    /// <param name="ordinal">1-based position among running sessions.</param>
    /// <param name="assetRoot">Directory the Hub may write generated assets into.</param>
    /// <param name="canWriteAssets">
    /// False when asset generation is unavailable (read-only install, missing
    /// toolchain). The plan then degrades to the best in-process option rather
    /// than failing: on Windows that is the taskbar overlay, elsewhere no badge.
    /// </param>
    /// <param name="stubExecutable">
    /// Windows only: path to the shipped launcher stub. Null selects the overlay
    /// strategy, because a per-profile <c>.exe</c> cannot be produced without one.
    /// </param>
    public static BadgePlan Plan(
        BadgeOs os,
        Profile profile,
        int ordinal,
        string assetRoot,
        bool canWriteAssets = true,
        string? stubExecutable = null)
    {
        var appId = AppIdFor(profile.Id);
        var text = TextFor(ordinal);

        switch (os)
        {
            // The shim needs two things, and both are genuine preconditions rather
            // than nice-to-haves: somewhere to write, and a stub executable to copy
            // and re-icon. A PE cannot be synthesised at runtime without a
            // toolchain, so the stub is a build artifact shipped with the Hub. When
            // it is absent — a source checkout, a trimmed package — the overlay is
            // the correct answer, not a broken shim.
            case BadgeOs.Windows when canWriteAssets && stubExecutable is not null:
                return new BadgePlan
                {
                    Strategy = BadgeStrategy.WindowsShim,
                    Ordinal = ordinal,
                    BadgeText = text,
                    AppId = appId,
                    AssetPath = Path.Combine(assetRoot, "shims", $"{Sanitise(profile.Id)}.exe"),
                    StubExecutable = stubExecutable,
                    Reason = $"Per-profile launcher with badge \"{text}\" and AppUserModelID {appId}.",
                };

            case BadgeOs.Windows:
                return new BadgePlan
                {
                    Strategy = BadgeStrategy.WindowsOverlay,
                    Ordinal = ordinal,
                    BadgeText = text,
                    AppId = appId,
                    Reason = canWriteAssets
                        ? "No launcher stub is available, so the badge is applied as a taskbar overlay " +
                          "on the live window instead. The overlay disappears when the window closes."
                        : "Cannot write a launcher shim, so the badge is applied as a taskbar overlay on " +
                          "the live window instead. The overlay disappears when the window closes.",
                };

            case BadgeOs.Linux when canWriteAssets:
                // WM_CLASS must be a bare token; the desktop entry is matched to
                // it by StartupWMClass.
                var wmClass = $"cloakhub-{Sanitise(profile.Id)}";
                return new BadgePlan
                {
                    Strategy = BadgeStrategy.LinuxDesktopEntry,
                    Ordinal = ordinal,
                    BadgeText = text,
                    AppId = wmClass,
                    AssetPath = Path.Combine(assetRoot, "applications", $"{wmClass}.desktop"),
                    Args = [$"--class={wmClass}"],
                    Reason = $"Desktop entry {wmClass}.desktop with badge \"{text}\"; window matched via --class.",
                };

            case BadgeOs.MacOs when canWriteAssets:
                return new BadgePlan
                {
                    Strategy = BadgeStrategy.MacAppBundle,
                    Ordinal = ordinal,
                    BadgeText = text,
                    AppId = appId,
                    AssetPath = Path.Combine(assetRoot, "bundles", $"{Sanitise(profile.Id)}.app"),
                    Reason = $"Per-profile .app bundle with badge \"{text}\" as CFBundleIconFile.",
                };

            default:
                return new BadgePlan
                {
                    Strategy = BadgeStrategy.None,
                    Ordinal = ordinal,
                    BadgeText = text,
                    AppId = appId,
                    Reason = "Instance badging unavailable on this host; the browser keeps its stock icon.",
                };
        }
    }

    private static string Sanitise(string s)
    {
        var chars = s.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var token = new string(chars).Trim('-');
        return token.Length == 0 ? "profile" : (token.Length > 48 ? token[..48] : token);
    }
}

/// <summary>Host OS for badge planning, injectable for tests.</summary>
public enum BadgeOs { Windows, Linux, MacOs, Other }
