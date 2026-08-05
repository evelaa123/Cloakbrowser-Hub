using CloakHub.Core.Model;
using CloakHub.Core.Network;

namespace CloakHub.Core.Storage;

/// <summary>
/// The saved proxy library.
/// <para>
/// Kept separate from profiles because the relationship is many-to-one: a provider
/// sells a pool, and a user assigns the same entry to a dozen profiles. Copying the
/// endpoint into each profile would mean editing a dozen records when a password
/// rotates, and would scatter credentials across the file.
/// </para>
/// <para>
/// Its own file rather than a section of <c>profiles.json</c>, so a bulk import of
/// two hundred proxies never rewrites — and never risks — the profile data.
/// </para>
/// </summary>
public sealed class ProxyStore(string path)
{
    // Same reasoning as ProfileStore: the UI can fire several mutations before the
    // first completes, and read-modify-write on a shared list loses entries.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<SavedProxy> _proxies = [];
    private bool _loaded;

    /// <summary>Where a corrupt file was moved on the last load, if it happened.</summary>
    public string? Quarantined { get; private set; }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>A snapshot, newest first.</summary>
    public IReadOnlyList<SavedProxy> List()
    {
        EnsureLoaded();
        return [.. _proxies];
    }

    public SavedProxy? Get(string id)
    {
        EnsureLoaded();
        return _proxies.FirstOrDefault(p => p.Id == id);
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;

        _gate.Wait();
        try
        {
            if (_loaded) return;

            _proxies = JsonStore.Read<List<SavedProxy>>(path, [], out var quarantined) ?? [];
            Quarantined = quarantined;
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    // ------------------------------------------------------------------
    // Writing
    // ------------------------------------------------------------------

    /// <summary>Add one entry, naming it if the caller did not.</summary>
    public SavedProxy Add(ProxyConfig config, string? name = null)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var saved = Materialise(config, name, list);
            list.Insert(0, saved);
            return saved;
        });
    }

    /// <summary>
    /// Add many at once, skipping ones already in the library.
    /// <para>
    /// Bulk imports overlap constantly — a user re-pastes a provider list to pick up
    /// ten new proxies and brings the previous two hundred with it. Silently
    /// duplicating them would double the library on every refresh, so identical
    /// endpoints are counted as skipped rather than added again.
    /// </para>
    /// </summary>
    public ProxyImportResult AddRange(IEnumerable<ProxyConfig> configs)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var added = new List<SavedProxy>();
            var skipped = 0;

            foreach (var config in configs)
            {
                if (list.Any(existing => SameEndpoint(existing, config)))
                {
                    skipped++;
                    continue;
                }

                var saved = Materialise(config, null, list);
                list.Insert(0, saved);
                added.Add(saved);
            }

            return new ProxyImportResult(added, skipped);
        });
    }

    /// <summary>
    /// Replace an entry.
    /// <para>
    /// <c>CreatedAt</c> is preserved from the stored copy: it orders the library and
    /// is not something an edit form should be able to reset.
    /// </para>
    /// </summary>
    public SavedProxy? Update(SavedProxy proxy)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var index = list.FindIndex(p => p.Id == proxy.Id);
            if (index < 0) return null;

            var stored = proxy with { CreatedAt = list[index].CreatedAt };
            list[index] = stored;
            return stored;
        });
    }

    /// <summary>
    /// Attach a check result.
    /// <para>
    /// Separate from <see cref="Update"/> so a background check cannot overwrite an
    /// edit the user made while it was running. A check knows the exit IP; it does
    /// not know the host was retyped two seconds ago.
    /// </para>
    /// </summary>
    public SavedProxy? RecordCheck(string id, ProxyCheckResult result)
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var index = list.FindIndex(p => p.Id == id);
            if (index < 0) return null;

            var stored = list[index] with { LastCheck = result };
            list[index] = stored;
            return stored;
        });
    }

    public bool Remove(string id)
    {
        EnsureLoaded();
        return Mutate(list => list.RemoveAll(p => p.Id == id) > 0);
    }

    /// <summary>Empty the library.</summary>
    public int Clear()
    {
        EnsureLoaded();
        return Mutate(list =>
        {
            var count = list.Count;
            list.Clear();
            return count;
        });
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Whether two entries point at the same endpoint.
    /// <para>
    /// Compared on host, port and username but not password. A rotated password is
    /// the same proxy with new credentials, and treating it as a new entry would
    /// leave the stale one behind for the user to find and delete.
    /// </para>
    /// </summary>
    internal static bool SameEndpoint(ProxyConfig a, ProxyConfig b) =>
        a.Kind == b.Kind
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port
        && string.Equals(a.Username ?? "", b.Username ?? "", StringComparison.Ordinal);

    private static SavedProxy Materialise(ProxyConfig config, string? name, List<SavedProxy> list)
    {
        var label = string.IsNullOrWhiteSpace(name)
            ? ProxyParser.Describe(config)
            : name.Trim();

        return new SavedProxy
        {
            Id = Guid.NewGuid().ToString(),
            Name = UniqueName(list, label),
            Kind = config.Kind,
            Host = config.Host,
            Port = config.Port,
            Username = config.Username,
            Password = config.Password,
            Bypass = config.Bypass,
            RotationUrl = config.RotationUrl,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }

    /// <summary>
    /// Suffix a name until it is unique.
    /// <para>
    /// Names are how a user picks a proxy from a dropdown, and two identical entries
    /// there is a choice they cannot make.
    /// </para>
    /// </summary>
    private static string UniqueName(List<SavedProxy> list, string desired)
    {
        var name = string.IsNullOrWhiteSpace(desired) ? "Proxy" : desired.Trim();
        if (!list.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return name;

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!list.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
    }

    /// <summary>Mutate under the gate and persist the result.</summary>
    private T Mutate<T>(Func<List<SavedProxy>, T> action)
    {
        _gate.Wait();
        try
        {
            var result = action(_proxies);
            JsonStore.Write(path, _proxies);
            return result;
        }
        finally { _gate.Release(); }
    }
}

/// <summary>What a bulk import produced.</summary>
public sealed record ProxyImportResult(IReadOnlyList<SavedProxy> Added, int Skipped)
{
    public int AddedCount => Added.Count;
}
