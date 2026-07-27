using CloakHub.Core.Model;
using CloakHub.Core.Storage;
using Xunit;

namespace CloakHub.Core.Tests;

/// <summary>The saved proxy library.</summary>
public sealed class ProxyStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cloakhub-proxystore-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "proxies.json");

    public ProxyStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private ProxyStore New() => new(Path_);

    private static ProxyConfig Config(string host = "1.2.3.4", int port = 8080) => new()
    {
        Kind = ProxyKind.Http,
        Host = host,
        Port = port,
    };

    // ------------------------------------------------------------------
    // Basics
    // ------------------------------------------------------------------

    [Fact]
    public void A_missing_file_is_an_empty_library_not_an_error()
    {
        // This is what first launch looks like, and it must not be reported as a
        // problem.
        Assert.Empty(New().List());
    }

    [Fact]
    public void An_added_proxy_survives_a_reload()
    {
        New().Add(Config());

        var reloaded = New().List();

        Assert.Single(reloaded);
        Assert.Equal("1.2.3.4", reloaded[0].Host);
    }

    [Fact]
    public void An_unnamed_proxy_is_named_after_its_endpoint()
    {
        // A blank name in a dropdown is a choice the user cannot make.
        var saved = New().Add(Config());
        Assert.Contains("1.2.3.4:8080", saved.Name);
    }

    [Fact]
    public void The_generated_name_never_contains_the_password()
    {
        // The name is shown everywhere, including in profile dropdowns.
        var saved = New().Add(Config() with { Username = "alice", Password = "s3cret" });

        Assert.DoesNotContain("s3cret", saved.Name);
        Assert.DoesNotContain("alice", saved.Name);
    }

    [Fact]
    public void Duplicate_names_are_suffixed()
    {
        var store = New();
        var a = store.Add(Config(), "Provider");
        var b = store.Add(Config("5.6.7.8"), "Provider");

        Assert.Equal("Provider", a.Name);
        Assert.Equal("Provider (2)", b.Name);
    }

    [Fact]
    public void Newest_entries_come_first()
    {
        // The one just added is the one the user is about to act on.
        var store = New();
        store.Add(Config("1.1.1.1"));
        store.Add(Config("2.2.2.2"));

        Assert.Equal("2.2.2.2", store.List()[0].Host);
    }

    // ------------------------------------------------------------------
    // Bulk import
    // ------------------------------------------------------------------

    [Fact]
    public void A_bulk_import_skips_entries_already_present()
    {
        // Users re-paste a provider list to pick up ten new proxies and bring the
        // previous two hundred with it. Adding them again would double the library
        // on every refresh.
        var store = New();
        store.Add(Config("1.1.1.1"));

        var result = store.AddRange([Config("1.1.1.1"), Config("2.2.2.2")]);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Duplicates_within_one_paste_are_also_collapsed()
    {
        var result = New().AddRange([Config("1.1.1.1"), Config("1.1.1.1")]);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void The_same_host_on_a_different_port_is_a_different_proxy()
    {
        var result = New().AddRange([Config("1.1.1.1", 8080), Config("1.1.1.1", 9090)]);
        Assert.Equal(2, result.AddedCount);
    }

    [Fact]
    public void The_same_endpoint_with_a_different_user_is_a_different_proxy()
    {
        // Provider pools commonly issue one host with a username per session.
        var result = New().AddRange([
            Config() with { Username = "alice" },
            Config() with { Username = "bob" },
        ]);

        Assert.Equal(2, result.AddedCount);
    }

    [Fact]
    public void A_rotated_password_is_the_same_proxy_not_a_new_one()
    {
        // Treating it as new would leave the stale entry behind for the user to find
        // and delete by hand.
        var store = New();
        store.Add(Config() with { Username = "alice", Password = "old" });

        var result = store.AddRange([Config() with { Username = "alice", Password = "new" }]);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Host_comparison_ignores_case()
    {
        var store = New();
        store.Add(Config("Gate.Provider.COM"));

        var result = store.AddRange([Config("gate.provider.com")]);
        Assert.Equal(1, result.Skipped);
    }

    // ------------------------------------------------------------------
    // Updating
    // ------------------------------------------------------------------

    [Fact]
    public void An_update_preserves_the_creation_time()
    {
        // It orders the library, and an edit form should not be able to reset it.
        var store = New();
        var saved = store.Add(Config());

        var updated = store.Update(saved with { CreatedAt = 0, Host = "9.9.9.9" });

        Assert.NotNull(updated);
        Assert.Equal(saved.CreatedAt, updated.CreatedAt);
        Assert.Equal("9.9.9.9", updated.Host);
    }

    [Fact]
    public void Updating_something_that_is_gone_reports_it_rather_than_recreating_it()
    {
        var store = New();
        var saved = store.Add(Config());
        store.Remove(saved.Id);

        Assert.Null(store.Update(saved));
    }

    [Fact]
    public void A_check_result_is_recorded_without_touching_the_endpoint()
    {
        // Separate from Update so a background check cannot overwrite an edit the
        // user made while it was running.
        var store = New();
        var saved = store.Add(Config());

        var updated = store.RecordCheck(saved.Id, new ProxyCheckResult
        {
            Ok = true,
            Ip = "9.8.7.6",
            CheckedAt = 1234,
        });

        Assert.NotNull(updated);
        Assert.True(updated.LastCheck!.Ok);
        Assert.Equal("9.8.7.6", updated.LastCheck.Ip);
        Assert.Equal("1.2.3.4", updated.Host);
    }

    [Fact]
    public void A_check_result_survives_a_reload()
    {
        // Otherwise the library presents every proxy as unverified after a restart,
        // and the user re-checks work that was already done.
        var store = New();
        var saved = store.Add(Config());
        store.RecordCheck(saved.Id, new ProxyCheckResult { Ok = true, Ip = "9.8.7.6" });

        Assert.Equal("9.8.7.6", New().List()[0].LastCheck?.Ip);
    }

    [Fact]
    public void Recording_a_check_for_something_gone_reports_it()
    {
        Assert.Null(New().RecordCheck("nope", new ProxyCheckResult { Ok = true }));
    }

    // ------------------------------------------------------------------
    // Removal
    // ------------------------------------------------------------------

    [Fact]
    public void Removing_reports_whether_anything_was_there()
    {
        var store = New();
        var saved = store.Add(Config());

        Assert.True(store.Remove(saved.Id));
        Assert.False(store.Remove(saved.Id));
    }

    [Fact]
    public void Clearing_reports_how_many_went()
    {
        var store = New();
        store.AddRange([Config("1.1.1.1"), Config("2.2.2.2")]);

        Assert.Equal(2, store.Clear());
        Assert.Empty(store.List());
        Assert.Equal(0, store.Clear());
    }

    // ------------------------------------------------------------------
    // Robustness
    // ------------------------------------------------------------------

    [Fact]
    public void A_corrupt_file_is_quarantined_rather_than_deleted()
    {
        // The user gets a working app back and the bytes stay on disk. Starting from
        // empty silently is indistinguishable from having thrown their work away.
        File.WriteAllText(Path_, "{ not json");

        var store = New();

        Assert.Empty(store.List());
        Assert.NotNull(store.Quarantined);
        Assert.True(File.Exists(store.Quarantined));
    }

    [Fact]
    public void The_list_is_a_copy_not_the_live_collection()
    {
        // Handing out the live list would let a caller mutate the store without
        // going through the save, so the UI would show state never persisted.
        var store = New();
        store.Add(Config());

        var first = store.List();
        store.Add(Config("5.6.7.8"));

        Assert.Single(first);
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Concurrent_adds_do_not_lose_entries()
    {
        // Read-modify-write on a shared list is exactly the shape that silently
        // drops entries, and the UI can fire several commands before the first
        // finishes.
        var store = New();

        Parallel.For(0, 40, i => store.Add(Config($"10.0.0.{i}")));

        Assert.Equal(40, store.List().Count);
        Assert.Equal(40, New().List().Count);
    }
}
