using CloakHub.Core.Network;
using Xunit;

namespace CloakHub.Core.Tests;

public class MacAddressTests
{
    [Fact]
    public void Generation_is_deterministic_for_a_seed()
    {
        // Matches the fingerprint seed doctrine: an address that changes every
        // launch is more suspicious than one that never changes, and it makes bug
        // reports impossible to reproduce.
        Assert.Equal(MacAddress.Generate(4242), MacAddress.Generate(4242));
        Assert.NotEqual(MacAddress.Generate(4242), MacAddress.Generate(4243));
    }

    [Fact]
    public void A_generated_address_is_a_valid_station_address()
    {
        // The multicast bit must be clear. A multicast MAC is invalid as an
        // interface address and drivers reject it — a common bug in naive
        // generators that only randomise six bytes.
        for (var seed = 10000; seed < 10200; seed++)
        {
            foreach (var realistic in new[] { true, false })
            {
                var parsed = MacAddress.TryParse(MacAddress.Generate(seed, realistic));
                Assert.NotNull(parsed);
                Assert.True(MacAddress.IsValidStationAddress(parsed!),
                    $"seed {seed} realistic={realistic} produced a multicast address");
            }
        }
    }

    [Fact]
    public void A_realistic_address_carries_a_known_vendor_prefix()
    {
        // A random 48-bit value has an unassigned OUI, which is as distinctive as
        // no spoof at all to anything that checks against the IEEE registry.
        for (var seed = 1; seed < 200; seed++)
        {
            var parsed = MacAddress.TryParse(MacAddress.Generate(seed, realisticVendor: true))!;
            Assert.NotNull(MacAddress.VendorOf(parsed));
        }
    }

    [Fact]
    public void A_non_vendor_address_is_explicitly_self_assigned()
    {
        // Without a real OUI the honest form is a locally-administered address:
        // unambiguously self-assigned rather than a forged claim to be Intel.
        for (var seed = 1; seed < 200; seed++)
        {
            var parsed = MacAddress.TryParse(MacAddress.Generate(seed, realisticVendor: false))!;
            Assert.True(MacAddress.IsLocallyAdministered(parsed));
            Assert.True(MacAddress.IsValidStationAddress(parsed));
        }
    }

    [Theory]
    [InlineData("00:1A:2B:3C:4D:5E")]
    [InlineData("00-1A-2B-3C-4D-5E")]
    [InlineData("001A.2B3C.4D5E")]
    [InlineData("001A2B3C4D5E")]
    [InlineData("00 1a 2b 3c 4d 5e")]
    public void Every_common_notation_parses_to_the_same_address(string text)
    {
        // Users paste from router pages, ipconfig and Cisco output interchangeably;
        // rejecting a valid address on formatting alone is a pointless obstacle.
        var parsed = MacAddress.TryParse(text);
        Assert.NotNull(parsed);
        Assert.Equal("00:1A:2B:3C:4D:5E", MacAddress.Format(parsed!));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00:1A:2B:3C:4D")]        // too short
    [InlineData("00:1A:2B:3C:4D:5E:6F")]  // too long
    [InlineData("00:1A:2B:3C:4D:ZZ")]     // not hex
    [InlineData("hello")]
    public void Junk_is_rejected_rather_than_coerced(string? text)
        => Assert.Null(MacAddress.TryParse(text));
}

public class MacSpoofPlanTests
{
    [Theory]
    [InlineData(BadgeOsLike.Linux)]
    [InlineData(BadgeOsLike.MacOs)]
    [InlineData(BadgeOsLike.Windows)]
    public void Every_supported_platform_states_that_websites_cannot_see_a_mac(BadgeOsLike os)
    {
        // The most likely way this feature harms a user is by letting them believe
        // a MAC change hides them from a website. The disclaimer is therefore part
        // of the contract, not UI copy that can drift away.
        var plan = MacSpoof.Plan(os, "eth0", "00:1A:2B:3C:4D:5E");

        Assert.True(plan.Supported);
        Assert.Contains("not visible to websites", plan.Explanation);
        Assert.Contains("does not alter your browser fingerprint", plan.Explanation);
    }

    [Theory]
    [InlineData(BadgeOsLike.Linux)]
    [InlineData(BadgeOsLike.MacOs)]
    [InlineData(BadgeOsLike.Windows)]
    public void Every_plan_requires_elevation_and_warns_about_losing_the_link(BadgeOsLike os)
    {
        var plan = MacSpoof.Plan(os, "eth0", "00:1A:2B:3C:4D:5E");

        Assert.True(plan.RequiresElevation);
        Assert.NotEmpty(plan.Command);
        // Dropping the connection is the consequence users are least prepared for,
        // especially over a remote session.
        Assert.Contains(plan.Warnings, w => w.Contains("drop", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("lose access", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(BadgeOsLike.Linux)]
    [InlineData(BadgeOsLike.MacOs)]
    [InlineData(BadgeOsLike.Windows)]
    public void A_revert_command_is_offered_when_the_original_is_known(BadgeOsLike os)
    {
        // An irreversible network change is not acceptable for a cosmetic feature.
        var plan = MacSpoof.Plan(os, "eth0", "00:1A:2B:3C:4D:5E", "AA:BB:CC:DD:EE:FF");
        Assert.NotEmpty(plan.RevertCommand);
    }

    [Fact]
    public void Linux_uses_ip_rather_than_deprecated_ifconfig()
    {
        // ifconfig is absent by default on current distributions.
        var plan = MacSpoof.Plan(BadgeOsLike.Linux, "wlan0", "00:1A:2B:3C:4D:5E");
        Assert.Contains("ip", plan.Command);
        Assert.DoesNotContain("ifconfig", plan.Command);
        Assert.Contains("wlan0", plan.Command);
    }

    [Fact]
    public void Windows_warns_that_the_change_persists_and_may_be_ignored()
    {
        // Two Windows-specific traps: it survives reboot unlike Linux, and many
        // drivers ignore the keyword while still reporting success.
        var plan = MacSpoof.Plan(BadgeOsLike.Windows, "Wi-Fi", "00:1A:2B:3C:4D:5E");

        Assert.Contains(plan.Warnings, w => w.Contains("persists", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, w => w.Contains("ignore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_strips_separators_because_the_registry_value_has_none()
    {
        var plan = MacSpoof.Plan(BadgeOsLike.Windows, "Ethernet", "00:1A:2B:3C:4D:5E");
        var command = string.Join(" ", plan.Command);
        Assert.Contains("001A2B3C4D5E", command);
    }

    [Fact]
    public void An_adapter_name_containing_a_quote_cannot_break_out_of_the_command()
    {
        // Adapter names are user-visible strings and can contain apostrophes; an
        // unescaped one would terminate the PowerShell literal early.
        var plan = MacSpoof.Plan(BadgeOsLike.Windows, "Bob's Wi-Fi", "00:1A:2B:3C:4D:5E");
        var command = string.Join(" ", plan.Command);
        Assert.Contains("Bob''s Wi-Fi", command);
    }

    [Fact]
    public void A_multicast_address_is_refused_with_a_reason()
    {
        var plan = MacSpoof.Plan(BadgeOsLike.Linux, "eth0", "01:00:5E:00:00:01");

        Assert.False(plan.Supported);
        Assert.Empty(plan.Command);
        Assert.Contains("multicast", plan.Explanation);
        // Even the refusal repeats the disclaimer, so a user who only ever sees an
        // error still learns what the feature does not do.
        Assert.Contains("not visible to websites", plan.Explanation);
    }

    [Fact]
    public void Garbage_input_and_unknown_platforms_produce_no_command()
    {
        Assert.False(MacSpoof.Plan(BadgeOsLike.Linux, "eth0", "nonsense").Supported);
        Assert.False(MacSpoof.Plan(BadgeOsLike.Linux, "", "00:1A:2B:3C:4D:5E").Supported);
        Assert.Empty(MacSpoof.Plan(BadgeOsLike.Other, "eth0", "00:1A:2B:3C:4D:5E").Command);
    }
}
