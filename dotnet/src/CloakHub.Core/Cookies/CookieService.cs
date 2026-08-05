namespace CloakHub.Core.Cookies;

/// <summary>Outcome of importing cookies into a profile.</summary>
/// <param name="Ok">Whether anything was written.</param>
/// <param name="Count">Cookies in the profile afterwards.</param>
/// <param name="Imported">Cookies parsed from the payload.</param>
/// <param name="Files">How many files contributed (0 for a paste).</param>
/// <param name="Domains">Domains now present.</param>
/// <param name="AuthHints">Services the profile appears to hold a session for.</param>
/// <param name="MissingCritical">
/// Session cookies that were in the payload but did not survive the write.
/// </param>
/// <param name="Error">Why nothing was written, phrased for the user.</param>
public sealed record CookieImportResult(
    bool Ok,
    int Count,
    int Imported,
    int Files,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> AuthHints,
    IReadOnlyList<string> MissingCritical,
    string? Error = null)
{
    public static CookieImportResult Failed(string error) =>
        new(false, 0, 0, 0, [], [], [], error);
}

/// <summary>
/// Cookie import and export for a profile.
/// <para>
/// The layer between the parsers and Chromium's own store. It exists mainly to
/// enforce two things the UI must not be trusted to remember: that a profile's
/// cookies are never written while its browser is running, and that a partial
/// session is reported rather than silently accepted.
/// </para>
/// </summary>
public sealed class CookieService(Func<string, string> userDataDirFor, Func<string, bool> isRunning)
{
    /// <summary>
    /// Import from pasted text.
    /// </summary>
    /// <param name="profileId">Profile to import into.</param>
    /// <param name="text">The pasted payload, in any supported format.</param>
    /// <param name="replace">Clear existing cookies first.</param>
    /// <param name="domain">Domain to attach, for header-format input only.</param>
    public CookieImportResult ImportText(
        string profileId,
        string text,
        bool replace = false,
        string? domain = null)
    {
        if (isRunning(profileId))
        {
            // Chromium holds the cookie database open and keeps its own in-memory copy,
            // which it flushes over ours at shutdown. Writing now would appear to work
            // and then silently vanish, so it is refused with the reason.
            return CookieImportResult.Failed(
                "Close this profile's browser before importing cookies — Chromium overwrites " +
                "the cookie store when it exits.");
        }

        var validation = CookieValidator.Validate(text);
        if (!validation.Ok)
            return CookieImportResult.Failed(validation.Error ?? "No cookies found.");

        var parsed = CookieParser.Parse(text, domain);
        return Write(profileId, parsed, replace, files: 0);
    }

    /// <summary>
    /// Import from one or more files. Unreadable or unrecognised files are skipped
    /// rather than aborting the batch: a user selecting twenty exports should not
    /// lose nineteen good ones to a single bad file, and the count reports the gap.
    /// </summary>
    public CookieImportResult ImportFiles(
        string profileId,
        IEnumerable<string> paths,
        bool replace = false,
        string? domain = null)
    {
        if (isRunning(profileId))
        {
            return CookieImportResult.Failed(
                "Close this profile's browser before importing cookies — Chromium overwrites " +
                "the cookie store when it exits.");
        }

        var sets = new List<IEnumerable<BrowserCookie>>();
        var files = 0;

        foreach (var path in paths)
        {
            try
            {
                var parsed = CookieParser.Parse(File.ReadAllText(path), domain);
                if (parsed.Count == 0) continue;

                files++;
                sets.Add(parsed);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Skip and keep going.
            }
        }

        if (files == 0)
            return CookieImportResult.Failed("None of the selected files contained readable cookies.");

        var merged = CookieParser.Merge([.. sets]);
        return Write(profileId, merged, replace, files);
    }

    /// <summary>Cookies currently stored for a profile.</summary>
    public List<BrowserCookie> Read(string profileId) =>
        ChromiumCookieDb.Read(userDataDirFor(profileId));

    /// <summary>How many cookies a profile holds.</summary>
    public int Count(string profileId) =>
        ChromiumCookieDb.Count(userDataDirFor(profileId));

    /// <summary>
    /// Export a profile's cookies to a file.
    /// </summary>
    /// <returns>How many cookies were written.</returns>
    public int Export(string profileId, string destination, CookieFormat format)
    {
        var cookies = Read(profileId);

        var text = format == CookieFormat.Netscape
            ? CookieWriter.ToNetscape(cookies)
            : CookieWriter.ToJson(cookies);

        File.WriteAllText(destination, text);
        return cookies.Count;
    }

    /// <summary>Remove every cookie from a profile.</summary>
    public bool Clear(string profileId)
    {
        if (isRunning(profileId)) return false;

        ChromiumCookieDb.Write(userDataDirFor(profileId), [], replace: true);
        return true;
    }

    private CookieImportResult Write(
        string profileId,
        IReadOnlyList<BrowserCookie> parsed,
        bool replace,
        int files)
    {
        var dir = userDataDirFor(profileId);

        try
        {
            ChromiumCookieDb.Write(dir, parsed, replace);
        }
        catch (Exception e)
        {
            return CookieImportResult.Failed($"Could not write the cookie store: {e.Message}");
        }

        // Read back rather than trusting the insert count. Chromium's unique index
        // silently collapses rows that differ only in a field it does not key on, so
        // "rows written" and "cookies present" are not the same number — and the one
        // that matters to the user is what the browser will actually see.
        var stored = ChromiumCookieDb.Read(dir);
        var storedNames = stored.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var domains = stored
            .Select(c => c.HostOnlyDomain)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var parsedNames = parsed.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();

        foreach (var (service, critical) in CookieValidator.CriticalCookies)
        {
            // Only judge a service whose session was actually in the payload; a file
            // with no Google cookies is not "missing" all of Google's.
            var relevant = critical.Where(parsedNames.Contains).ToList();
            if (relevant.Count == 0) continue;

            missing.AddRange(relevant
                .Where(n => !storedNames.Contains(n))
                .Select(n => $"{service}: {n}"));
        }

        return new CookieImportResult(
            Ok: true,
            Count: stored.Count,
            Imported: parsed.Count,
            Files: files,
            Domains: domains,
            AuthHints: CookieValidator.DetectServices(storedNames, domains),
            MissingCritical: missing);
    }
}
