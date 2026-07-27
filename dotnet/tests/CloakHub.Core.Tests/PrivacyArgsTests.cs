using CloakHub.Core.Launch;
using CloakHub.Core.Model;

namespace CloakHub.Core.Tests;

public class PrivacyArgsTests
{
    private static Profile WithPorts(params int[] ports) => new()
    {
        Id = "p-1",
        Startup = new StartupConfig { BlockedPorts = [.. ports] },
    };

    [Fact]
    public void No_ports_means_no_flag()
    {
        Assert.Empty(PrivacyArgs.Build(new Profile { Id = "p" }));
    }

    [Fact]
    public void Blocked_ports_no_longer_emit_the_unsupported_resolver_flag()
    {
        // --host-resolver-rules is on Chromium's bad-flags list, so passing it
        // raised the "unsupported command-line flag" banner on every launch. That
        // banner costs ~40px of viewport, which desynchronises innerHeight from
        // the spoofed screen size and makes the profile MORE identifiable than
        // the localhost probe the flag was blocking.
        Assert.Empty(PrivacyArgs.Build(WithPorts(3389)));
    }

    [Fact]
    public void A_profile_with_blocked_ports_is_told_they_are_not_enforced()
    {
        // The setting is retained but inert. Saying so is the whole point: a
        // security control that silently does nothing is worse than an absent
        // one, because the user stops treating the risk as open.
        var notice = PrivacyArgs.PortBlockingNotice(WithPorts(3389, 5900));

        Assert.NotNull(notice);
        Assert.Contains("3389", notice);
        Assert.Contains("5900", notice);
    }

    [Fact]
    public void A_profile_without_blocked_ports_gets_no_notice()
    {
        Assert.Null(PrivacyArgs.PortBlockingNotice(new Profile { Id = "p" }));
    }

    [Fact]
    public void The_notice_lists_ports_in_a_stable_order()
    {
        // Same reasoning the argv preview had: a message that reorders between
        // renders is useless for comparing two profiles.
        Assert.Equal(
            PrivacyArgs.PortBlockingNotice(WithPorts(5900, 3389, 7070)),
            PrivacyArgs.PortBlockingNotice(WithPorts(7070, 3389, 5900)));
    }

    [Fact]
    public void Duplicate_ports_collapse()
    {
        Assert.Equal([3389], PrivacyArgs.NormalisePorts([3389, 3389, 3389]));
    }

    [Fact]
    public void Out_of_range_ports_are_dropped_rather_than_emitted()
    {
        // An argv the user can inspect must not contain entries that do nothing.
        Assert.Equal([1, 65535], PrivacyArgs.NormalisePorts([0, -5, 1, 65535, 65536, 99999]));
    }

    [Fact]
    public void Default_port_list_covers_the_strong_remote_access_signals()
    {
        // These imply a managed or farmed machine, which is the correlation that
        // matters most; the rest of the list is conventional.
        Assert.Contains(3389, PrivacyArgs.DefaultBlockedPorts);   // RDP
        Assert.Contains(5900, PrivacyArgs.DefaultBlockedPorts);   // VNC
        Assert.Contains(5938, PrivacyArgs.DefaultBlockedPorts);   // TeamViewer
        Assert.Contains(6568, PrivacyArgs.DefaultBlockedPorts);   // AnyDesk
    }

    [Fact]
    public void Default_port_list_has_no_duplicates_and_is_all_valid()
    {
        var normalised = PrivacyArgs.NormalisePorts(PrivacyArgs.DefaultBlockedPorts);
        Assert.Equal(PrivacyArgs.DefaultBlockedPorts.Distinct().Count(), normalised.Count);
    }

    [Fact]
    public void Do_not_track_is_off_by_default_because_most_real_users_have_it_off()
    {
        Assert.DoesNotContain("--enable-do-not-track", PrivacyArgs.Build(new Profile { Id = "p" }));
    }

    [Fact]
    public void Do_not_track_can_be_enabled()
    {
        var p = new Profile { Id = "p", Startup = new StartupConfig { DoNotTrack = true } };
        Assert.Contains("--enable-do-not-track", PrivacyArgs.Build(p));
    }
}

public class SandboxArgsTests
{
    [Fact]
    public void Non_linux_keeps_the_sandbox()
    {
        var d = SandboxArgs.Resolve(isLinux: false);
        Assert.False(d.Disabled);
        Assert.Empty(d.Args);
    }

    [Fact]
    public void Linux_with_user_namespaces_keeps_the_sandbox()
    {
        var d = SandboxArgs.Resolve(isLinux: true, new SandboxArgs.Probe
        {
            UsernsAllowed = () => true,
            Containerised = () => false,
        });
        Assert.False(d.Disabled);
        Assert.Empty(d.Args);
    }

    [Fact]
    public void An_unknown_userns_answer_is_treated_as_allowed()
    {
        // The sysctl is absent on kernels where the feature is unconditionally
        // on; guessing "blocked" would disable the sandbox on healthy machines.
        var d = SandboxArgs.Resolve(isLinux: true, new SandboxArgs.Probe
        {
            UsernsAllowed = () => null,
            Containerised = () => false,
        });
        Assert.False(d.Disabled);
    }

    [Fact]
    public void Blocked_user_namespaces_force_no_sandbox_with_the_infobar_suppressed()
    {
        var d = SandboxArgs.Resolve(isLinux: true, new SandboxArgs.Probe
        {
            UsernsAllowed = () => false,
            Containerised = () => false,
        });
        Assert.True(d.Disabled);
        Assert.Contains("--no-sandbox", d.Args);
        // --test-type is the flag that actually removes the yellow bar, and the
        // bar costs ~40px of innerHeight — a fingerprint inconsistency, not just
        // an eyesore. The two flags must always travel together.
        Assert.Contains("--test-type", d.Args);
    }

    [Fact]
    public void A_container_forces_no_sandbox()
    {
        var d = SandboxArgs.Resolve(isLinux: true, new SandboxArgs.Probe
        {
            UsernsAllowed = () => true,
            Containerised = () => true,
        });
        Assert.True(d.Disabled);
        Assert.Contains("--no-sandbox", d.Args);
        Assert.Contains("--test-type", d.Args);
    }

    [Fact]
    public void The_env_override_wins_over_every_probe()
    {
        var d = SandboxArgs.Resolve(isLinux: false, new SandboxArgs.Probe { ForceNoSandbox = true });
        Assert.True(d.Disabled);
        Assert.Contains("--no-sandbox", d.Args);
        Assert.Contains("--test-type", d.Args);
    }

    [Fact]
    public void Every_disabled_decision_explains_itself()
    {
        // A silently unsandboxed renderer is the outcome this module exists to
        // prevent; if it happens anyway the log must say why.
        foreach (var probe in new[]
        {
            new SandboxArgs.Probe { ForceNoSandbox = true },
            new SandboxArgs.Probe { UsernsAllowed = () => false },
            new SandboxArgs.Probe { UsernsAllowed = () => true, Containerised = () => true },
        })
        {
            var d = SandboxArgs.Resolve(isLinux: true, probe);
            Assert.True(d.Disabled);
            Assert.False(string.IsNullOrWhiteSpace(d.Reason));
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("maybe", false)]
    public void Env_override_parsing(string value, bool expected)
    {
        var env = new Dictionary<string, string> { ["CLOAKBROWSER_HUB_NO_SANDBOX"] = value };
        Assert.Equal(expected, SandboxArgs.NoSandboxOverride(env));
    }
}
