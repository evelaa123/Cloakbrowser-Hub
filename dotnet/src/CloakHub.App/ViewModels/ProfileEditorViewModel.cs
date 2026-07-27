using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CloakHub.Core.Launch;
using CloakHub.Core.Model;
using CloakHub.Core.Network;
using CloakHub.Core.Platform;

namespace CloakHub.App.ViewModels;

/// <summary>The editor's tabs. Mirrors <c>TabId</c> in ProfileEditor.tsx.</summary>
public enum EditorTab { General, Fingerprint, Proxy, Locale, Behaviour, Startup, Advanced }

/// <summary>
/// Edits one profile.
/// <para>
/// Works on a <b>draft</b> and writes it back only on Save, so Cancel is a real
/// cancel and a half-finished fingerprint is never persisted. That matters more here
/// than in an ordinary form: a profile is an identity a site has already seen, and
/// saving an incoherent intermediate state — a macOS platform with the Windows GPU
/// still attached, because the user was mid-edit — would burn it.
/// </para>
/// <para>
/// The draft is a single immutable <see cref="Profile"/> record rather than a field
/// per input. Sub-records are replaced wholesale on every edit, which is verbose in
/// the setters but means the draft is always a valid <see cref="Profile"/> that can be
/// handed straight to <see cref="FingerprintArgs.Build"/> for the live flag preview.
/// A parallel set of loose fields would have to be assembled first, and any field
/// forgotten in that assembly would silently not be saved.
/// </para>
/// </summary>
public sealed class ProfileEditorViewModel : ViewModelBase
{
    private readonly Action<Profile> _save;
    private readonly Action _cancel;

    public ProfileEditorViewModel(
        Profile profile,
        IReadOnlyList<ProfileFolder> folders,
        Action<Profile> save,
        Action cancel,
        IReadOnlyList<SavedProxy>? savedProxies = null)
    {
        _draft = profile;
        _save = save;
        _cancel = cancel;

        SavedProxyChoices =
        [
            SavedProxyChoice.Inline,
            .. (savedProxies ?? []).Select(p => new SavedProxyChoice(p)),
        ];

        // If the profile points at an entry that has since been deleted, fall back to
        // inline rather than showing an empty combo box. The copied host and port are
        // still in the draft, so the profile keeps working and the user can see what
        // it was pointing at instead of an unexplained blank.
        _savedProxy = SavedProxyChoices.FirstOrDefault(c => c.Id == profile.Proxy.SavedProxyId)
                      ?? SavedProxyChoice.Inline;
        if (_savedProxy.Proxy is null && profile.Proxy.SavedProxyId is not null)
            _draft = _draft with { Proxy = _draft.Proxy with { SavedProxyId = null } };

        // A synthetic "no folder" entry, so the combo box can express the root. A
        // null SelectedItem would also work but renders as an empty row, which reads
        // as a missing value rather than a deliberate choice.
        FolderChoices = [FolderChoice.Root, .. folders.Select(f => new FolderChoice(f.Id, f.Name))];
        _folder = FolderChoices.FirstOrDefault(f => f.Id == profile.FolderId) ?? FolderChoice.Root;

        SaveCommand = new RelayCommand(OnSave);
        CancelCommand = new RelayCommand(() => _cancel());
        NewFingerprintCommand = new RelayCommand(RerollFingerprint);
        NewSeedCommand = new RelayCommand(() => Seed = ProfileFactory.NewSeed());
        RandomMacCommand = new RelayCommand(GenerateMac);
        SelectTabCommand = new RelayCommand<EditorTab>(tab => Tab = tab);
        ApplyLocalePresetCommand = new RelayCommand<LocalePreset>(ApplyLocalePreset);
        ApplyScreenPresetCommand = new RelayCommand<ScreenPreset>(ApplyScreenPreset);
        ApplyGpuPresetCommand = new RelayCommand<GpuPreset>(ApplyGpuPreset);
        PickColourCommand = new RelayCommand<string>(c => Colour = c);
    }

    // ------------------------------------------------------------------
    // Draft
    // ------------------------------------------------------------------

    private Profile _draft;

    /// <summary>
    /// Replace the draft and refresh everything derived from it.
    /// <para>
    /// One broadcast point rather than per-property notifications, because almost
    /// every field feeds the summary panel and the flag preview. Raising them
    /// individually would mean each new field needs a matching notification added in
    /// two places, and the one that gets forgotten shows a stale summary — which
    /// looks like the value did not save.
    /// </para>
    /// </summary>
    private void Mutate(Func<Profile, Profile> change)
    {
        _draft = change(_draft);
        OnPropertyChanged(string.Empty);
    }

    private FingerprintConfig Fp => _draft.Fingerprint;

    private void MutateFp(Func<FingerprintConfig, FingerprintConfig> change) =>
        Mutate(d => d with { Fingerprint = change(d.Fingerprint) });

    public string Id => _draft.Id;

    // ------------------------------------------------------------------
    // Tabs
    // ------------------------------------------------------------------

    private EditorTab _tab = EditorTab.General;
    public EditorTab Tab
    {
        get => _tab;
        set
        {
            if (!SetField(ref _tab, value)) return;
            foreach (var item in Tabs) item.IsActive = item.Tab == value;
            OnPropertyChanged(nameof(IsGeneral));
            OnPropertyChanged(nameof(IsFingerprint));
            OnPropertyChanged(nameof(IsProxy));
            OnPropertyChanged(nameof(IsLocale));
            OnPropertyChanged(nameof(IsBehaviour));
            OnPropertyChanged(nameof(IsStartup));
            OnPropertyChanged(nameof(IsAdvanced));
        }
    }

    public IReadOnlyList<EditorTabItem> Tabs { get; } =
    [
        new(EditorTab.General, "General") { IsActive = true },
        new(EditorTab.Fingerprint, "Fingerprint"),
        new(EditorTab.Proxy, "Proxy"),
        new(EditorTab.Locale, "Locale & Geo"),
        new(EditorTab.Behaviour, "Behaviour"),
        new(EditorTab.Startup, "Startup"),
        new(EditorTab.Advanced, "Advanced"),
    ];

    public RelayCommand<EditorTab> SelectTabCommand { get; }

    public bool IsGeneral => _tab == EditorTab.General;
    public bool IsFingerprint => _tab == EditorTab.Fingerprint;
    public bool IsProxy => _tab == EditorTab.Proxy;
    public bool IsLocale => _tab == EditorTab.Locale;
    public bool IsBehaviour => _tab == EditorTab.Behaviour;
    public bool IsStartup => _tab == EditorTab.Startup;
    public bool IsAdvanced => _tab == EditorTab.Advanced;

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand NewFingerprintCommand { get; }
    public RelayCommand NewSeedCommand { get; }
    public RelayCommand RandomMacCommand { get; }
    public RelayCommand<LocalePreset> ApplyLocalePresetCommand { get; }
    public RelayCommand<ScreenPreset> ApplyScreenPresetCommand { get; }
    public RelayCommand<GpuPreset> ApplyGpuPresetCommand { get; }
    public RelayCommand<string> PickColourCommand { get; }

    private void OnSave()
    {
        if (string.IsNullOrWhiteSpace(_draft.Name))
        {
            // Refuses rather than inventing a name, and jumps to the field at fault so
            // the message is not about a control the user cannot see.
            Tab = EditorTab.General;
            NameError = "Give the profile a name first.";
            return;
        }

        NameError = null;
        _save(_draft with { Name = _draft.Name.Trim() });
    }

    private string? _nameError;
    public string? NameError
    {
        get => _nameError;
        private set { if (SetField(ref _nameError, value)) OnPropertyChanged(nameof(HasNameError)); }
    }

    public bool HasNameError => !string.IsNullOrEmpty(_nameError);

    /// <summary>
    /// Re-roll the hardware identity.
    /// <para>
    /// Deliberately does not touch the browser brand, noise settings or WebRTC mode —
    /// see <see cref="ProfileFactory.Reroll"/>. "New fingerprint" means new machine,
    /// not new preferences.
    /// </para>
    /// </summary>
    private void RerollFingerprint() =>
        MutateFp(fp => ProfileFactory.Reroll(fp, fp.Platform));

    /// <summary>
    /// Generate a plausible MAC address.
    /// <para>
    /// Seeded from the fingerprint seed so the address is stable for the profile
    /// rather than different on every click, and drawn from a real vendor OUI list —
    /// a fully random prefix belongs to no manufacturer, which is itself conspicuous
    /// on a network that logs devices.
    /// </para>
    /// </summary>
    private void GenerateMac()
    {
        var seed = Fp.Seed ?? FingerprintArgs.SeedFromId(_draft.Id);
        Mutate(d => d with
        {
            Mac = d.Mac with
            {
                Mode = ValueMode.Manual,
                Address = MacAddress.Generate(seed),
            },
        });
    }

    // ------------------------------------------------------------------
    // General
    // ------------------------------------------------------------------

    public string Name
    {
        get => _draft.Name;
        set
        {
            if (_draft.Name == value) return;
            Mutate(d => d with { Name = value });
            // Cleared as soon as the user types, rather than only on the next save
            // attempt: an error that outlives the mistake trains people to ignore it.
            if (!string.IsNullOrWhiteSpace(value)) NameError = null;
        }
    }

    public string Notes
    {
        get => _draft.Notes ?? "";
        set { if (_draft.Notes != value) Mutate(d => d with { Notes = Blank(value) }); }
    }

    /// <summary>
    /// Tags as the user types them, comma separated.
    /// <para>
    /// Held as text and split on save rather than parsed per keystroke, because
    /// splitting live makes a half-typed "a, b" collapse the moment the comma is
    /// entered and the caret jump. Empty entries are dropped so a trailing comma does
    /// not create a nameless tag.
    /// </para>
    /// </summary>
    public string TagsText
    {
        get => string.Join(", ", _draft.Tags);
        set => Mutate(d => d with
        {
            Tags = [.. value.Split(',')
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)],
        });
    }

    public string? Colour
    {
        get => _draft.Colour;
        set { if (_draft.Colour != value) Mutate(d => d with { Colour = value }); }
    }

    public IReadOnlyList<string> ColourChoices { get; } = Pools.Colours;

    public IReadOnlyList<ProfileStatus> StatusChoices { get; } =
        Enum.GetValues<ProfileStatus>();

    public ProfileStatus Status
    {
        get => _draft.Status;
        set { if (_draft.Status != value) Mutate(d => d with { Status = value }); }
    }

    public IReadOnlyList<ProfileKind> KindChoices { get; } = Enum.GetValues<ProfileKind>();

    public ProfileKind Kind
    {
        get => _draft.Kind;
        set { if (_draft.Kind != value) Mutate(d => d with { Kind = value }); }
    }

    public IReadOnlyList<FolderChoice> FolderChoices { get; }

    private FolderChoice _folder;
    public FolderChoice Folder
    {
        get => _folder;
        set
        {
            if (value is null || !SetField(ref _folder, value)) return;
            Mutate(d => d with { FolderId = value.Id });
        }
    }

    // ------------------------------------------------------------------
    // Fingerprint
    // ------------------------------------------------------------------

    public int? Seed
    {
        get => Fp.Seed;
        set { if (Fp.Seed != value) MutateFp(fp => fp with { Seed = value }); }
    }

    public IReadOnlyList<FingerprintPlatform> PlatformChoices { get; } =
        Enum.GetValues<FingerprintPlatform>();

    /// <summary>
    /// Target OS.
    /// <para>
    /// Changing it re-rolls the screen, GPU and platform version from the new
    /// platform's pools. That is not a convenience: leaving a Windows
    /// <c>D3D11</c> renderer attached to a macOS platform describes a machine that
    /// cannot exist, and one line of JavaScript comparing the two exposes the profile
    /// more thoroughly than not spoofing at all would have.
    /// </para>
    /// </summary>
    public FingerprintPlatform Platform
    {
        get => Fp.Platform;
        set
        {
            if (Fp.Platform == value) return;

            var (width, height) = Pools.Screens[value][0];
            var (vendor, renderer) = Pools.Gpus[value][0];

            MutateFp(fp => fp with
            {
                Platform = value,
                PlatformVersion = Pools.PlatformVersions[value][0],
                Screen = fp.Screen.Mode == ValueMode.Manual
                    ? fp.Screen with { Width = width, Height = height }
                    : fp.Screen,
                Gpu = fp.Gpu.Mode == ValueMode.Manual
                    ? fp.Gpu with { Vendor = vendor, Renderer = renderer }
                    : fp.Gpu,
                // Windows-only flag; leaving it set on another platform would emit a
                // flag the binary ignores and mislead the user into thinking it applied.
                WindowsFontMetrics = value == FingerprintPlatform.Windows && fp.WindowsFontMetrics,
            });

            OnPropertyChanged(nameof(PlatformVersionChoices));
            OnPropertyChanged(nameof(ScreenPresets));
            OnPropertyChanged(nameof(GpuPresets));
            OnPropertyChanged(nameof(IsWindows));
        }
    }

    public bool IsWindows => Fp.Platform == FingerprintPlatform.Windows;

    public IReadOnlyList<string> PlatformVersionChoices => Pools.PlatformVersions[Fp.Platform];

    public string? PlatformVersion
    {
        get => Fp.PlatformVersion;
        set { if (Fp.PlatformVersion != value) MutateFp(fp => fp with { PlatformVersion = value }); }
    }

    public IReadOnlyList<BrowserBrand> BrandChoices { get; } = Enum.GetValues<BrowserBrand>();

    public BrowserBrand Brand
    {
        get => Fp.Brand;
        set { if (Fp.Brand != value) MutateFp(fp => fp with { Brand = value }); }
    }

    public string BrandVersion
    {
        get => Fp.BrandVersion ?? "";
        set { if (Fp.BrandVersion != value) MutateFp(fp => fp with { BrandVersion = Blank(value) }); }
    }

    public string UserAgent
    {
        get => _draft.UserAgent ?? "";
        set { if (_draft.UserAgent != value) Mutate(d => d with { UserAgent = Blank(value) }); }
    }

    // --- screen ---

    public IReadOnlyList<ValueMode> ValueModes { get; } = Enum.GetValues<ValueMode>();

    public ValueMode ScreenMode
    {
        get => Fp.Screen.Mode;
        set
        {
            if (Fp.Screen.Mode == value) return;
            MutateFp(fp => fp with { Screen = fp.Screen with { Mode = value } });
            OnPropertyChanged(nameof(IsScreenManual));
        }
    }

    public bool IsScreenManual => Fp.Screen.Mode == ValueMode.Manual;

    public int? ScreenWidth
    {
        get => Fp.Screen.Width;
        set { if (Fp.Screen.Width != value) MutateFp(fp => fp with { Screen = fp.Screen with { Width = value } }); }
    }

    public int? ScreenHeight
    {
        get => Fp.Screen.Height;
        set { if (Fp.Screen.Height != value) MutateFp(fp => fp with { Screen = fp.Screen with { Height = value } }); }
    }

    public IReadOnlyList<ScreenPreset> ScreenPresets =>
        [.. Pools.Screens[Fp.Platform]
            .Distinct()
            .Select(s => new ScreenPreset(s.Width, s.Height))];

    private void ApplyScreenPreset(ScreenPreset preset) => MutateFp(fp => fp with
    {
        Screen = new ScreenConfig
        {
            Mode = ValueMode.Manual,
            Width = preset.Width,
            Height = preset.Height,
        },
    });

    // --- GPU ---

    public ValueMode GpuMode
    {
        get => Fp.Gpu.Mode;
        set
        {
            if (Fp.Gpu.Mode == value) return;
            MutateFp(fp => fp with { Gpu = fp.Gpu with { Mode = value } });
            OnPropertyChanged(nameof(IsGpuManual));
        }
    }

    public bool IsGpuManual => Fp.Gpu.Mode == ValueMode.Manual;

    public string GpuVendor
    {
        get => Fp.Gpu.Vendor ?? "";
        set { if (Fp.Gpu.Vendor != value) MutateFp(fp => fp with { Gpu = fp.Gpu with { Vendor = Blank(value) } }); }
    }

    public string GpuRenderer
    {
        get => Fp.Gpu.Renderer ?? "";
        set { if (Fp.Gpu.Renderer != value) MutateFp(fp => fp with { Gpu = fp.Gpu with { Renderer = Blank(value) } }); }
    }

    public IReadOnlyList<GpuPreset> GpuPresets =>
        [.. Pools.Gpus[Fp.Platform].Select(g => new GpuPreset(g.Vendor, g.Renderer))];

    /// <summary>
    /// Apply a vendor/renderer pair together.
    /// <para>
    /// Both at once, never one: they are a matched pair, and letting the user pick a
    /// vendor independently is how "Apple Inc." ends up reporting a Radeon.
    /// </para>
    /// </summary>
    private void ApplyGpuPreset(GpuPreset preset) => MutateFp(fp => fp with
    {
        Gpu = new GpuConfig
        {
            Mode = ValueMode.Manual,
            Vendor = preset.Vendor,
            Renderer = preset.Renderer,
        },
    });

    // --- CPU / memory ---

    public ValueMode CpuMode
    {
        get => Fp.CpuCores.Mode;
        set
        {
            if (Fp.CpuCores.Mode == value) return;
            MutateFp(fp => fp with { CpuCores = fp.CpuCores with { Mode = value } });
            OnPropertyChanged(nameof(IsCpuManual));
        }
    }

    public bool IsCpuManual => Fp.CpuCores.Mode == ValueMode.Manual;

    public int? CpuCores
    {
        get => Fp.CpuCores.Value;
        set { if (Fp.CpuCores.Value != value) MutateFp(fp => fp with { CpuCores = fp.CpuCores with { Value = value } }); }
    }

    /// <summary>Core counts offered as buttons. Deduplicated: the pool is weighted, the picker is not.</summary>
    public IReadOnlyList<int> CpuChoices { get; } = [.. Pools.CpuCores.Distinct().Order()];

    public ValueMode MemoryMode
    {
        get => Fp.DeviceMemory.Mode;
        set
        {
            if (Fp.DeviceMemory.Mode == value) return;
            MutateFp(fp => fp with { DeviceMemory = fp.DeviceMemory with { Mode = value } });
            OnPropertyChanged(nameof(IsMemoryManual));
        }
    }

    public bool IsMemoryManual => Fp.DeviceMemory.Mode == ValueMode.Manual;

    public int? DeviceMemory
    {
        get => Fp.DeviceMemory.Value;
        set { if (Fp.DeviceMemory.Value != value) MutateFp(fp => fp with { DeviceMemory = fp.DeviceMemory with { Value = value } }); }
    }

    public IReadOnlyList<int> MemoryChoices { get; } = [.. Pools.DeviceMemory.Distinct().Order()];

    // --- noise ---

    public IReadOnlyList<NoiseMode> NoiseModes { get; } = Enum.GetValues<NoiseMode>();

    public NoiseMode CanvasNoise
    {
        get => Fp.Noise.Canvas;
        set { if (Fp.Noise.Canvas != value) MutateFp(fp => fp with { Noise = fp.Noise with { Canvas = value } }); }
    }

    public NoiseMode WebGlNoise
    {
        get => Fp.Noise.WebGl;
        set { if (Fp.Noise.WebGl != value) MutateFp(fp => fp with { Noise = fp.Noise with { WebGl = value } }); }
    }

    public NoiseMode AudioNoise
    {
        get => Fp.Noise.Audio;
        set { if (Fp.Noise.Audio != value) MutateFp(fp => fp with { Noise = fp.Noise with { Audio = value } }); }
    }

    public NoiseMode ClientRectsNoise
    {
        get => Fp.Noise.ClientRects;
        set { if (Fp.Noise.ClientRects != value) MutateFp(fp => fp with { Noise = fp.Noise with { ClientRects = value } }); }
    }

    /// <summary>
    /// States the per-surface limitation, rather than letting the four controls imply
    /// independence they do not have. See <see cref="NoiseConfig"/>.
    /// </summary>
    public string NoiseNote => Fp.Noise.Resolve()
        ? "Noise is on. The browser exposes a single noise switch, so any surface set "
          + "to Noise enables randomisation for all four."
        : "Noise is off for every surface. Canvas, WebGL and audio will return stable "
          + "readings, which is easier to link across sites.";

    // --- other fingerprint values ---

    public int? StorageQuotaMb
    {
        get => Fp.StorageQuotaMb;
        set { if (Fp.StorageQuotaMb != value) MutateFp(fp => fp with { StorageQuotaMb = value }); }
    }

    public int? TaskbarHeight
    {
        get => Fp.TaskbarHeight;
        set { if (Fp.TaskbarHeight != value) MutateFp(fp => fp with { TaskbarHeight = value }); }
    }

    public bool WindowsFontMetrics
    {
        get => Fp.WindowsFontMetrics;
        set { if (Fp.WindowsFontMetrics != value) MutateFp(fp => fp with { WindowsFontMetrics = value }); }
    }

    public string FontsDir
    {
        get => Fp.FontsDir ?? "";
        set { if (Fp.FontsDir != value) MutateFp(fp => fp with { FontsDir = Blank(value) }); }
    }

    public bool AllowThirdPartyCookies
    {
        get => Fp.AllowThirdPartyCookies;
        set { if (Fp.AllowThirdPartyCookies != value) MutateFp(fp => fp with { AllowThirdPartyCookies = value }); }
    }

    // --- WebRTC ---

    public IReadOnlyList<WebRtcMode> WebRtcModes { get; } = Enum.GetValues<WebRtcMode>();

    public WebRtcMode WebRtcMode
    {
        get => Fp.WebRtc.Mode;
        set
        {
            if (Fp.WebRtc.Mode == value) return;
            MutateFp(fp => fp with { WebRtc = fp.WebRtc with { Mode = value } });
            OnPropertyChanged(nameof(IsWebRtcManual));
            OnPropertyChanged(nameof(WebRtcNote));
        }
    }

    public bool IsWebRtcManual => Fp.WebRtc.Mode == Core.Model.WebRtcMode.Manual;

    public string WebRtcIp
    {
        get => Fp.WebRtc.Ip ?? "";
        set { if (Fp.WebRtc.Ip != value) MutateFp(fp => fp with { WebRtc = fp.WebRtc with { Ip = Blank(value) } }); }
    }

    /// <summary>
    /// Warns about the one WebRTC setting that quietly does nothing.
    /// <para>
    /// Auto derives the ICE address from the proxy exit IP, so without a proxy there
    /// is nothing to derive it from and the flag is not emitted at all. Silence there
    /// would leave the user believing WebRTC was handled.
    /// </para>
    /// </summary>
    public string WebRtcNote => Fp.WebRtc.Mode == Core.Model.WebRtcMode.Auto && !_draft.Proxy.IsConfigured
        ? "Auto follows the proxy's exit IP, but this profile has no proxy — so no "
          + "WebRTC address will be spoofed. Set a proxy, or pin an address manually."
        : "";

    public bool HasWebRtcNote => WebRtcNote.Length > 0;

    // ------------------------------------------------------------------
    // Proxy
    // ------------------------------------------------------------------

    public IReadOnlyList<ProxyKind> ProxyKinds { get; } = Enum.GetValues<ProxyKind>();

    /// <summary>
    /// The saved proxies offered by the picker, with a synthetic "typed in here"
    /// entry at the top.
    /// <para>
    /// Two ways to attach a proxy exist because they answer different needs. A
    /// library entry is shared: rotating a provider password is one edit rather than
    /// one per profile, and that is what anyone running more than a handful of
    /// profiles actually wants. But a one-off proxy that will never be reused should
    /// not have to be filed in the library first, so the inline fields stay.
    /// </para>
    /// </summary>
    public IReadOnlyList<SavedProxyChoice> SavedProxyChoices { get; private set; } =
        [SavedProxyChoice.Inline];

    private SavedProxyChoice _savedProxy = SavedProxyChoice.Inline;

    /// <summary>
    /// The selected library entry, or the synthetic inline entry.
    /// <para>
    /// Selecting a library entry copies its host, port and credentials into the
    /// draft as well as recording the id. That looks redundant, but it is what keeps
    /// the profile openable if the entry is later deleted: the launcher prefers the
    /// library, and falls back to the copy rather than starting the profile
    /// unproxied — which for an anti-detect profile is the one failure mode that
    /// actually costs something, since it leaks the real IP to a site that has
    /// already seen the identity behind a different one.
    /// </para>
    /// </summary>
    public SavedProxyChoice SavedProxy
    {
        get => _savedProxy;
        set
        {
            var choice = value ?? SavedProxyChoice.Inline;
            if (ReferenceEquals(_savedProxy, choice)) return;
            _savedProxy = choice;

            if (choice.Proxy is { } saved)
            {
                // Bypass and rotation stay per-profile if the profile already set
                // them: a shared entry describes the endpoint, not what one identity
                // should route around it.
                Mutate(d => d with
                {
                    Proxy = d.Proxy with
                    {
                        SavedProxyId = saved.Id,
                        Kind = saved.Kind,
                        Host = saved.Host,
                        Port = saved.Port,
                        Username = saved.Username,
                        Password = saved.Password,
                        Bypass = d.Proxy.Bypass ?? saved.Bypass,
                        RotationUrl = d.Proxy.RotationUrl ?? saved.RotationUrl,
                    },
                });
            }
            else
            {
                // Detaching leaves the copied values in place rather than clearing
                // them. The user picked that endpoint deliberately; dropping it the
                // moment they switch to manual editing would delete work.
                Mutate(d => d with { Proxy = d.Proxy with { SavedProxyId = null } });
            }

            OnPropertyChanged(nameof(SavedProxy));
            OnPropertyChanged(nameof(IsLibraryProxy));
            OnPropertyChanged(nameof(IsInlineProxy));
            OnPropertyChanged(nameof(HasProxy));
            OnPropertyChanged(nameof(WebRtcNote));
            OnPropertyChanged(nameof(HasWebRtcNote));
        }
    }

    /// <summary>True when the draft is attached to a library entry.</summary>
    public bool IsLibraryProxy => _draft.Proxy.SavedProxyId is not null;

    /// <summary>True when the endpoint fields should be editable.</summary>
    public bool IsInlineProxy => !IsLibraryProxy;

    /// <summary>Explains what a library attachment means, shown in place of the fields.</summary>
    public string LibraryProxyNote => _savedProxy.Proxy is null
        ? ""
        : $"Using \"{_savedProxy.Name}\" from the proxy library. Editing it there updates every "
          + "profile that shares it. Switch to \"Enter details here\" to give this profile its own copy.";

    public bool ShowLibraryPicker => SavedProxyChoices.Count > 1;

    public ProxyKind ProxyKind
    {
        get => _draft.Proxy.Kind;
        set
        {
            if (_draft.Proxy.Kind == value) return;
            Mutate(d => d with { Proxy = d.Proxy with { Kind = value } });
            OnPropertyChanged(nameof(HasProxy));
            OnPropertyChanged(nameof(WebRtcNote));
            OnPropertyChanged(nameof(HasWebRtcNote));
        }
    }

    public bool HasProxy => _draft.Proxy.Kind != Core.Model.ProxyKind.None;

    public string ProxyHost
    {
        get => _draft.Proxy.Host ?? "";
        set
        {
            if (_draft.Proxy.Host == value) return;
            Mutate(d => d with { Proxy = d.Proxy with { Host = Blank(value) } });
            OnPropertyChanged(nameof(WebRtcNote));
            OnPropertyChanged(nameof(HasWebRtcNote));
        }
    }

    public int? ProxyPort
    {
        get => _draft.Proxy.Port;
        set
        {
            if (_draft.Proxy.Port == value) return;
            Mutate(d => d with { Proxy = d.Proxy with { Port = value } });
            OnPropertyChanged(nameof(WebRtcNote));
            OnPropertyChanged(nameof(HasWebRtcNote));
        }
    }

    public string ProxyUsername
    {
        get => _draft.Proxy.Username ?? "";
        set { if (_draft.Proxy.Username != value) Mutate(d => d with { Proxy = d.Proxy with { Username = Blank(value) } }); }
    }

    public string ProxyPassword
    {
        get => _draft.Proxy.Password ?? "";
        set { if (_draft.Proxy.Password != value) Mutate(d => d with { Proxy = d.Proxy with { Password = Blank(value) } }); }
    }

    public string ProxyBypass
    {
        get => _draft.Proxy.Bypass ?? "";
        set { if (_draft.Proxy.Bypass != value) Mutate(d => d with { Proxy = d.Proxy with { Bypass = Blank(value) } }); }
    }

    public string ProxyRotationUrl
    {
        get => _draft.Proxy.RotationUrl ?? "";
        set { if (_draft.Proxy.RotationUrl != value) Mutate(d => d with { Proxy = d.Proxy with { RotationUrl = Blank(value) } }); }
    }

    /// <summary>
    /// Says plainly that the password is not encrypted yet.
    /// <para>
    /// The Electron build stored it in the OS keychain. That is not ported, so the
    /// password sits in plain text in profiles.json — and a user who assumes
    /// otherwise may reuse a credential they would not have. Stating it is the only
    /// honest option short of implementing the keychain.
    /// </para>
    /// </summary>
    public string ProxySecretNote =>
        "Stored as plain text in profiles.json. OS keychain storage is not ported yet.";

    // ------------------------------------------------------------------
    // Locale and geolocation
    // ------------------------------------------------------------------

    public IReadOnlyList<LocaleMode> LocaleModes { get; } = Enum.GetValues<LocaleMode>();

    public LocaleMode LocaleMode
    {
        get => _draft.Locale.Mode;
        set
        {
            if (_draft.Locale.Mode == value) return;
            Mutate(d => d with { Locale = d.Locale with { Mode = value } });
            OnPropertyChanged(nameof(IsLocaleManual));
        }
    }

    public bool IsLocaleManual => _draft.Locale.Mode == Core.Model.LocaleMode.Manual;

    public string Locale
    {
        get => _draft.Locale.Locale ?? "";
        set { if (_draft.Locale.Locale != value) Mutate(d => d with { Locale = d.Locale with { Locale = Blank(value) } }); }
    }

    public string Timezone
    {
        get => _draft.Locale.Timezone ?? "";
        set { if (_draft.Locale.Timezone != value) Mutate(d => d with { Locale = d.Locale with { Timezone = Blank(value) } }); }
    }

    public IReadOnlyList<LocalePreset> LocalePresets { get; } =
        [.. Pools.Locales.Select(l => new LocalePreset(l.Label, l.Locale, l.Timezone))];

    /// <summary>Applies a locale and its timezone together, for the reason given on <see cref="Pools.Locales"/>.</summary>
    private void ApplyLocalePreset(LocalePreset preset) => Mutate(d => d with
    {
        Locale = new LocaleConfig
        {
            Mode = Core.Model.LocaleMode.Manual,
            Locale = preset.Locale,
            Timezone = preset.Timezone,
        },
    });

    public IReadOnlyList<GeoMode> GeoModes { get; } = Enum.GetValues<GeoMode>();

    public GeoMode GeoMode
    {
        get => _draft.Geo.Mode;
        set
        {
            if (_draft.Geo.Mode == value) return;
            Mutate(d => d with { Geo = d.Geo with { Mode = value } });
            OnPropertyChanged(nameof(IsGeoManual));
        }
    }

    public bool IsGeoManual => _draft.Geo.Mode == Core.Model.GeoMode.Manual;

    public double? Latitude
    {
        get => _draft.Geo.Latitude;
        set { if (_draft.Geo.Latitude != value) Mutate(d => d with { Geo = d.Geo with { Latitude = value } }); }
    }

    public double? Longitude
    {
        get => _draft.Geo.Longitude;
        set { if (_draft.Geo.Longitude != value) Mutate(d => d with { Geo = d.Geo with { Longitude = value } }); }
    }

    public double? GeoAccuracy
    {
        get => _draft.Geo.Accuracy;
        set { if (_draft.Geo.Accuracy != value) Mutate(d => d with { Geo = d.Geo with { Accuracy = value } }); }
    }

    // ------------------------------------------------------------------
    // Behaviour
    // ------------------------------------------------------------------

    public bool Humanize
    {
        get => _draft.Behaviour.Humanize;
        set
        {
            if (_draft.Behaviour.Humanize == value) return;
            Mutate(d => d with { Behaviour = d.Behaviour with { Humanize = value } });
            OnPropertyChanged(nameof(BehaviourNote));
        }
    }

    public IReadOnlyList<HumanPresetKind> HumanPresets { get; } = Enum.GetValues<HumanPresetKind>();

    public HumanPresetKind HumanPreset
    {
        get => _draft.Behaviour.Preset;
        set { if (_draft.Behaviour.Preset != value) Mutate(d => d with { Behaviour = d.Behaviour with { Preset = value } }); }
    }

    public double? TypingDelay
    {
        get => _draft.Behaviour.TypingDelay;
        set { if (_draft.Behaviour.TypingDelay != value) Mutate(d => d with { Behaviour = d.Behaviour with { TypingDelay = value } }); }
    }

    public double? MistypeChance
    {
        get => _draft.Behaviour.MistypeChance;
        set { if (_draft.Behaviour.MistypeChance != value) Mutate(d => d with { Behaviour = d.Behaviour with { MistypeChance = value } }); }
    }

    public bool IdleBetweenActions
    {
        get => _draft.Behaviour.IdleBetweenActions;
        set { if (_draft.Behaviour.IdleBetweenActions != value) Mutate(d => d with { Behaviour = d.Behaviour with { IdleBetweenActions = value } }); }
    }

    /// <summary>
    /// Says that these settings only affect scripted input.
    /// <para>
    /// Humanisation applies to automation driving the browser. A person typing with
    /// their own hands is already human, and implying otherwise would suggest the
    /// setting protects manual browsing when it does nothing for it.
    /// </para>
    /// </summary>
    public string BehaviourNote =>
        "Applies to automated input only. It has no effect when you drive the "
        + "browser yourself, and the automation API is not ported yet.";

    // ------------------------------------------------------------------
    // Startup
    // ------------------------------------------------------------------

    /// <summary>
    /// Start pages, one per line.
    /// <para>
    /// A newline-separated block rather than a list editor: URLs are pasted in
    /// batches, and a per-row editor turns pasting twelve of them into twelve
    /// interactions.
    /// </para>
    /// </summary>
    public string StartPagesText
    {
        get => string.Join(Environment.NewLine, _draft.Startup.StartPages);
        set => Mutate(d => d with { Startup = d.Startup with { StartPages = SplitLines(value) } });
    }

    public string ExtensionPathsText
    {
        get => string.Join(Environment.NewLine, _draft.Startup.ExtensionPaths);
        set => Mutate(d => d with { Startup = d.Startup with { ExtensionPaths = SplitLines(value) } });
    }

    public string ExtraArgsText
    {
        get => string.Join(Environment.NewLine, _draft.Startup.ExtraArgs);
        set => Mutate(d => d with { Startup = d.Startup with { ExtraArgs = SplitLines(value) } });
    }

    public bool Headless
    {
        get => _draft.Startup.Headless;
        set { if (_draft.Startup.Headless != value) Mutate(d => d with { Startup = d.Startup with { Headless = value } }); }
    }

    public bool DoNotTrack
    {
        get => _draft.Startup.DoNotTrack;
        set { if (_draft.Startup.DoNotTrack != value) Mutate(d => d with { Startup = d.Startup with { DoNotTrack = value } }); }
    }

    /// <summary>
    /// Blocked localhost ports, comma separated.
    /// <para>
    /// Non-numeric entries are dropped rather than rejected: this field is edited by
    /// hand and a stray character should not block a save. Invalid values would be
    /// filtered by the flag builder anyway.
    /// </para>
    /// </summary>
    public string BlockedPortsText
    {
        get => string.Join(", ", _draft.Startup.BlockedPorts);
        set => Mutate(d => d with
        {
            Startup = d.Startup with
            {
                BlockedPorts = [.. value
                    .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => int.TryParse(p.Trim(), out var n) ? n : -1)
                    .Where(n => n is > 0 and <= 65535)
                    .Distinct()],
            },
        });
    }

    public string PortsNote =>
        "Sites can probe localhost ports to recognise your machine across profiles. "
        + "Blocking the remote-access ports above is also what a typical firewall does.";

    // ------------------------------------------------------------------
    // Advanced: MAC and device name
    // ------------------------------------------------------------------

    public IReadOnlyList<ValueMode> MacModes { get; } = [ValueMode.Real, ValueMode.Manual];

    public ValueMode MacMode
    {
        get => _draft.Mac.Mode;
        set
        {
            if (_draft.Mac.Mode == value) return;
            Mutate(d => d with { Mac = d.Mac with { Mode = value } });
            OnPropertyChanged(nameof(IsMacManual));
        }
    }

    public bool IsMacManual => _draft.Mac.Mode == ValueMode.Manual;

    public string MacAddressText
    {
        get => _draft.Mac.Address ?? "";
        set
        {
            if (_draft.Mac.Address == value) return;
            Mutate(d => d with { Mac = d.Mac with { Address = Blank(value) } });
            OnPropertyChanged(nameof(MacValidationNote));
        }
    }

    public string MacInterface
    {
        get => _draft.Mac.InterfaceName ?? "";
        set { if (_draft.Mac.InterfaceName != value) Mutate(d => d with { Mac = d.Mac with { InterfaceName = Blank(value) } }); }
    }

    /// <summary>
    /// Validates the typed address and names the vendor when it maps to a real OUI.
    /// <para>
    /// Checked here rather than only at apply time, because a MAC change needs
    /// elevation and discovering the address was malformed after a password prompt is
    /// a worse experience than being told immediately.
    /// </para>
    /// </summary>
    public string MacValidationNote
    {
        get
        {
            var text = _draft.Mac.Address;
            if (string.IsNullOrWhiteSpace(text)) return "";

            var parsed = Core.Network.MacAddress.TryParse(text);
            if (parsed is null) return "Not a valid MAC address.";
            if (!Core.Network.MacAddress.IsValidStationAddress(parsed))
                return "The multicast bit is set; most drivers reject this address.";

            var vendor = Core.Network.MacAddress.VendorOf(parsed);
            return vendor is null
                ? "Valid, but the prefix does not match a known vendor."
                : $"Valid — looks like {vendor}.";
        }
    }

    /// <summary>The blunt statement that this is not a fingerprint control.</summary>
    public string MacNote => MacSpoof.BrowserVisibilityNote;

    public string DeviceName
    {
        get => _draft.DeviceName ?? "";
        set { if (_draft.DeviceName != value) Mutate(d => d with { DeviceName = Blank(value) }); }
    }

    // ------------------------------------------------------------------
    // Summary panel and flag preview
    // ------------------------------------------------------------------

    /// <summary>
    /// The flags this profile would launch with.
    /// <para>
    /// Built from the same <see cref="FingerprintArgs.Build"/> the launcher uses, not
    /// a re-description of it. A preview assembled separately would eventually
    /// disagree with reality, and a preview that lies is worse than none.
    /// </para>
    /// </summary>
    public string FlagPreview => string.Join(Environment.NewLine, FingerprintArgs.Build(_draft));

    public string SeedLabel =>
        Fp.Seed is > 0
            ? Fp.Seed!.Value.ToString(CultureInfo.InvariantCulture)
            : $"{FingerprintArgs.SeedFromId(_draft.Id)} (derived from the profile id)";

    public string ScreenSummary => Fp.Screen.Mode switch
    {
        ValueMode.Manual when Fp.Screen.Width is > 0 && Fp.Screen.Height is > 0 =>
            $"{Fp.Screen.Width}x{Fp.Screen.Height}",
        ValueMode.Real => "Real",
        _ => "Auto",
    };

    public string GpuSummary => Fp.Gpu.Mode switch
    {
        ValueMode.Manual when !string.IsNullOrWhiteSpace(Fp.Gpu.Renderer) => Fp.Gpu.Renderer!,
        ValueMode.Real => "Real",
        _ => "Auto",
    };

    public string CpuSummary => Fp.CpuCores.Mode == ValueMode.Manual && Fp.CpuCores.Value is > 0
        ? $"{Fp.CpuCores.Value} cores"
        : Fp.CpuCores.Mode.ToString();

    public string MemorySummary => Fp.DeviceMemory.Mode == ValueMode.Manual && Fp.DeviceMemory.Value is > 0
        ? $"{Fp.DeviceMemory.Value} GB"
        : Fp.DeviceMemory.Mode.ToString();

    public string ProxySummary => _draft.Proxy.Kind == Core.Model.ProxyKind.None
        ? "No proxy"
        : $"{_draft.Proxy.Kind.ToString().ToLowerInvariant()}://{_draft.Proxy.Host}:{_draft.Proxy.Port}";

    public string LocaleSummary => _draft.Locale.Mode == Core.Model.LocaleMode.Manual
        ? $"{Blank(_draft.Locale.Locale) ?? "auto"} · {Blank(_draft.Locale.Timezone) ?? "auto"}"
        : "Follows proxy IP";

    public string PlatformSummary => DisplayNames.Of(Fp.Platform);

    public string NoiseSummary => Fp.Noise.Resolve() ? "On" : "Off";

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Empty and whitespace-only become null, so blank fields are absent rather than "".</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Split a textarea into trimmed, non-empty lines.</summary>
    private static List<string> SplitLines(string value) =>
        [.. value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)];
}

/// <summary>One tab button.</summary>
public sealed class EditorTabItem : ViewModelBase
{
    public EditorTabItem(EditorTab tab, string label)
    {
        Tab = tab;
        Label = label;
    }

    public EditorTab Tab { get; }
    public string Label { get; }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set => SetField(ref _isActive, value);
    }
}

/// <summary>A folder option, including the synthetic root.</summary>
public sealed record FolderChoice(string? Id, string Name)
{
    public static readonly FolderChoice Root = new(null, "No folder");
}

/// <summary>
/// One entry in the saved-proxy picker.
/// <para>
/// A reference type with a singleton for the inline case, so the combo box can
/// select by reference and the "not from the library" option is a real selectable
/// item rather than a null. A null selection renders as a blank row, which reads
/// as a value that failed to load instead of a deliberate choice.
/// </para>
/// </summary>
public sealed class SavedProxyChoice
{
    /// <summary>The synthetic "type the details in here" entry.</summary>
    public static readonly SavedProxyChoice Inline = new();

    private SavedProxyChoice() { }

    public SavedProxyChoice(SavedProxy proxy)
    {
        Proxy = proxy;
        // Falls back to the masked endpoint when the entry was imported without a
        // name, so the row is never blank.
        Name = string.IsNullOrWhiteSpace(proxy.Name) ? ProxyParser.Describe(proxy) : proxy.Name;
    }

    public SavedProxy? Proxy { get; }

    public string? Id => Proxy?.Id;

    public string Name { get; } = "Enter details here";

    /// <summary>What the combo box shows: the name, plus the masked endpoint beneath it.</summary>
    public string Detail => Proxy is null
        ? "This profile keeps its own copy"
        : ProxyParser.Describe(Proxy);
}

public sealed record ScreenPreset(int Width, int Height)
{
    public string Label => $"{Width}x{Height}";
}

public sealed record GpuPreset(string Vendor, string Renderer)
{
    /// <summary>
    /// A short label for the button.
    /// <para>
    /// The full renderer string runs past 70 characters, which would make every
    /// button the width of the pane. The model name is the part that identifies it.
    /// </para>
    /// </summary>
    public string Label
    {
        get
        {
            var text = Renderer;

            // Pull the model out of "ANGLE (vendor, model, api)".
            var open = text.IndexOf('(');
            if (open >= 0)
            {
                var inner = text[(open + 1)..].TrimEnd(')');
                var parts = inner.Split(',');
                if (parts.Length >= 2) text = parts[1].Trim();
            }

            return text.Length > 40 ? text[..39] + "\u2026" : text;
        }
    }
}

public sealed record LocalePreset(string Label, string Locale, string Timezone);
