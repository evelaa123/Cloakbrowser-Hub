using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Doctor.Console;

namespace CloakHub.Doctor.Reports;

/// <summary>
/// The exact Chromium command line a profile produces.
/// <para>
/// This exists because the flags are the whole product. A user cannot verify an
/// anti-detect tool by looking at its UI — they need to see what it actually
/// passes to the browser, and be able to check it against the browser's own
/// <c>chrome://version</c>. Printing argv is the only honest way to show that.
/// </para>
/// </summary>
public static class FingerprintReport
{
    public static void Run()
    {
        Output.Section("Fingerprint flags");

        var minimal = new Profile { Id = "doctor-minimal-0001", Name = "Minimal" };
        Show("A profile with nothing configured", minimal);

        Output.Plain();
        Output.Paragraph(
            "Note that a seed is emitted even though none was set. That is deliberate and " +
            "was a real bug once: the Hub launches with the wrapper's stealth args " +
            "disabled, so if it omitted the seed the browser would start with no " +
            "fingerprint spoofing at all while the UI still called the profile protected. " +
            "An unset seed is now derived from the profile id, which also makes it stable " +
            "across launches — a profile that changed its device identity every time " +
            "would look like a new machine on every visit.");

        Show("A fully configured profile", Configured());
        ShowNoise();
        ShowPrivacy(Configured());
    }

    /// <summary>
    /// What the noise settings actually do, stated plainly.
    /// <para>
    /// Called out separately because this is the one place the UI is at risk of
    /// overpromising. The profile model stores four independent per-surface
    /// switches — canvas, WebGL, audio, client rects — because that is what users
    /// expect from a tool in this category, but the browser binary exposes a single
    /// on/off flag covering all four. A user who sets canvas to noise and audio to
    /// real gets noise on both, and would have no way to discover that without
    /// being told.
    /// </para>
    /// </summary>
    private static void ShowNoise()
    {
        Output.Section("Noise");

        var defaults = new NoiseConfig();
        Output.Item("Canvas", defaults.Canvas.ToString());
        Output.Item("WebGL", defaults.WebGl.ToString());
        Output.Item("Audio", defaults.Audio.ToString());
        Output.Item("Client rects", defaults.ClientRects.ToString());
        Output.Item("Resolves to", defaults.Resolve() ? "noise ENABLED" : "noise DISABLED");
        Output.Item("Flag emitted", defaults.Resolve() ? "(none — on is the binary default)" : "--fingerprint-noise=false");

        Output.Plain();
        Output.Warn("The four settings above collapse into one browser flag.");
        Output.Paragraph(
            "The binary offers a single --fingerprint-noise switch covering canvas, WebGL, " +
            "audio and client rects together, so the surfaces cannot currently be " +
            "controlled independently. They are stored separately anyway: any one surface " +
            "asking for noise keeps noise on for all of them, which errs toward honouring " +
            "an explicit request rather than silently ignoring it, and a future binary with " +
            "finer flags will need no data migration.");
    }

    private static void Show(string title, Profile profile)
    {
        Output.Plain();
        Output.Item(title, "");

        var args = FingerprintArgs.Build(profile);
        foreach (var arg in args.OrderBy(a => a, StringComparer.Ordinal))
            Output.Plain($"    {arg}");

        Output.Plain();
        Output.Info($"{args.Count} flag{(args.Count == 1 ? "" : "s")}; seed for id " +
                    $"\"{profile.Id}\" is {FingerprintArgs.SeedFromId(profile.Id)}.");
    }

    /// <summary>
    /// Privacy flags, reported apart from fingerprint flags.
    /// <para>
    /// They are built by a different component and answer a different question:
    /// fingerprint flags shape the identity a site sees, privacy flags shape what
    /// the page is allowed to probe. Merging them into one list would hide which
    /// setting produced which flag, and port blocking in particular is easy to
    /// mistake for a fingerprint feature.
    /// </para>
    /// </summary>
    private static void ShowPrivacy(Profile profile)
    {
        Output.Section("Privacy flags");

        var args = PrivacyArgs.Build(profile);
        if (args.Count == 0)
        {
            Output.Info("None — no ports blocked and Do Not Track is off.");
        }
        else
        {
            foreach (var arg in args)
            {
                // The resolver rule list is one enormous flag; wrapping it keeps the
                // report readable without misrepresenting it as several flags.
                if (arg.Length > 100)
                {
                    var eq = arg.IndexOf('=');
                    Output.Plain($"    {arg[..(eq + 1)]}");
                    foreach (var rule in arg[(eq + 1)..].Split(','))
                        Output.Plain($"      {rule},");
                }
                else
                {
                    Output.Plain($"    {arg}");
                }
            }
        }

        Output.Plain();
        Output.Paragraph(
            "Blocked localhost ports matter more than they look. Sites probe them to " +
            "correlate a visitor across profiles: the set of reachable local ports is a " +
            "machine trait that survives every fingerprint change, so two profiles with " +
            "identical fingerprints and identical open ports still link together. " +
            "Blocking them is also what an ordinary user's firewall already does, so it " +
            "reads as normal rather than as evasion.");
        Output.Plain();
        Output.Item("Default blocked ports", string.Join(", ", PrivacyArgs.DefaultBlockedPorts));
    }

    /// <summary>
    /// A profile with every knob set, to show the full flag surface.
    /// <para>
    /// Values are chosen to be internally coherent — a Windows platform with a
    /// Windows platform version, a screen size that exists, a timezone matching the
    /// locale. An incoherent example would print flags that no sane profile would
    /// produce and would teach the reader the wrong thing about what the tool does.
    /// </para>
    /// </summary>
    private static Profile Configured() => new()
    {
        Id = "doctor-full-0001",
        Name = "Fully Configured",
        Fingerprint = new FingerprintConfig
        {
            Seed = 48219,
            Platform = FingerprintPlatform.Windows,
            PlatformVersion = "10.0.0",
            Brand = BrowserBrand.Chrome,
            Screen = new ScreenConfig { Mode = ValueMode.Manual, Width = 1920, Height = 1080 },
            Gpu = new GpuConfig
            {
                Mode = ValueMode.Manual,
                Vendor = "Google Inc. (NVIDIA)",
                Renderer = "ANGLE (NVIDIA, NVIDIA GeForce RTX 3060 Direct3D11 vs_5_0 ps_5_0, D3D11)",
            },
            CpuCores = new ValueOf<int> { Mode = ValueMode.Manual, Value = 8 },
            DeviceMemory = new ValueOf<int> { Mode = ValueMode.Manual, Value = 8 },
            StorageQuotaMb = 120000,
            TaskbarHeight = 40,
            WebRtc = new WebRtcConfig { Mode = WebRtcMode.Auto },
            // Left at the default (noise on for all four surfaces). Mixing them
            // would misrepresent what the tool does: the binary has one switch, so
            // asking for noise on canvas while asking for real audio still produces
            // noise on both. Showing a mixed configuration here would suggest a
            // per-surface capability that does not exist yet.
        },
        // A proxy is required for WebRTC "auto" to emit anything: a spoofed ICE IP
        // on a direct connection is itself a mismatch, so the builder skips it.
        Proxy = new ProxyConfig
        {
            Kind = ProxyKind.Socks5,
            Host = "203.0.113.10",
            Port = 1080,
            Username = "user",
            Password = "secret",
        },
        Locale = new LocaleConfig
        {
            Mode = LocaleMode.Manual,
            Locale = "en-US",
            Timezone = "America/New_York",
        },
        Geo = new GeoConfig
        {
            Mode = GeoMode.Manual,
            Latitude = 40.7128,
            Longitude = -74.0060,
            Accuracy = 50,
        },
        Startup = new StartupConfig
        {
            BlockedPorts = [.. PrivacyArgs.DefaultBlockedPorts],
            DoNotTrack = false,
        },
        SchemaVersion = 3,
    };
}
