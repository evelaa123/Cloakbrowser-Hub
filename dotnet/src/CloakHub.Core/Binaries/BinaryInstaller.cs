using CloakHub.Core.Import;
using CloakHub.Core.Licensing;

namespace CloakHub.Core.Binaries;

/// <summary>How far along a download is, for the progress bar.</summary>
public sealed record DownloadProgress
{
    public required string Stage { get; init; }
    public long BytesRead { get; init; }

    /// <summary>Total size, or null when the server sent no Content-Length.</summary>
    public long? TotalBytes { get; init; }

    public double? Fraction =>
        TotalBytes is > 0 ? Math.Clamp(BytesRead / (double)TotalBytes.Value, 0, 1) : null;

    public string Label => TotalBytes is > 0
        ? $"{Stage} — {Mb(BytesRead)} / {Mb(TotalBytes.Value)} MB"
        : BytesRead > 0
            ? $"{Stage} — {Mb(BytesRead)} MB"
            : Stage;

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");
}

/// <summary>What is installed right now.</summary>
public sealed record BinaryState
{
    public bool Installed { get; init; }
    public string? Version { get; init; }
    public string? Platform { get; init; }
    public BinaryTier Tier { get; init; } = BinaryTier.Free;
    public string? Path { get; init; }
    public string? CacheDir { get; init; }

    /// <summary>A newer version the server knows about, when it has been checked.</summary>
    public string? Latest { get; init; }

    public string? Error { get; init; }

    public bool UpdateAvailable =>
        Installed && Latest is { Length: > 0 } && Version is { Length: > 0 } &&
        !string.Equals(Latest, Version, StringComparison.Ordinal);
}

/// <summary>
/// Downloads, verifies and unpacks the stealth Chromium build.
/// <para>
/// The Electron build delegated all of this to the <c>cloakbrowser</c> npm package
/// via a dynamic <c>import()</c>. That is exactly the dependency this port exists to
/// remove — it required Node on the user's machine, and it meant the Hub could not
/// report a download failure in any detail because it only saw the wrapper's exit
/// status. Reimplemented here, the Hub owns the whole path and can say precisely
/// which step failed.
/// </para>
/// <para>
/// The security properties are kept identical to the wrapper's, because they are
/// not optional: the archive's SHA-256 must appear in a manifest whose Ed25519
/// signature validates against a pinned key, and that manifest must name the
/// version being installed.
/// </para>
/// </summary>
public sealed class BinaryInstaller : IDisposable
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Func<string, string?> _env;

    public BinaryInstaller(HttpClient? http = null, Func<string, string?>? env = null)
    {
        _ownsClient = http is null;

        // A generous timeout: this is a 300-400 MB download and a user on a slow
        // connection must not have it aborted at the two-minute default. Progress
        // reporting is what keeps that from feeling like a hang.
        _http = http ?? new HttpClient { Timeout = DownloadTimeout };
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>
    /// Report what is on disk right now, without touching the network.
    /// <para>
    /// Reads the cache rather than assuming the expected version, so the answer
    /// matches what a launch would actually use — including a build left by the CLI
    /// that the Hub never downloaded.
    /// </para>
    /// </summary>
    public BinaryState Inspect()
    {
        string tag;
        try
        {
            tag = BinaryCatalog.PlatformTag();
        }
        catch (Exception e)
        {
            return new BinaryState { Installed = false, Error = e.Message };
        }

        var cache = BinaryCatalog.CacheDir(_env);

        // The launcher's own resolution is reused so this can never disagree with
        // it. A panel reporting a browser the launcher will not pick is worse than
        // no panel at all.
        var resolved = Launch.ChromiumBinary.Resolve();

        if (!resolved.Found)
        {
            return new BinaryState
            {
                Installed = false,
                Platform = tag,
                CacheDir = cache,
                Error = resolved.Error,
            };
        }

        var buildDir = Path.GetDirectoryName(resolved.Path!) ?? "";

        // On macOS the executable is three levels inside the .app bundle, so the
        // build directory is not simply the parent.
        if (OperatingSystem.IsMacOS())
        {
            var appIndex = buildDir.IndexOf(
                Path.Combine("Chromium.app", "Contents", "MacOS"), StringComparison.Ordinal);
            if (appIndex > 0) buildDir = buildDir[..appIndex].TrimEnd(Path.DirectorySeparatorChar);
        }

        var dirName = Path.GetFileName(buildDir);
        var pro = dirName.EndsWith("-pro", StringComparison.Ordinal);
        var version = dirName.StartsWith("chromium-", StringComparison.Ordinal)
            ? dirName["chromium-".Length..].TrimEnd()
            : dirName;

        if (pro) version = version[..^"-pro".Length];

        return new BinaryState
        {
            Installed = true,
            Version = version,
            Platform = tag,
            Tier = pro ? BinaryTier.Pro : BinaryTier.Free,
            Path = resolved.Path,
            CacheDir = cache,
        };
    }

    /// <summary>
    /// Ensure a usable browser is installed, downloading it when it is not.
    /// <para>
    /// A valid licence gets the licensed build; everything else — no key, an
    /// unreachable server, an expired key — gets the free one. Falling back rather
    /// than failing is deliberate: refusing to install any browser because a
    /// licence check timed out would leave the user with an app that cannot open a
    /// single page.
    /// </para>
    /// </summary>
    public async Task<BinaryState> EnsureAsync(
        string? licenseKey = null,
        string? versionPin = null,
        bool preview = false,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancel = default)
    {
        try
        {
            var tier = string.IsNullOrWhiteSpace(licenseKey) ? BinaryTier.Free : BinaryTier.Pro;

            var version = versionPin
                          ?? await ResolveVersionAsync(tier, licenseKey, preview, cancel).ConfigureAwait(false)
                          ?? BinaryCatalog.DefaultVersion();

            var buildDir = BinaryCatalog.BuildDir(version, tier, _env);
            var exe = BinaryCatalog.ExecutableIn(buildDir);

            if (File.Exists(exe))
            {
                MarkExecutable(exe);
                return Inspect() with { Latest = version };
            }

            await InstallAsync(version, tier, licenseKey, progress, cancel).ConfigureAwait(false);

            // Recorded only after a successful install, so a marker never advertises
            // a build that is not actually on disk.
            WriteMarker(version, tier, preview);

            return Inspect() with { Latest = version };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return Inspect() with { Error = e.Message };
        }
    }

    /// <summary>Download, verify and unpack one specific version.</summary>
    private async Task InstallAsync(
        string version,
        BinaryTier tier,
        string? licenseKey,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancel)
    {
        var buildDir = BinaryCatalog.BuildDir(version, tier, _env);
        Directory.CreateDirectory(BinaryCatalog.CacheDir(_env));

        var archive = Path.Combine(
            BinaryCatalog.CacheDir(_env),
            $"_download_{Guid.NewGuid():N}{BinaryCatalog.ArchiveExtension()}");

        try
        {
            var urls = tier == BinaryTier.Pro
                ? [BinaryCatalog.ProArchiveUrl(version, _env)]
                : BinaryCatalog.FreeArchiveUrls(version, _env);

            await DownloadAsync(urls, archive, licenseKey, progress, cancel).ConfigureAwait(false);

            progress?.Report(new DownloadProgress { Stage = "Verifying signature" });
            await VerifyAsync(archive, version, cancel).ConfigureAwait(false);

            progress?.Report(new DownloadProgress { Stage = "Unpacking" });

            // Unpacked through the same hardened extractor the profile importer
            // uses. A release archive is not untrusted in the same way a
            // user-supplied one is, but it has just been fetched over the network
            // and there is no reason to hold it to a weaker standard.
            var extract = await ArchiveExtractor
                .ExtractAsync(archive, buildDir, cancel)
                .ConfigureAwait(false);

            if (!extract.Ok)
                throw new InvalidOperationException(extract.Error ?? "The archive could not be unpacked.");

            FlattenSingleSubdirectory(buildDir);

            var exe = BinaryCatalog.ExecutableIn(buildDir);
            if (!File.Exists(exe))
            {
                throw new InvalidOperationException(
                    $"The archive unpacked, but no browser executable was found at {exe}. " +
                    "The download may be for a different platform.");
            }

            MarkExecutable(exe);
        }
        catch
        {
            // A half-unpacked build directory is worse than none: it satisfies the
            // File.Exists check on the next run, so the user would be stuck with a
            // broken browser and no way to trigger a re-download from the UI.
            TryDelete(buildDir);
            throw;
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    private async Task DownloadAsync(
        IReadOnlyList<string> urls,
        string dest,
        string? licenseKey,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancel)
    {
        Exception? last = null;

        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                if (!string.IsNullOrWhiteSpace(licenseKey))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", licenseKey.Trim());
                    request.Headers.TryAddWithoutValidation("X-Platform", BinaryCatalog.PlatformTag());
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    last = new HttpRequestException(
                        $"{(int)response.StatusCode} {response.ReasonPhrase} from {url}");
                    continue;
                }

                var total = response.Content.Headers.ContentLength;

                await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                await using var target = File.Create(dest);

                var buffer = new byte[81920];
                long read = 0;
                var lastReport = 0L;

                while (true)
                {
                    var n = await source.ReadAsync(buffer, cancel).ConfigureAwait(false);
                    if (n == 0) break;

                    await target.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    read += n;

                    // Throttled to every 4 MB. Reporting each chunk would post tens
                    // of thousands of updates to the UI thread and make the download
                    // slower than the network.
                    if (read - lastReport >= 4 * 1024 * 1024)
                    {
                        lastReport = read;
                        progress?.Report(new DownloadProgress
                        {
                            Stage = "Downloading",
                            BytesRead = read,
                            TotalBytes = total,
                        });
                    }
                }

                progress?.Report(new DownloadProgress
                {
                    Stage = "Downloading",
                    BytesRead = read,
                    TotalBytes = total ?? read,
                });

                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                last = e;
                TryDeleteFile(dest);
            }
        }

        throw new InvalidOperationException(
            $"Could not download the browser. Last error: {last?.Message ?? "no origin responded"}");
    }

    /// <summary>
    /// Check the archive against a signed, version-bound manifest.
    /// <para>
    /// The order matters and is not interchangeable: verify the signature, then
    /// confirm the manifest names the version being installed, and only then trust
    /// a hash out of it. Hashing first would mean trusting an unauthenticated
    /// document to tell us what the correct hash is.
    /// </para>
    /// </summary>
    private async Task VerifyAsync(string archive, string version, CancellationToken cancel)
    {
        var (manifest, signature) = await FetchManifestAsync(version, cancel).ConfigureAwait(false);

        if (manifest is null || signature is null)
        {
            throw new InvalidOperationException(
                $"Could not fetch a signed SHA256SUMS for {version}. The download cannot be " +
                "verified, so it will not be installed.");
        }

        if (!ReleaseManifest.VerifySignature(manifest, signature))
        {
            throw new InvalidOperationException(
                "The release manifest's signature did not match any trusted key. The browser " +
                "download could not be confirmed as authentic and has been discarded.");
        }

        var text = System.Text.Encoding.UTF8.GetString(manifest);

        var named = ReleaseManifest.ParseVersion(text);
        if (named is not null && !string.Equals(named, version, StringComparison.Ordinal))
        {
            // A correctly-signed manifest for a *different* release. Genuine bytes,
            // wrong release — this is the downgrade replay the version line exists
            // to catch.
            throw new InvalidOperationException(
                $"The signed manifest is for version {named}, not {version}. Refusing to install.");
        }

        var sums = ReleaseManifest.ParseChecksums(text);
        var archiveName = BinaryCatalog.ArchiveName();

        if (!sums.TryGetValue(archiveName, out var expected))
        {
            throw new InvalidOperationException(
                $"The signed manifest has no entry for {archiveName}. Refusing to install an " +
                "archive it does not cover.");
        }

        var actual = await ReleaseManifest.HashFileAsync(archive, cancel).ConfigureAwait(false);

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The downloaded browser does not match its signed checksum. It may be corrupted " +
                "or tampered with, and has been discarded.");
        }
    }

    private async Task<(byte[]? Manifest, byte[]? Signature)> FetchManifestAsync(
        string version,
        CancellationToken cancel)
    {
        foreach (var basis in BinaryCatalog.ManifestBases(version, _env))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                cts.CancelAfter(MetadataTimeout);

                // Both files must come from the same origin so the signature always
                // certifies the exact manifest bytes fetched beside it. Mixing
                // origins would let one server's manifest be "verified" by another's
                // signature only by coincidence — or by design.
                var manifest = await _http.GetByteArrayAsync($"{basis}/SHA256SUMS", cts.Token).ConfigureAwait(false);
                var signature = await _http.GetByteArrayAsync($"{basis}/SHA256SUMS.sig", cts.Token).ConfigureAwait(false);

                return (manifest, signature);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next origin.
            }
        }

        return (null, null);
    }

    /// <summary>
    /// The newest version for a tier, or null when the server cannot say.
    /// </summary>
    public async Task<string?> ResolveVersionAsync(
        BinaryTier tier,
        string? licenseKey,
        bool preview,
        CancellationToken cancel = default)
    {
        var url = tier == BinaryTier.Pro
            ? BinaryCatalog.ProVersionUrl(preview, _env)
            : BinaryCatalog.FreeVersionUrl(preview, _env);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(MetadataTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(licenseKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", licenseKey.Trim());
            }

            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = (await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)).Trim();

            // The endpoint has returned both a bare version string and a JSON object
            // across server versions, so both are accepted rather than assuming the
            // current shape and failing the whole install on a format change.
            if (body.StartsWith('{'))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                foreach (var name in new[] { "version", "latest" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var el) &&
                        el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return Clean(el.GetString());
                    }
                }

                return null;
            }

            return Clean(body);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Unknown. The caller falls back to this build's expected version rather
            // than refusing to install anything.
            return null;
        }
    }

    private static string? Clean(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        var trimmed = version.Trim().Trim('"');

        // Anything that is not a dotted numeric version is a server error page or
        // an unexpected payload, and turning it into a directory name would create
        // a cache entry nothing can ever find again.
        return trimmed.Length > 0 && trimmed.All(c => char.IsAsciiDigit(c) || c == '.')
            ? trimmed
            : null;
    }

    private void WriteMarker(string version, BinaryTier tier, bool preview)
    {
        try
        {
            File.WriteAllText(BinaryCatalog.VersionMarker(tier, preview, _env), version + "\n");
        }
        catch
        {
            // The marker is a hint, not state the app depends on — Inspect() reads
            // the directory listing, so a missing marker costs nothing.
        }
    }

    /// <summary>
    /// Collapse <c>&lt;build&gt;/cloakbrowser-linux-x64/chrome</c> to <c>&lt;build&gt;/chrome</c>.
    /// <para>
    /// Release archives have been published both with and without a top-level
    /// wrapper directory. Without this the executable ends up one level deeper than
    /// every path in the app expects, and the install "succeeds" into a browser
    /// nothing can find.
    /// </para>
    /// </summary>
    private static void FlattenSingleSubdirectory(string buildDir)
    {
        try
        {
            if (File.Exists(BinaryCatalog.ExecutableIn(buildDir))) return;

            var entries = Directory.GetFileSystemEntries(buildDir);
            if (entries.Length != 1 || !Directory.Exists(entries[0])) return;

            var inner = entries[0];
            foreach (var item in Directory.GetFileSystemEntries(inner))
            {
                var target = Path.Combine(buildDir, Path.GetFileName(item));
                if (Directory.Exists(item)) Directory.Move(item, target);
                else File.Move(item, target, overwrite: true);
            }

            Directory.Delete(inner, recursive: true);
        }
        catch
        {
            // If this fails the executable check below reports it far more clearly
            // than an exception from a move would.
        }
    }

    private static void MarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            // Zip does not preserve the executable bit, and a browser without it
            // fails at exec time with a permission error that says nothing about
            // the real cause.
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Filesystem without Unix modes; the launch will report it if it matters.
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // Best effort — a leftover temp archive wastes disk but breaks nothing.
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
