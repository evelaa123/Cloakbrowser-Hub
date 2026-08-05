using CloakHub.Core.Launch;
using CloakHub.Core.Model;

namespace CloakHub.Core.Tests;

public class FingerprintArgsTests
{
    private static Profile Base(Action<Profile>? _ = null) => new()
    {
        Id = "p-1",
        Name = "Test",
    };

    private static string? Flag(IEnumerable<string> args, string key) =>
        args.FirstOrDefault(a => a == key || a.StartsWith(key + "=", StringComparison.Ordinal));

    // ---------------------------------------------------------------------
    // The seed contract. This is the one that had a real safety consequence:
    // with StealthArgs=false the wrapper no longer supplies a random seed, so
    // an omitted flag means no spoofing at all.
    // ---------------------------------------------------------------------

    [Fact]
    public void Always_emits_a_seed_even_when_the_profile_has_none()
    {
        var args = FingerprintArgs.Build(Base());
        var seed = Flag(args, "--fingerprint");
        Assert.NotNull(seed);
        Assert.Equal($"--fingerprint={FingerprintArgs.SeedFromId("p-1")}", seed);
    }

    [Fact]
    public void Fallback_seed_is_stable_for_the_same_id()
    {
        Assert.Equal(FingerprintArgs.SeedFromId("abc"), FingerprintArgs.SeedFromId("abc"));
        Assert.NotEqual(FingerprintArgs.SeedFromId("abc"), FingerprintArgs.SeedFromId("abd"));
    }

    [Fact]
    public void Fallback_seed_lands_in_the_wrappers_own_range()
    {
        foreach (var id in new[] { "", "a", "profile-42", Guid.NewGuid().ToString() })
        {
            var seed = FingerprintArgs.SeedFromId(id);
            Assert.InRange(seed, 10000, 99999);
        }
    }

    [Fact]
    public void Explicit_seed_wins_over_the_fallback()
    {
        var p = Base() with { Fingerprint = new FingerprintConfig { Seed = 54321 } };
        Assert.Equal("--fingerprint=54321", Flag(FingerprintArgs.Build(p), "--fingerprint"));
    }

    // ---------------------------------------------------------------------
    // Storage quota — the incognito-detection bug.
    // ---------------------------------------------------------------------

    [Fact]
    public void Emits_storage_quota_by_default_so_the_profile_is_not_read_as_incognito()
    {
        var args = FingerprintArgs.Build(Base());
        Assert.Equal("--fingerprint-storage-quota=120000", Flag(args, "--fingerprint-storage-quota"));
    }

    [Fact]
    public void A_cleared_storage_quota_emits_no_flag()
    {
        var p = Base() with { Fingerprint = new FingerprintConfig { StorageQuotaMb = null } };
        Assert.Null(Flag(FingerprintArgs.Build(p), "--fingerprint-storage-quota"));
    }

    // ---------------------------------------------------------------------
    // Auto vs manual: auto must stay silent so the binary keeps values coherent.
    // ---------------------------------------------------------------------

    [Fact]
    public void Auto_values_emit_no_flags()
    {
        var args = FingerprintArgs.Build(Base());
        Assert.Null(Flag(args, "--fingerprint-screen-width"));
        Assert.Null(Flag(args, "--fingerprint-gpu-vendor"));
        Assert.Null(Flag(args, "--fingerprint-hardware-concurrency"));
        Assert.Null(Flag(args, "--fingerprint-device-memory"));
    }

    [Fact]
    public void Manual_values_emit_flags()
    {
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig
            {
                Screen = new ScreenConfig { Mode = ValueMode.Manual, Width = 1920, Height = 1080 },
                Gpu = new GpuConfig { Mode = ValueMode.Manual, Vendor = "Google Inc. (NVIDIA)", Renderer = "ANGLE (NVIDIA)" },
                CpuCores = new ValueOf<int> { Mode = ValueMode.Manual, Value = 8 },
                DeviceMemory = new ValueOf<int> { Mode = ValueMode.Manual, Value = 16 },
            },
        };
        var args = FingerprintArgs.Build(p);
        Assert.Equal("--fingerprint-screen-width=1920", Flag(args, "--fingerprint-screen-width"));
        Assert.Equal("--fingerprint-screen-height=1080", Flag(args, "--fingerprint-screen-height"));
        Assert.Equal("--fingerprint-gpu-vendor=Google Inc. (NVIDIA)", Flag(args, "--fingerprint-gpu-vendor"));
        Assert.Equal("--fingerprint-hardware-concurrency=8", Flag(args, "--fingerprint-hardware-concurrency"));
        Assert.Equal("--fingerprint-device-memory=16", Flag(args, "--fingerprint-device-memory"));
    }

    [Fact]
    public void Manual_screen_without_dimensions_emits_nothing()
    {
        // A half-filled manual override must not produce a partial flag pair;
        // a width with no height would be an incoherent screen.
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig { Screen = new ScreenConfig { Mode = ValueMode.Manual } },
        };
        var args = FingerprintArgs.Build(p);
        Assert.Null(Flag(args, "--fingerprint-screen-width"));
        Assert.Null(Flag(args, "--fingerprint-screen-height"));
    }

    // ---------------------------------------------------------------------
    // Noise: four UI switches, one binary flag.
    // ---------------------------------------------------------------------

    [Fact]
    public void A_new_profile_leaves_noise_enabled()
    {
        // Regression guard. This originally asserted the opposite, because the
        // ported NoiseConfig defaulted every surface to Real, which collapses to
        // --fingerprint-noise=false. That shipped a brand-new profile with canvas,
        // WebGL and audio randomisation switched off while the UI still called it
        // protected — the TypeScript original defaults noise on. The bug was found
        // by reading the flags the diagnostics report printed, and the old test had
        // locked it in by asserting the collapse rather than the default.
        var args = FingerprintArgs.Build(Base());
        Assert.Null(Flag(args, "--fingerprint-noise"));
    }

    [Fact]
    public void All_surfaces_set_to_real_disables_noise()
    {
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig
            {
                Noise = new NoiseConfig
                {
                    Canvas = NoiseMode.Real,
                    WebGl = NoiseMode.Real,
                    Audio = NoiseMode.Real,
                    ClientRects = NoiseMode.Real,
                },
            },
        };
        Assert.Equal("--fingerprint-noise=false", Flag(FingerprintArgs.Build(p), "--fingerprint-noise"));
    }

    [Fact]
    public void Any_single_surface_asking_for_noise_keeps_noise_on()
    {
        // The binary cannot separate the surfaces, so one request wins. Erring
        // toward "noise on" is deliberate: silently ignoring an explicit request
        // to randomise a surface is the worse failure.
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig
            {
                Noise = new NoiseConfig { Canvas = NoiseMode.Noise },
            },
        };
        Assert.Null(Flag(FingerprintArgs.Build(p), "--fingerprint-noise"));
    }

    // ---------------------------------------------------------------------
    // WebRTC: 'auto' without a proxy is itself a mismatch.
    // ---------------------------------------------------------------------

    [Fact]
    public void Webrtc_auto_is_skipped_without_a_proxy()
    {
        var p = Base() with { Fingerprint = new FingerprintConfig { WebRtc = new WebRtcConfig { Mode = WebRtcMode.Auto } } };
        Assert.Null(Flag(FingerprintArgs.Build(p), "--fingerprint-webrtc-ip"));
    }

    [Fact]
    public void Webrtc_auto_is_emitted_with_a_proxy()
    {
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig { WebRtc = new WebRtcConfig { Mode = WebRtcMode.Auto } },
            Proxy = new ProxyConfig { Kind = ProxyKind.Http, Host = "1.2.3.4", Port = 8080 },
        };
        Assert.Equal("--fingerprint-webrtc-ip=auto", Flag(FingerprintArgs.Build(p), "--fingerprint-webrtc-ip"));
    }

    [Fact]
    public void Webrtc_manual_ip_needs_no_proxy()
    {
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig { WebRtc = new WebRtcConfig { Mode = WebRtcMode.Manual, Ip = "9.9.9.9" } },
        };
        Assert.Equal("--fingerprint-webrtc-ip=9.9.9.9", Flag(FingerprintArgs.Build(p), "--fingerprint-webrtc-ip"));
    }

    // ---------------------------------------------------------------------
    // Locale must reach --lang too, or Accept-Language contradicts the profile.
    // ---------------------------------------------------------------------

    [Fact]
    public void Manual_locale_sets_both_lang_and_fingerprint_locale()
    {
        var p = Base() with { Locale = new LocaleConfig { Mode = LocaleMode.Manual, Locale = "de-DE", Timezone = "Europe/Berlin" } };
        var args = FingerprintArgs.Build(p);
        Assert.Equal("--lang=de-DE", Flag(args, "--lang"));
        Assert.Equal("--fingerprint-locale=de-DE", Flag(args, "--fingerprint-locale"));
        Assert.Equal("--fingerprint-timezone=Europe/Berlin", Flag(args, "--fingerprint-timezone"));
    }

    [Fact]
    public void Ip_locale_pins_nothing()
    {
        var p = Base() with { Locale = new LocaleConfig { Mode = LocaleMode.Ip, Locale = "de-DE" } };
        var args = FingerprintArgs.Build(p);
        Assert.Null(Flag(args, "--lang"));
        Assert.Null(Flag(args, "--fingerprint-locale"));
    }

    // ---------------------------------------------------------------------
    // Extra args: additive, but they must not hijack identity flags.
    // ---------------------------------------------------------------------

    [Fact]
    public void Extra_args_are_appended()
    {
        var p = Base() with { Startup = new StartupConfig { ExtraArgs = ["--mute-audio"] } };
        Assert.Contains("--mute-audio", FingerprintArgs.Build(p));
    }

    [Fact]
    public void Extra_args_cannot_override_an_owned_fingerprint_flag()
    {
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig { Seed = 11111 },
            Startup = new StartupConfig { ExtraArgs = ["--fingerprint=99999", "--lang=zz-ZZ"] },
        };
        var args = FingerprintArgs.Build(p);
        Assert.Equal("--fingerprint=11111", Flag(args, "--fingerprint"));
        // --lang was not set by the profile (locale is Ip mode), so the user flag
        // is free to apply: "owned" blocks hijacking, not all use.
        Assert.Equal("--lang=zz-ZZ", Flag(args, "--lang"));
    }

    [Fact]
    public void Extra_args_ignore_non_flag_junk()
    {
        var p = Base() with { Startup = new StartupConfig { ExtraArgs = ["   ", "notaflag", "-single"] } };
        var args = FingerprintArgs.Build(p);
        Assert.DoesNotContain("notaflag", args);
        Assert.DoesNotContain("-single", args);
    }

    [Fact]
    public void Output_is_deterministic_for_the_same_profile()
    {
        // The flag-preview panel is only useful if it does not reshuffle between
        // renders, and a reordering argv makes launch diffs unreadable.
        var p = Base() with
        {
            Fingerprint = new FingerprintConfig
            {
                Screen = new ScreenConfig { Mode = ValueMode.Manual, Width = 1440, Height = 900 },
                CpuCores = new ValueOf<int> { Mode = ValueMode.Manual, Value = 4 },
            },
        };
        Assert.Equal(FingerprintArgs.Build(p), FingerprintArgs.Build(p));
    }

    [Fact]
    public void No_duplicate_flag_keys_are_emitted()
    {
        var p = Base() with { Startup = new StartupConfig { ExtraArgs = ["--mute-audio", "--mute-audio=false"] } };
        var keys = FingerprintArgs.Build(p).Select(a => a.Split('=')[0]).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Windows_font_metrics_only_applies_to_a_windows_profile()
    {
        var win = Base() with { Fingerprint = new FingerprintConfig { Platform = FingerprintPlatform.Windows, WindowsFontMetrics = true } };
        var lin = Base() with { Fingerprint = new FingerprintConfig { Platform = FingerprintPlatform.Linux, WindowsFontMetrics = true } };
        Assert.NotNull(Flag(FingerprintArgs.Build(win), "--fingerprint-windows-font-metrics"));
        Assert.Null(Flag(FingerprintArgs.Build(lin), "--fingerprint-windows-font-metrics"));
    }

    [Fact]
    public void Geo_manual_uses_invariant_decimal_separator()
    {
        // A comma decimal separator under a European culture would produce
        // "--fingerprint-location=52,5,13,4" and silently mean nothing.
        var prev = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var p = Base() with { Geo = new GeoConfig { Mode = GeoMode.Manual, Latitude = 52.52, Longitude = 13.405 } };
            Assert.Equal("--fingerprint-location=52.52,13.405", Flag(FingerprintArgs.Build(p), "--fingerprint-location"));
        }
        finally { Thread.CurrentThread.CurrentCulture = prev; }
    }
}
