using CloakHub.Core.Branding;
using CloakHub.Core.Model;

namespace CloakHub.Core.Tests;

public class OrdinalAllocatorTests
{
    [Fact]
    public void Hands_out_one_two_three()
    {
        var a = new OrdinalAllocator();
        Assert.Equal(1, a.Acquire());
        Assert.Equal(2, a.Acquire());
        Assert.Equal(3, a.Acquire());
    }

    [Fact]
    public void Reuses_the_lowest_freed_slot()
    {
        // The badge answers "how many are open and which is which", so the
        // visible set must stay 1..n. A monotonic counter would show 1,3,4 after
        // closing the second window, which answers nothing.
        var a = new OrdinalAllocator();
        a.Acquire(); a.Acquire(); a.Acquire();   // 1 2 3
        a.Release(2);
        Assert.Equal(2, a.Acquire());
    }

    [Fact]
    public void Fills_the_lowest_gap_not_the_most_recent()
    {
        var a = new OrdinalAllocator();
        for (var i = 0; i < 5; i++) a.Acquire();   // 1..5
        a.Release(4);
        a.Release(2);
        Assert.Equal(2, a.Acquire());
        Assert.Equal(4, a.Acquire());
        Assert.Equal(6, a.Acquire());
    }

    [Fact]
    public void Releasing_an_unheld_ordinal_is_a_no_op()
    {
        // Teardown runs on several paths — explicit stop, crash, app exit — so a
        // double release must not throw during cleanup and mask the real failure.
        var a = new OrdinalAllocator();
        a.Release(7);
        a.Release(7);
        Assert.Equal(1, a.Acquire());
    }

    [Fact]
    public void Clear_frees_everything()
    {
        var a = new OrdinalAllocator();
        a.Acquire(); a.Acquire();
        a.Clear();
        Assert.Empty(a.InUse);
        Assert.Equal(1, a.Acquire());
    }

    [Fact]
    public void In_use_reflects_current_holdings()
    {
        var a = new OrdinalAllocator();
        a.Acquire(); a.Acquire(); a.Acquire();
        a.Release(2);
        Assert.Equal([1, 3], a.InUse);
    }
}

public class InstanceBadgeTests
{
    private static Profile P(string id = "abc-123") => new() { Id = id, Name = "Test" };

    [Fact]
    public void Badge_text_is_the_ordinal()
    {
        Assert.Equal("1", InstanceBadge.TextFor(1));
        Assert.Equal("42", InstanceBadge.TextFor(42));
    }

    [Fact]
    public void Badge_text_caps_because_three_digits_are_illegible_at_16px()
    {
        Assert.Equal("99", InstanceBadge.TextFor(99));
        Assert.Equal("99+", InstanceBadge.TextFor(100));
        Assert.Equal("99+", InstanceBadge.TextFor(5000));
    }

    [Fact]
    public void Badge_text_never_shows_zero_or_negative()
    {
        Assert.Equal("1", InstanceBadge.TextFor(0));
        Assert.Equal("1", InstanceBadge.TextFor(-3));
    }

    [Fact]
    public void App_id_is_derived_from_the_id_so_renaming_keeps_the_taskbar_group()
    {
        var a = InstanceBadge.AppIdFor("abc-123");
        var b = InstanceBadge.AppIdFor("abc-123");
        Assert.Equal(a, b);
        Assert.StartsWith("dev.cloakbrowser.hub.profile.", a);
    }

    [Fact]
    public void App_id_strips_characters_windows_rejects()
    {
        // AppUserModelID allows no spaces and a limited character set.
        var id = InstanceBadge.AppIdFor("my profile/../x!@#");
        Assert.DoesNotContain(' ', id);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('!', id);
    }

    [Fact]
    public void App_id_stays_within_the_windows_length_limit()
    {
        var id = InstanceBadge.AppIdFor(new string('x', 500));
        Assert.True(id.Length <= 128, $"AppUserModelID must be <= 128 chars, was {id.Length}");
    }

    [Fact]
    public void App_id_survives_an_id_with_no_usable_characters()
    {
        var id = InstanceBadge.AppIdFor("!!!///");
        Assert.EndsWith("unknown", id);
    }

    // ---------------------------------------------------------------------
    // Per-OS strategy. Each branch is asserted on its own so a change to one
    // platform cannot quietly alter another.
    // ---------------------------------------------------------------------

    [Fact]
    public void Windows_prefers_a_launcher_shim_when_a_stub_is_available()
    {
        // The stub argument is required, and that is the point: a per-profile .exe
        // is a copy of a shipped binary, because a PE cannot be synthesised at
        // runtime without a toolchain. This test previously passed no stub and
        // still expected a shim, which asserted a promise the writer could not
        // keep.
        var plan = InstanceBadge.Plan(
            BadgeOs.Windows, P(), 3, "/assets", stubExecutable: @"C:\hub\stub.exe");

        Assert.Equal(BadgeStrategy.WindowsShim, plan.Strategy);
        Assert.Equal("3", plan.BadgeText);
        Assert.Contains("shims", plan.AssetPath);
        Assert.EndsWith(".exe", plan.AssetPath);
        Assert.Equal(@"C:\hub\stub.exe", plan.StubExecutable);
    }

    [Fact]
    public void Windows_degrades_to_an_overlay_when_no_stub_is_shipped()
    {
        // Reachable in a source checkout or a trimmed package. The overlay is a
        // real badge, so this is a degradation rather than a loss of the feature.
        var plan = InstanceBadge.Plan(BadgeOs.Windows, P(), 3, "/assets");

        Assert.Equal(BadgeStrategy.WindowsOverlay, plan.Strategy);
        Assert.Null(plan.StubExecutable);
        Assert.Contains("overlay", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Windows_degrades_to_a_taskbar_overlay_when_assets_cannot_be_written()
    {
        // A read-only install must still badge the window rather than give up:
        // the overlay needs no file, only the live HWND.
        var plan = InstanceBadge.Plan(BadgeOs.Windows, P(), 2, "/assets", canWriteAssets: false);
        Assert.Equal(BadgeStrategy.WindowsOverlay, plan.Strategy);
        Assert.Null(plan.AssetPath);
        Assert.Contains("overlay", plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Linux_uses_a_desktop_entry_and_passes_class()
    {
        var plan = InstanceBadge.Plan(BadgeOs.Linux, P("abc-123"), 1, "/assets");
        Assert.Equal(BadgeStrategy.LinuxDesktopEntry, plan.Strategy);
        Assert.Equal("cloakhub-abc-123", plan.AppId);
        Assert.Contains("--class=cloakhub-abc-123", plan.Args);
        Assert.EndsWith(".desktop", plan.AssetPath);
    }

    [Fact]
    public void Linux_wm_class_is_a_bare_token()
    {
        // WM_CLASS with a space or slash would not match StartupWMClass, so the
        // icon would silently fall back to the stock one.
        var plan = InstanceBadge.Plan(BadgeOs.Linux, P("my profile/x"), 1, "/assets");
        Assert.DoesNotContain(' ', plan.AppId);
        Assert.DoesNotContain('/', plan.AppId);
    }

    [Fact]
    public void MacOs_uses_an_app_bundle()
    {
        var plan = InstanceBadge.Plan(BadgeOs.MacOs, P(), 4, "/assets");
        Assert.Equal(BadgeStrategy.MacAppBundle, plan.Strategy);
        Assert.EndsWith(".app", plan.AssetPath);
    }

    [Fact]
    public void Unknown_os_degrades_to_no_badge_with_a_stated_reason()
    {
        var plan = InstanceBadge.Plan(BadgeOs.Other, P(), 1, "/assets");
        Assert.Equal(BadgeStrategy.None, plan.Strategy);
        Assert.NotEmpty(plan.Reason);
    }

    [Fact]
    public void Linux_and_macos_do_not_silently_badge_when_assets_are_unavailable()
    {
        // Unlike Windows there is no in-process fallback on these platforms, so
        // the honest outcome is None with an explanation, not a plan that
        // pretends to work.
        foreach (var os in new[] { BadgeOs.Linux, BadgeOs.MacOs })
        {
            var plan = InstanceBadge.Plan(os, P(), 1, "/assets", canWriteAssets: false);
            Assert.Equal(BadgeStrategy.None, plan.Strategy);
            Assert.NotEmpty(plan.Reason);
        }
    }

    [Fact]
    public void Every_plan_carries_a_reason_for_the_session_log()
    {
        // Branding is cosmetic; a silent no-op must never look like a launch
        // failure, so the log always gets a sentence.
        foreach (var os in Enum.GetValues<BadgeOs>())
        foreach (var writable in new[] { true, false })
        {
            var plan = InstanceBadge.Plan(os, P(), 1, "/assets", writable);
            Assert.False(string.IsNullOrWhiteSpace(plan.Reason), $"{os}/{writable} had no reason");
        }
    }

    [Fact]
    public void Ordinal_is_carried_through_to_the_plan()
    {
        var plan = InstanceBadge.Plan(BadgeOs.Windows, P(), 7, "/assets");
        Assert.Equal(7, plan.Ordinal);
        Assert.Equal("7", plan.BadgeText);
    }
}
