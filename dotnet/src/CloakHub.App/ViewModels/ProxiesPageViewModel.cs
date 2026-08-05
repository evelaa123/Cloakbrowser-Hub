using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CloakHub.Core.Model;
using CloakHub.Core.Network;
using CloakHub.Core.Storage;

namespace CloakHub.App.ViewModels;

/// <summary>
/// The proxy library.
/// <para>
/// Exists so an endpoint is entered once and referenced by many profiles. The
/// alternative — retyping the same provider details into every profile — makes a
/// password rotation an afternoon of editing, and scatters credentials across the
/// profile file where they are harder to audit.
/// </para>
/// </summary>
public sealed class ProxiesPageViewModel : ViewModelBase
{
    private readonly ProxyStore _store;
    private readonly ProfileStore _profiles;
    private readonly ToastHost _toasts;
    private readonly ProxyChecker _checker = new();

    /// <summary>Cancels an in-flight bulk check when the user asks it to stop.</summary>
    private CancellationTokenSource? _checkAll;

    public ProxiesPageViewModel(ProxyStore store, ProfileStore profiles, ToastHost toasts)
    {
        _store = store;
        _profiles = profiles;
        _toasts = toasts;

        ImportCommand = new AsyncRelayCommand(ImportAsync, onError: toasts.Error);
        CheckAllCommand = new AsyncRelayCommand(CheckAllAsync, onError: toasts.Error);
        CancelCheckCommand = new RelayCommand(CancelCheck);
        ClearCommand = new RelayCommand(Clear);

        Refresh();
    }

    public ObservableCollection<ProxyRowViewModel> Rows { get; } = [];

    public AsyncRelayCommand ImportCommand { get; }
    public AsyncRelayCommand CheckAllCommand { get; }
    public RelayCommand CancelCheckCommand { get; }
    public RelayCommand ClearCommand { get; }

    // ------------------------------------------------------------------
    // Import
    // ------------------------------------------------------------------

    private string _importText = "";

    /// <summary>
    /// The paste box.
    /// <para>
    /// A free-text area rather than host/port/user/pass fields, because the input is
    /// always a block a provider generated. Making the user split two hundred lines
    /// into four fields each would be the slowest possible way to do this.
    /// </para>
    /// </summary>
    public string ImportText
    {
        get => _importText;
        set
        {
            if (!SetField(ref _importText, value)) return;
            OnPropertyChanged(nameof(ImportPreview));
            OnPropertyChanged(nameof(CanImport));
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// A live count of what the paste will produce.
    /// <para>
    /// Shown before the button is pressed. Parsing is the step most likely to go
    /// wrong — a provider format the parser does not know reads as a wall of failed
    /// lines — and finding that out before committing is much better than after.
    /// </para>
    /// </summary>
    public string ImportPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_importText)) return "";

            var parsed = ProxyParser.ParseList(_importText);

            if (parsed.Proxies.Count == 0)
                return $"No proxies recognised in {parsed.Failed.Count} line(s).";

            return parsed.HasFailures
                ? $"{parsed.Proxies.Count} recognised, {parsed.Failed.Count} line(s) not understood."
                : $"{parsed.Proxies.Count} proxies recognised.";
        }
    }

    public bool CanImport => !string.IsNullOrWhiteSpace(_importText);

    private Task ImportAsync()
    {
        var parsed = ProxyParser.ParseList(_importText);

        if (parsed.Proxies.Count == 0)
        {
            _toasts.Error("Nothing in that paste looked like a proxy.");
            return Task.CompletedTask;
        }

        var result = _store.AddRange(parsed.Proxies);

        ImportText = "";
        Refresh();

        // Says what happened to all three categories. A bare "imported 40" leaves the
        // user unable to tell a duplicate from a parse failure, and those need
        // different responses from them.
        var parts = new List<string> { $"Added {result.AddedCount}" };
        if (result.Skipped > 0) parts.Add($"{result.Skipped} already in the library");
        if (parsed.HasFailures) parts.Add($"{parsed.Failed.Count} line(s) not understood");

        var message = string.Join(" · ", parts) + ".";

        if (parsed.HasFailures)
        {
            // Naming the first bad line turns "some lines failed" into something the
            // user can actually go and look at.
            var first = parsed.Failed[0];
            _toasts.Warning($"{message} First unreadable line was {first.Line}: {Truncate(first.Text)}");
        }
        else
        {
            _toasts.Success(message);
        }

        return Task.CompletedTask;
    }

    private static string Truncate(string text) =>
        text.Length <= 48 ? text : text[..48] + "…";

    // ------------------------------------------------------------------
    // Checking
    // ------------------------------------------------------------------

    private bool _isCheckingAll;
    public bool IsCheckingAll
    {
        get => _isCheckingAll;
        private set
        {
            if (!SetField(ref _isCheckingAll, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            CheckAllCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsIdle => !_isCheckingAll;

    private string _progress = "";
    public string Progress
    {
        get => _progress;
        private set => SetField(ref _progress, value);
    }

    /// <summary>
    /// Check every entry.
    /// <para>
    /// Run with limited concurrency rather than all at once. Each check makes a real
    /// request through a real proxy, and firing two hundred simultaneously would
    /// exhaust the local socket pool and get the shared geo endpoints to rate-limit
    /// us — which would look like every proxy failing.
    /// </para>
    /// </summary>
    private async Task CheckAllAsync()
    {
        if (Rows.Count == 0)
        {
            _toasts.Info("The library is empty.");
            return;
        }

        _checkAll?.Cancel();
        _checkAll?.Dispose();
        _checkAll = new CancellationTokenSource();
        var ct = _checkAll.Token;

        IsCheckingAll = true;

        var done = 0;
        var ok = 0;
        var total = Rows.Count;

        Progress = $"Checking 0 of {total}…";

        using var slots = new SemaphoreSlim(8, 8);

        try
        {
            var work = Rows.ToList().Select(async row =>
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await CheckOneAsync(row, ct).ConfigureAwait(false);
                    if (result?.Ok == true) Interlocked.Increment(ref ok);
                }
                finally
                {
                    slots.Release();

                    var seen = Interlocked.Increment(ref done);
                    Dispatcher.UIThread.Post(() => Progress = $"Checking {seen} of {total}…");
                }
            });

            await Task.WhenAll(work).ConfigureAwait(true);

            _toasts.Success($"Checked {total} — {ok} working, {total - ok} failed.");
        }
        catch (OperationCanceledException)
        {
            _toasts.Info($"Stopped after {done} of {total}.");
        }
        finally
        {
            IsCheckingAll = false;
            Progress = "";
        }
    }

    private void CancelCheck() => _checkAll?.Cancel();

    /// <summary>Check one entry and persist the outcome.</summary>
    internal async Task<ProxyCheckResult?> CheckOneAsync(
        ProxyRowViewModel row, CancellationToken ct = default)
    {
        var saved = _store.Get(row.Id);
        if (saved is null)
        {
            _toasts.Error("That proxy is no longer in the library.");
            Refresh();
            return null;
        }

        Dispatcher.UIThread.Post(() => row.MarkChecking());

        try
        {
            var result = await _checker.CheckAsync(saved, ct).ConfigureAwait(false);

            // Persisted, so the library still shows what it knows after a restart
            // rather than presenting every proxy as unverified again.
            _store.RecordCheck(row.Id, result);

            Dispatcher.UIThread.Post(() => row.Apply(result));
            return result;
        }
        catch (OperationCanceledException)
        {
            Dispatcher.UIThread.Post(row.ClearChecking);
            throw;
        }
        catch (Exception e)
        {
            var failure = new ProxyCheckResult
            {
                Ok = false,
                CheckedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Error = e.Message,
            };

            _store.RecordCheck(row.Id, failure);
            Dispatcher.UIThread.Post(() => row.Apply(failure));
            return failure;
        }
    }

    /// <summary>Ask the provider to rotate this proxy's exit IP, then re-check.</summary>
    internal async Task RotateAsync(ProxyRowViewModel row)
    {
        var saved = _store.Get(row.Id);
        if (saved is null)
        {
            _toasts.Error("That proxy is no longer in the library.");
            Refresh();
            return;
        }

        if (string.IsNullOrWhiteSpace(saved.RotationUrl))
        {
            _toasts.Warning($"\"{saved.Name}\" has no rotation link set.");
            return;
        }

        row.MarkChecking("Rotating…");

        try
        {
            var result = await _checker.RotateAsync(saved.RotationUrl).ConfigureAwait(true);

            if (!result.Ok)
            {
                row.ClearChecking();
                _toasts.Error(result.Error ?? $"The rotation link returned HTTP {result.Status}.");
                return;
            }

            // Re-checked immediately: rotation without confirming the new exit IP
            // tells the user nothing about whether it worked, which is the only
            // thing they wanted to know.
            _toasts.Success($"Rotated \"{saved.Name}\". Checking the new IP…");
            await CheckOneAsync(row).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            row.ClearChecking();
            _toasts.Error(e.Message);
        }
    }

    // ------------------------------------------------------------------
    // List
    // ------------------------------------------------------------------

    internal void Remove(ProxyRowViewModel row)
    {
        // Counted first: a proxy in use by profiles is one the user probably did not
        // mean to delete, and they cannot see that from this screen.
        var inUse = _profiles.List().Count(p => p.Proxy.SavedProxyId == row.Id);

        if (!_store.Remove(row.Id))
        {
            _toasts.Error("That proxy is no longer in the library.");
            Refresh();
            return;
        }

        Refresh();

        _toasts.Success(inUse == 0
            ? $"Removed \"{row.Name}\"."
            : $"Removed \"{row.Name}\" — {inUse} profile(s) referenced it and will fall back to their own settings.");
    }

    private void Clear()
    {
        var count = _store.Clear();
        Refresh();
        _toasts.Success(count == 0 ? "The library was already empty." : $"Removed {count} proxies.");
    }

    public void Refresh()
    {
        Rows.Clear();
        foreach (var saved in _store.List()) Rows.Add(new ProxyRowViewModel(saved, this));

        OnPropertyChanged(nameof(CountLabel));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public bool IsEmpty => Rows.Count == 0;

    public string CountLabel
    {
        get
        {
            var total = Rows.Count;
            var working = Rows.Count(r => r.IsOk);
            return total == 0 ? "No saved proxies" : $"{total} saved · {working} verified";
        }
    }
}

/// <summary>One row in the proxy library.</summary>
public sealed class ProxyRowViewModel : ViewModelBase
{
    private readonly ProxiesPageViewModel _page;
    private SavedProxy _proxy;

    public ProxyRowViewModel(SavedProxy proxy, ProxiesPageViewModel page)
    {
        _proxy = proxy;
        _page = page;

        CheckCommand = new AsyncRelayCommand(
            () => _page.CheckOneAsync(this), onError: _ => ClearChecking());

        RotateCommand = new AsyncRelayCommand(
            () => _page.RotateAsync(this), onError: _ => ClearChecking());

        RemoveCommand = new RelayCommand(() => _page.Remove(this));
    }

    public string Id => _proxy.Id;
    public string Name => _proxy.Name;

    /// <summary>
    /// The endpoint, credentials masked.
    /// <para>
    /// Never the full URL. Proxy credentials are usually account-wide, and a list
    /// row is exactly the sort of thing that ends up in a screenshot.
    /// </para>
    /// </summary>
    public string Endpoint => ProxyParser.Describe(_proxy);

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand RotateCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public bool CanRotate => !string.IsNullOrWhiteSpace(_proxy.RotationUrl);

    private bool _isChecking;
    private string _checkingLabel = "Checking…";

    public bool IsChecking
    {
        get => _isChecking;
        private set
        {
            if (!SetField(ref _isChecking, value)) return;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !_isChecking;

    internal void MarkChecking(string label = "Checking…")
    {
        _checkingLabel = label;
        IsChecking = true;
    }

    internal void ClearChecking() => IsChecking = false;

    internal void Apply(ProxyCheckResult result)
    {
        _proxy = _proxy with { LastCheck = result };
        IsChecking = false;

        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(LocationLabel));
        OnPropertyChanged(nameof(LatencyLabel));
        OnPropertyChanged(nameof(Detail));
    }

    public bool IsOk => !_isChecking && _proxy.LastCheck?.Ok == true;
    public bool IsFailed => !_isChecking && _proxy.LastCheck is { Ok: false };

    public string StatusLabel
    {
        get
        {
            if (_isChecking) return _checkingLabel;
            if (_proxy.LastCheck is null) return "Unchecked";
            return _proxy.LastCheck.Ok ? "Working" : "Failed";
        }
    }

    /// <summary>
    /// Exit IP and place.
    /// <para>
    /// The exit IP rather than the proxy's own address: the two differ on most
    /// provider pools, and it is the exit that every geo and timezone decision has
    /// to agree with.
    /// </para>
    /// </summary>
    public string LocationLabel
    {
        get
        {
            var check = _proxy.LastCheck;
            if (check is null || !check.Ok) return "";

            var place = new[] { check.City, check.Country }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToArray();

            return place.Length == 0
                ? check.Ip ?? ""
                : $"{check.Ip} · {string.Join(", ", place)}";
        }
    }

    public string LatencyLabel =>
        _proxy.LastCheck is { Ok: true, LatencyMs: { } ms } ? $"{ms} ms" : "";

    /// <summary>The failure reason, or the detected timezone when it worked.</summary>
    public string Detail
    {
        get
        {
            var check = _proxy.LastCheck;
            if (check is null) return "";
            if (!check.Ok) return check.Error ?? "Check failed.";
            return string.IsNullOrWhiteSpace(check.Timezone) ? "" : check.Timezone;
        }
    }
}
