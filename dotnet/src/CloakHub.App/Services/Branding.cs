using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CloakHub.App.Services;

/// <summary>
/// Loads the application's own icon for the window and taskbar.
/// <para>
/// The icon is set from code rather than with <c>Icon="/Assets/app-icon.png"</c> in
/// the XAML because a missing or unreadable asset in that attribute throws during
/// <c>InitializeComponent</c>, which takes down the whole window before anything is
/// on screen. An app with no icon is a cosmetic fault; an app that will not open is
/// not, and the two must not share a failure path.
/// </para>
/// <para>
/// Note that this covers the <i>running</i> window only. The icon Explorer and the
/// taskbar show for the executable itself comes from the Win32 resource compiled in
/// via <c>&lt;ApplicationIcon&gt;</c>, which is a separate mechanism and is why both
/// exist.
/// </para>
/// </summary>
public static class Branding
{
    /// <summary>The window/taskbar icon, as an Avalonia resource URI.</summary>
    public const string IconUri = "avares://CloakBrowserHub/Assets/app-icon.png";

    /// <summary>The wordmark-free cloak mark used in the sidebar.</summary>
    public const string MarkUri = "avares://CloakBrowserHub/Assets/cloak-mark.png";

    private static WindowIcon? _icon;
    private static bool _iconAttempted;

    private static Bitmap? _mark;
    private static bool _markAttempted;

    /// <summary>
    /// The window icon, or null when the asset could not be loaded.
    /// <para>
    /// Cached: a <c>WindowIcon</c> is immutable and decoding the PNG once per window
    /// is wasted work, but more to the point every caller gets the same instance so
    /// two windows can never disagree about the app's identity.
    /// </para>
    /// </summary>
    public static WindowIcon? Icon
    {
        get
        {
            if (_iconAttempted) return _icon;
            _iconAttempted = true;

            try
            {
                using var stream = AssetLoader.Open(new Uri(IconUri));
                _icon = new WindowIcon(stream);
            }
            catch (Exception ex)
            {
                // Swallowed to a log line, deliberately. See the class remarks: the
                // window must still open.
                CrashLog.Note($"Could not load the window icon from {IconUri}: {ex.Message}");
                _icon = null;
            }

            return _icon;
        }
    }

    /// <summary>The sidebar mark, or null when unavailable.</summary>
    public static Bitmap? Mark
    {
        get
        {
            if (_markAttempted) return _mark;
            _markAttempted = true;

            try
            {
                using var stream = AssetLoader.Open(new Uri(MarkUri));
                _mark = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                CrashLog.Note($"Could not load the sidebar mark from {MarkUri}: {ex.Message}");
                _mark = null;
            }

            return _mark;
        }
    }

    /// <summary>Apply the icon to a window, doing nothing when it is unavailable.</summary>
    public static void Apply(Window window)
    {
        if (Icon is { } icon) window.Icon = icon;
    }
}
