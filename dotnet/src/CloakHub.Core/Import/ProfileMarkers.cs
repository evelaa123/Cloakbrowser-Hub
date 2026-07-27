using System.Text.Json;

namespace CloakHub.Core.Import;

/// <summary>
/// The filesystem signals that say "this directory is a browser profile", and the
/// labels read out of it.
/// <para>
/// Shared by both discovery paths — the standard-locations scan and the
/// pick-any-folder scan — so the two cannot disagree about what a profile is. When
/// they diverged in the Electron build the symptom was a profile that appeared in
/// one list and not the other, which reads to the user as the app losing it.
/// </para>
/// </summary>
public static class ProfileMarkers
{
    /// <summary>A Chromium profile always has a <c>Preferences</c> file.</summary>
    public static bool IsChromium(string dir) => File.Exists(Path.Combine(dir, "Preferences"));

    /// <summary>A Firefox profile always has <c>prefs.js</c>.</summary>
    public static bool IsFirefox(string dir) => File.Exists(Path.Combine(dir, "prefs.js"));

    /// <summary>
    /// Does the profile carry a cookie store?
    /// <para>
    /// Chromium moved the file into <c>Network/</c> in M96 and both layouts are
    /// still in the wild — a profile restored from an old backup keeps the flat
    /// one — so both are checked rather than assuming the current version.
    /// </para>
    /// </summary>
    public static bool HasCookies(string dir) =>
        File.Exists(Path.Combine(dir, "Cookies")) ||
        File.Exists(Path.Combine(dir, "Network", "Cookies")) ||
        File.Exists(Path.Combine(dir, "cookies.sqlite"));

    /// <summary>
    /// Guess the browser from the path.
    /// <para>
    /// Purely for the label. An unrecognised path yields "Imported" rather than a
    /// rejection: the import reads marker files, not the vendor name, so refusing
    /// an unknown path would only block profiles that would have imported fine —
    /// a portable build, a restored backup, a fork nobody has heard of.
    /// </para>
    /// </summary>
    public static string GuessBrowser(string dir)
    {
        var p = dir.Replace('\\', '/').ToLowerInvariant();

        // Ordered most-specific first. Brave and Edge both contain "chrom" in
        // their real install paths, so a "chromium" test placed earlier would
        // relabel every Brave profile.
        if (p.Contains("bravesoftware") || p.Contains("brave-browser")) return "Brave";
        if (p.Contains("microsoft/edge") || p.Contains("microsoft edge")) return "Edge";
        if (p.Contains("google/chrome") || p.Contains("google-chrome")) return "Chrome";
        if (p.Contains("chromium")) return "Chromium";
        if (p.Contains("opera")) return "Opera";
        if (p.Contains("vivaldi")) return "Vivaldi";
        if (p.Contains("yandex")) return "Yandex";
        if (p.Contains("firefox") || p.Contains("mozilla")) return "Firefox";
        return "Imported";
    }

    /// <summary>
    /// Friendly name from Chromium's own <c>Preferences</c>, falling back to the folder.
    /// <para>
    /// The folder name is "Default" or "Profile 3" for everyone, so without this a
    /// user with five Chrome profiles gets five rows they cannot tell apart. The
    /// signed-in email is the part that actually identifies which account is
    /// which, so it is preferred over the display name and included alongside it.
    /// </para>
    /// </summary>
    public static string ChromiumLabel(string dir)
    {
        var folder = LastSegment(dir);
        try
        {
            // A profile that has been open for months has a Preferences file in
            // the hundreds of KB. Reading it whole is still cheaper than the
            // directory walk that got here, so a stream parser would be
            // complexity for no measured gain.
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "Preferences")));
            var root = doc.RootElement;

            string? name = null;
            if (root.TryGetProperty("profile", out var profile) &&
                profile.ValueKind == JsonValueKind.Object &&
                profile.TryGetProperty("name", out var nameEl) &&
                nameEl.ValueKind == JsonValueKind.String)
            {
                name = Blank(nameEl.GetString());
            }

            string? email = null;
            if (root.TryGetProperty("account_info", out var accounts) &&
                accounts.ValueKind == JsonValueKind.Array)
            {
                foreach (var account in accounts.EnumerateArray())
                {
                    if (account.ValueKind != JsonValueKind.Object) continue;
                    if (!account.TryGetProperty("email", out var emailEl)) continue;
                    if (emailEl.ValueKind != JsonValueKind.String) continue;
                    email = Blank(emailEl.GetString());
                    if (email is not null) break;
                }
            }

            if (name is not null && email is not null) return $"{name} ({email})";
            return email ?? name ?? folder;
        }
        catch
        {
            // A truncated or non-JSON Preferences file is exactly what a partial
            // archive extraction produces, and it is not a reason to hide the
            // profile: the folder name is still a usable label and the clone will
            // copy whatever is really there.
            return folder;
        }
    }

    /// <summary>
    /// Locale hint from a Chromium profile, for pre-filling the new profile's locale.
    /// <para>
    /// <c>accept_languages</c> is preferred over <c>app_locale</c> because it is
    /// what the browser actually sent on the wire — which is the value a site has
    /// already associated with the account being imported. The UI language is only
    /// a fallback.
    /// </para>
    /// </summary>
    public static string? ChromiumLocale(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "Preferences")));
            if (!doc.RootElement.TryGetProperty("intl", out var intl) ||
                intl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (intl.TryGetProperty("accept_languages", out var accept) &&
                accept.ValueKind == JsonValueKind.String)
            {
                var first = accept.GetString()?.Split(',').FirstOrDefault()?.Trim();
                if (!string.IsNullOrEmpty(first)) return first;
            }

            if (intl.TryGetProperty("app_locale", out var app) &&
                app.ValueKind == JsonValueKind.String)
            {
                // Chromium writes the UI locale with an underscore ("en_GB"); the
                // rest of the app, and every web API, uses BCP 47.
                return Blank(app.GetString()?.Replace('_', '-'));
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Last path segment, tolerating a trailing separator.
    /// <para>
    /// <c>Path.GetFileName</c> returns empty for "/a/b/", which is exactly what a
    /// path assembled from a folder picker or a zip entry often looks like — the
    /// resulting blank label would then hide the profile's identity in the list.
    /// </para>
    /// </summary>
    public static string LastSegment(string dir)
    {
        var trimmed = dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
