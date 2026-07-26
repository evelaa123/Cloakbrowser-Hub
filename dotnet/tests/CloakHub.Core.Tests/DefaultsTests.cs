using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Storage;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>
/// The value pools and the factory that draws from them.
/// <para>
/// These tests are not asserting "randomness works". They pin the two properties
/// that make a generated fingerprint useful rather than harmful: every pool must
/// cover every platform (a missing key is a crash on the one OS nobody tested),
/// and every drawn combination must be one a real machine could report. A
/// fingerprint that contradicts itself — macOS with a Direct3D renderer, German in
/// Tokyo — is <i>more</i> identifying than the honest values it replaced, so
/// coherence is the property worth testing.
/// </para>
/// </summary>
public class DefaultsTests
{
    // ---------------------------------------------------------------------
    // Pool coverage — a missing key is an exception at profile creation.
    //
    // Every platform-parameterised test below lists all three explicitly rather
    // than looping the enum, so adding a platform makes the omission a visible
    // diff here instead of a silently unexercised branch.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(FingerprintPlatform.Windows)]
    [InlineData(FingerprintPlatform.Macos)]
    [InlineData(FingerprintPlatform.Linux)]
    public void Every_platform_has_screens_gpus_and_platform_versions(FingerprintPlatform platform)
    {
        Assert.NotEmpty(Pools.Screens[platform]);
        Assert.NotEmpty(Pools.Gpus[platform]);
        Assert.NotEmpty(Pools.PlatformVersions[platform]);
    }

    [Fact]
    public void The_pools_cover_exactly_the_platforms_the_model_defines()
    {
        // Adding a platform to the enum without extending the pools would only fail
        // when a user happened to pick it, on their machine, at profile creation.
        var declared = Enum.GetValues<FingerprintPlatform>().Length;

        Assert.Equal(declared, Pools.Screens.Count);
        Assert.Equal(declared, Pools.Gpus.Count);
        Assert.Equal(declared, Pools.PlatformVersions.Count);
    }

    // ---------------------------------------------------------------------
    // Coherence — the reason the pools are keyed by platform and stored as pairs.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mac_gpus_never_claim_a_direct3d_renderer()
    {
        // A machine reporting macOS with an "...D3D11" renderer describes a computer
        // that cannot exist, and that one contradiction tells a detector more than
        // the real values would have.
        Assert.All(
            Pools.Gpus[FingerprintPlatform.Macos],
            g => Assert.DoesNotContain("D3D11", g.Renderer, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Windows_gpus_are_all_angle_over_direct3d()
    {
        // Chrome on Windows renders WebGL through ANGLE over D3D11; a bare OpenGL
        // renderer string there is the tell.
        Assert.All(Pools.Gpus[FingerprintPlatform.Windows], g =>
        {
            Assert.StartsWith("ANGLE (", g.Renderer, StringComparison.Ordinal);
            Assert.Contains("D3D11", g.Renderer, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Apple_vendors_only_appear_on_macos()
    {
        foreach (var (platform, gpus) in Pools.Gpus)
        {
            if (platform == FingerprintPlatform.Macos) continue;
            Assert.All(gpus, g => Assert.DoesNotContain("Apple", g.Vendor, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(FingerprintPlatform.Windows)]
    [InlineData(FingerprintPlatform.Macos)]
    [InlineData(FingerprintPlatform.Linux)]
    public void Every_gpu_pair_names_both_a_vendor_and_a_renderer(FingerprintPlatform platform)
    {
        // A blank half reaches the page as an empty WEBGL_debug_renderer_info string,
        // which is itself unusual enough to stand out.
        Assert.All(Pools.Gpus[platform], g =>
        {
            Assert.False(string.IsNullOrWhiteSpace(g.Vendor));
            Assert.False(string.IsNullOrWhiteSpace(g.Renderer));
        });
    }

    [Theory]
    [InlineData(FingerprintPlatform.Windows)]
    [InlineData(FingerprintPlatform.Macos)]
    [InlineData(FingerprintPlatform.Linux)]
    public void Every_screen_is_landscape_and_plausibly_sized(FingerprintPlatform platform)
    {
        Assert.All(Pools.Screens[platform], s =>
        {
            Assert.True(s.Width >= s.Height, $"{s.Width}x{s.Height} is not landscape.");
            Assert.InRange(s.Width, 1024, 7680);
            Assert.InRange(s.Height, 600, 4320);
        });
    }

    [Fact]
    public void Macos_screens_avoid_pc_only_panel_sizes()
    {
        // 1366x768 and 1536x864 are PC laptop panels Apple has never shipped, so a
        // Mac reporting one describes a configuration that does not exist.
        Assert.DoesNotContain((1366, 768), Pools.Screens[FingerprintPlatform.Macos]);
        Assert.DoesNotContain((1536, 864), Pools.Screens[FingerprintPlatform.Macos]);
    }

    [Fact]
    public void Cpu_core_counts_are_even_and_at_least_four()
    {
        // Chromium clamps hardwareConcurrency, and hardware with 3 cores is
        // essentially not sold.
        Assert.All(Pools.CpuCores, c =>
        {
            Assert.True(c % 2 == 0, $"{c} cores is not an even count.");
            Assert.InRange(c, 4, 64);
        });
    }

    [Fact]
    public void Device_memory_values_are_powers_of_two()
    {
        // navigator.deviceMemory is specified to bucket real RAM to a power of two,
        // so 12 is a value no real browser can return.
        Assert.All(Pools.DeviceMemory, m =>
        {
            Assert.True(m > 0 && (m & (m - 1)) == 0, $"{m} GB is not a power of two.");
            Assert.InRange(m, 1, 64);
        });
    }

    [Fact]
    public void Locale_and_timezone_pairs_are_geographically_consistent()
    {
        // The pairing is the whole point: de-DE in Asia/Tokyo is a contradiction any
        // site can test for in one line of JavaScript.
        var regions = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["en-US"] = ["America/"],
            ["en-CA"] = ["America/"],
            ["pt-BR"] = ["America/"],
            ["en-GB"] = ["Europe/"],
            ["de-DE"] = ["Europe/"],
            ["fr-FR"] = ["Europe/"],
            ["nl-NL"] = ["Europe/"],
            ["es-ES"] = ["Europe/"],
            ["it-IT"] = ["Europe/"],
            ["pl-PL"] = ["Europe/"],
            ["uk-UA"] = ["Europe/"],
            // Both of these span the conventional boundary, so either prefix is real.
            ["tr-TR"] = ["Europe/", "Asia/"],
            ["ru-RU"] = ["Europe/", "Asia/"],
            ["en-AU"] = ["Australia/"],
            ["en-IN"] = ["Asia/"],
            ["en-SG"] = ["Asia/"],
            ["ja-JP"] = ["Asia/"],
            ["ar-AE"] = ["Asia/"],
        };

        Assert.All(Pools.Locales, entry =>
        {
            Assert.True(
                regions.TryGetValue(entry.Locale, out var prefixes),
                $"{entry.Locale} has no expected region — extend this test alongside the pool.");

            Assert.True(
                prefixes!.Any(p => entry.Timezone.StartsWith(p, StringComparison.Ordinal)),
                $"{entry.Locale} paired with {entry.Timezone} is not a combination a real visitor reports.");
        });
    }

    [Fact]
    public void Every_locale_entry_is_labelled_and_well_formed()
    {
        Assert.All(Pools.Locales, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Label));
            // BCP-47 language-REGION, which is what Intl and Accept-Language expect.
            Assert.Matches("^[a-z]{2}-[A-Z]{2}$", entry.Locale);
            // IANA zone, always Area/Location.
            Assert.Contains('/', entry.Timezone);
        });
    }

    [Fact]
    public void Locales_and_timezones_are_offered_only_once_each()
    {
        // A duplicated row reads as a bug in a picker, and a duplicated timezone
        // quietly biases every randomised pick toward one city.
        Assert.Equal(Pools.Locales.Length, Pools.Locales.Select(l => l.Label).Distinct().Count());
        Assert.Equal(Pools.Locales.Length, Pools.Locales.Select(l => l.Timezone).Distinct().Count());
    }

    [Fact]
    public void Colours_are_distinct_six_digit_hex()
    {
        // Bound straight into a brush, so a malformed value throws while rendering a
        // template — far from where the mistake was made.
        Assert.All(Pools.Colours, c => Assert.Matches("^#[0-9a-f]{6}$", c));
        Assert.Equal(Pools.Colours.Length, Pools.Colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_seed_range_is_five_digits()
    {
        // Not a security boundary — the seed is a device identity, not a secret — but
        // short enough to read aloud when comparing two profiles by hand.
        Assert.Equal(10_000, Pools.SeedMin);
        Assert.Equal(99_999, Pools.SeedMax);
        Assert.True(Pools.SeedMin < Pools.SeedMax);
    }

    // ---------------------------------------------------------------------
    // Weighting — the distributions are lumpy on purpose.
    // ---------------------------------------------------------------------

    [Fact]
    public void Common_values_are_repeated_so_the_draw_is_weighted()
    {
        // Sampling uniformly from a set of plausible values produces a population
        // that is itself implausible: rare resolutions would appear as often as modal
        // ones, and a profile holding a 1-in-9 screen size is more distinguishable,
        // not less.
        Assert.True(
            Pools.Screens[FingerprintPlatform.Windows].Count(s => s == (1920, 1080)) > 1,
            "1920x1080 must be over-represented in the Windows screen pool.");

        Assert.True(Pools.DeviceMemory.Count(m => m == 8) > 1, "8 GB must be over-represented.");
        Assert.True(Pools.CpuCores.Count(c => c == 8) > 1, "8 cores must be over-represented.");
    }

    // ---------------------------------------------------------------------
    // ProfileFactory
    // ---------------------------------------------------------------------

    [Fact]
    public void A_new_seed_stays_inside_the_declared_range_inclusive()
    {
        // The upper bound is the interesting half: GetInt32's max is exclusive, so an
        // off-by-one here silently makes 99999 unreachable.
        for (var i = 0; i < 500; i++)
            Assert.InRange(ProfileFactory.NewSeed(), Pools.SeedMin, Pools.SeedMax);
    }

    [Fact]
    public void Seeds_drawn_in_the_same_tick_are_independent()
    {
        // System.Random seeded from the clock returns correlated sequences when
        // several instances are created in one tick, which is exactly what "create 20
        // profiles" does — and profiles sharing a seed present the same device.
        var seeds = Enumerable.Range(0, 200).Select(_ => ProfileFactory.NewSeed()).ToList();

        Assert.True(seeds.Distinct().Count() > 150, "Seeds look correlated rather than independent.");
    }

    [Fact]
    public void A_new_colour_comes_from_the_pool()
    {
        for (var i = 0; i < 100; i++)
            Assert.Contains(ProfileFactory.NewColour(), Pools.Colours);
    }

    [Theory]
    [InlineData(FingerprintPlatform.Windows)]
    [InlineData(FingerprintPlatform.Macos)]
    [InlineData(FingerprintPlatform.Linux)]
    public void A_new_fingerprint_draws_every_value_from_that_platforms_pools(FingerprintPlatform platform)
    {
        for (var i = 0; i < 60; i++)
        {
            var fp = ProfileFactory.NewFingerprint(platform);

            Assert.Equal(platform, fp.Platform);
            Assert.Contains((fp.Screen.Width!.Value, fp.Screen.Height!.Value), Pools.Screens[platform]);
            Assert.Contains((fp.Gpu.Vendor!, fp.Gpu.Renderer!), Pools.Gpus[platform]);
            Assert.Contains(fp.PlatformVersion!, Pools.PlatformVersions[platform]);
            Assert.Contains(fp.CpuCores.Value!.Value, Pools.CpuCores);
            Assert.Contains(fp.DeviceMemory.Value!.Value, Pools.DeviceMemory);
        }
    }

    [Fact]
    public void A_new_fingerprint_pins_every_drawn_value_to_manual()
    {
        // Auto is coherent too — the binary derives it from the seed — but it is
        // invisible: the user cannot see what their profile claims, so they cannot
        // judge whether it suits the site they are opening. Pinning is the entire
        // reason the "new fingerprint" button exists.
        var fp = ProfileFactory.NewFingerprint(FingerprintPlatform.Windows);

        Assert.Equal(ValueMode.Manual, fp.Screen.Mode);
        Assert.Equal(ValueMode.Manual, fp.Gpu.Mode);
        Assert.Equal(ValueMode.Manual, fp.CpuCores.Mode);
        Assert.Equal(ValueMode.Manual, fp.DeviceMemory.Mode);
        Assert.NotNull(fp.Seed);
    }

    [Fact]
    public void A_new_fingerprint_keeps_the_records_own_protective_defaults()
    {
        // Restating these in the factory would create a second place for the default
        // to drift; this asserts the factory left them alone. Noise on and a
        // realistic quota are the two defaults that matter — a fresh profile must not
        // ship with the central defence off or a quota that reads as incognito.
        var fresh = ProfileFactory.NewFingerprint(FingerprintPlatform.Linux);
        var untouched = new FingerprintConfig();

        Assert.Equal(untouched.Noise, fresh.Noise);
        Assert.True(fresh.Noise.Resolve());
        Assert.Equal(untouched.StorageQuotaMb, fresh.StorageQuotaMb);
        Assert.Equal(untouched.WebRtc, fresh.WebRtc);
    }

    [Fact]
    public void Rerolling_replaces_the_hardware_identity()
    {
        var before = ProfileFactory.NewFingerprint(FingerprintPlatform.Windows);

        // Over enough draws the seed must move; a reroll that returns the same device
        // has not done the one thing the user asked for.
        var seeds = Enumerable.Range(0, 20)
            .Select(_ => ProfileFactory.Reroll(before, FingerprintPlatform.Windows).Seed)
            .ToList();

        Assert.Contains(seeds, s => s != before.Seed);
    }

    [Fact]
    public void Rerolling_keeps_the_choices_that_are_not_hardware()
    {
        // A user who selected Edge and turned canvas noise off asked for a different
        // device, not a different browser and defence posture.
        var chosen = ProfileFactory.NewFingerprint(FingerprintPlatform.Windows) with
        {
            Brand = BrowserBrand.Edge,
            BrandVersion = "121.0.0.0",
            Noise = new NoiseConfig
            {
                Canvas = NoiseMode.Real,
                WebGl = NoiseMode.Real,
                Audio = NoiseMode.Real,
                ClientRects = NoiseMode.Real,
            },
            FontsDir = "/opt/fonts",
            WindowsFontMetrics = true,
            AllowThirdPartyCookies = true,
            WebRtc = new WebRtcConfig { Mode = WebRtcMode.Manual, Ip = "203.0.113.7" },
            StorageQuotaMb = 4096,
            TaskbarHeight = 48,
        };

        var rerolled = ProfileFactory.Reroll(chosen, FingerprintPlatform.Windows);

        Assert.Equal(chosen.Brand, rerolled.Brand);
        Assert.Equal(chosen.BrandVersion, rerolled.BrandVersion);
        Assert.Equal(chosen.Noise, rerolled.Noise);
        Assert.Equal(chosen.FontsDir, rerolled.FontsDir);
        Assert.Equal(chosen.WindowsFontMetrics, rerolled.WindowsFontMetrics);
        Assert.Equal(chosen.AllowThirdPartyCookies, rerolled.AllowThirdPartyCookies);
        Assert.Equal(chosen.WebRtc, rerolled.WebRtc);
        Assert.Equal(chosen.StorageQuotaMb, rerolled.StorageQuotaMb);
        Assert.Equal(chosen.TaskbarHeight, rerolled.TaskbarHeight);
    }

    [Fact]
    public void Rerolling_onto_another_platform_leaves_no_trace_of_the_old_one()
    {
        // Switching Windows to macOS while keeping the D3D11 renderer would build the
        // impossible machine the pools are keyed to prevent.
        var windows = ProfileFactory.NewFingerprint(FingerprintPlatform.Windows);
        var mac = ProfileFactory.Reroll(windows, FingerprintPlatform.Macos);

        Assert.Equal(FingerprintPlatform.Macos, mac.Platform);
        Assert.Contains((mac.Gpu.Vendor!, mac.Gpu.Renderer!), Pools.Gpus[FingerprintPlatform.Macos]);
        Assert.Contains(mac.PlatformVersion!, Pools.PlatformVersions[FingerprintPlatform.Macos]);
        Assert.DoesNotContain("D3D11", mac.Gpu.Renderer!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_new_profile_is_identified_stamped_and_current()
    {
        var profile = ProfileFactory.NewProfile("Amazon", FingerprintPlatform.Windows);

        Assert.Equal("Amazon", profile.Name);
        Assert.False(string.IsNullOrWhiteSpace(profile.Id));
        Assert.True(profile.CreatedAt > 0);
        Assert.Equal(profile.CreatedAt, profile.UpdatedAt);
        Assert.Null(profile.LastLaunchedAt);
        Assert.Contains(profile.Colour!, Pools.Colours);

        // Otherwise the very next load would try to migrate a profile this build just
        // wrote, and backfill over the user's explicit choices.
        Assert.Equal(ProfileMigration.CurrentVersion, profile.SchemaVersion);
    }

    [Fact]
    public void A_new_profile_blocks_the_default_ports()
    {
        // The migration backfills these for existing profiles; a brand-new one must
        // not end up less protected than an upgraded one.
        var profile = ProfileFactory.NewProfile("P", FingerprintPlatform.Linux);

        Assert.Equal(PrivacyArgs.DefaultBlockedPorts.Length, profile.Startup.BlockedPorts.Count);
    }

    [Fact]
    public void A_new_profile_lands_in_the_root_unless_a_folder_is_given()
    {
        Assert.Null(ProfileFactory.NewProfile("P", FingerprintPlatform.Windows).FolderId);
        Assert.Equal("f1", ProfileFactory.NewProfile("P", FingerprintPlatform.Windows, "f1").FolderId);
    }

    [Fact]
    public void Two_new_profiles_never_share_an_id_or_a_seed()
    {
        // Creating from the sidebar in a loop is one tick's worth of calls, and two
        // profiles presenting the same device is the correlation the tool exists to
        // break.
        var made = Enumerable.Range(0, 40)
            .Select(i => ProfileFactory.NewProfile($"P{i}", FingerprintPlatform.Windows))
            .ToList();

        Assert.Equal(made.Count, made.Select(p => p.Id).Distinct().Count());
        Assert.True(
            made.Select(p => p.Fingerprint.Seed).Distinct().Count() > made.Count / 2,
            "Seeds across a batch look correlated.");
    }
}
