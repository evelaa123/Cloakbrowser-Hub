using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CloakHub.Core.Branding;

/// <summary>
/// Windows taskbar integration for instance badges.
/// <para>
/// Two mechanisms, matching the two Windows strategies in
/// <see cref="BadgeStrategy"/>:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>AppUserModelID</b> — the taskbar groups windows by this identifier and
/// resolves the pinned icon and label from it. Setting a distinct one per
/// profile is what stops five sessions collapsing into one Chrome button.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>SetOverlayIcon</b> — stamps a small icon onto the corner of an existing
/// taskbar button. Needs no files, but only lasts as long as the window and
/// requires the HWND, so it cannot be applied before launch.
/// </description>
/// </item>
/// </list>
/// <para>
/// Every entry point is a no-op on non-Windows hosts rather than throwing, so
/// callers need no platform branch of their own. The P/Invokes are isolated here
/// so the rest of the branding code stays portable and testable.
/// </para>
/// </summary>
public static class WindowsTaskbar
{
    /// <summary>
    /// Set the calling process's AppUserModelID.
    /// <para>
    /// Must be called before the process shows a window, which is why the
    /// per-profile shim does it as its first action and then starts Chromium: a
    /// child inherits the identity, and there is no way to set it on a process
    /// that is already up.
    /// </para>
    /// </summary>
    /// <returns>True if applied; false on a non-Windows host or on failure.</returns>
    public static bool TrySetProcessAppId(string appId)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(appId)) return false;

        // 128 characters including the terminator is the documented limit; a longer
        // string fails the call outright, so truncate rather than lose the grouping.
        var id = appId.Length > 127 ? appId[..127] : appId;

        try
        {
            return SetCurrentProcessExplicitAppUserModelID(id) >= 0;
        }
        catch (DllNotFoundException)
        {
            // shell32 is always present on a real Windows install; this only fires
            // under emulation layers such as Wine, where the badge is cosmetic
            // anyway.
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-Windows 7. The API simply does not exist there.
            return false;
        }
    }

    /// <summary>
    /// Whether the overlay API can be used at all on this host.
    /// <para>
    /// Separated out so the session manager can decide between the shim and the
    /// overlay without a try/catch driving control flow.
    /// </para>
    /// </summary>
    public static bool OverlaySupported =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version >= new Version(6, 1);

    /// <summary>
    /// Apply a badge overlay to a live window's taskbar button.
    /// </summary>
    /// <param name="hwnd">Target window handle.</param>
    /// <param name="icoBytes">Badge as a Windows <c>.ico</c>.</param>
    /// <param name="description">Accessibility text describing the overlay.</param>
    /// <param name="tempDirectory">
    /// Where the icon may be staged. <c>LoadImage</c> reads from a file, and
    /// building an HICON from memory means hand-rolling a DIB, so writing the
    /// bytes out is the smaller and better-understood step.
    /// </param>
    [SupportedOSPlatform("windows")]
    public static bool TrySetOverlay(IntPtr hwnd, byte[] icoBytes, string description, string tempDirectory)
    {
        if (!OverlaySupported || hwnd == IntPtr.Zero || icoBytes.Length == 0) return false;

        string? path = null;
        var icon = IntPtr.Zero;
        ITaskbarList3? taskbar = null;

        try
        {
            Directory.CreateDirectory(tempDirectory);
            path = Path.Combine(tempDirectory, $"overlay-{Guid.NewGuid():N}.ico");
            File.WriteAllBytes(path, icoBytes);

            // 16x16: the overlay is drawn small regardless, and asking for the exact
            // size avoids Windows scaling a larger frame down and softening the
            // digit we worked to keep legible.
            icon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
            if (icon == IntPtr.Zero) return false;

            taskbar = (ITaskbarList3)new TaskbarInstance();
            taskbar.HrInit();
            taskbar.SetOverlayIcon(hwnd, icon, description);
            return true;
        }
        catch (Exception)
        {
            // COM activation, a shell that is not running, or a window that died
            // between the check and the call. All cosmetic.
            return false;
        }
        finally
        {
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (taskbar is not null) Marshal.ReleaseComObject(taskbar);
            // The shell copies the icon during SetOverlayIcon, so the staging file
            // is no longer needed once the call has returned.
            if (path is not null)
            {
                try { File.Delete(path); } catch (Exception) { /* temp file; ignore */ }
            }
        }
    }

    /// <summary>Remove a previously applied overlay.</summary>
    [SupportedOSPlatform("windows")]
    public static bool TryClearOverlay(IntPtr hwnd)
    {
        if (!OverlaySupported || hwnd == IntPtr.Zero) return false;

        ITaskbarList3? taskbar = null;
        try
        {
            taskbar = (ITaskbarList3)new TaskbarInstance();
            taskbar.HrInit();
            taskbar.SetOverlayIcon(hwnd, IntPtr.Zero, null);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (taskbar is not null) Marshal.ReleaseComObject(taskbar);
        }
    }

    // -----------------------------------------------------------------------
    // Interop.

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    [DllImport("shell32.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(
        IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// Minimal <c>ITaskbarList3</c> declaration.
    /// <para>
    /// Only the three methods used are given real signatures; the rest are
    /// placeholders that must still be declared, and declared in order, because a
    /// COM interface is a vtable and omitting an entry would silently shift every
    /// later slot and call the wrong function.
    /// </para>
    /// </summary>
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, int tbpFlags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, IntPtr pButtons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string? pszDescription);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? pszTip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
    }

    [ComImport]
    [Guid("56fdf344-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarInstance;
}
