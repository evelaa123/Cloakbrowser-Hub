using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace CloakHub.App.ViewModels;

public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// Transient notifications, ported from the Electron build's toast component.
/// <para>
/// Errors reach the user through this rather than through a modal dialogue, because
/// most failures here are recoverable and non-blocking: a proxy that did not
/// respond, a profile that would not start. A modal would demand acknowledgement for
/// something the user can simply retry.
/// </para>
/// </summary>
public sealed class ToastHost : ViewModelBase
{
    /// <summary>
    /// Live list, bound directly by the view.
    /// <para>
    /// Capped, because the failure mode that produces the most toasts is a loop —
    /// every profile in a batch failing the same way — and an unbounded list would
    /// cover the window it is reporting on.
    /// </para>
    /// </summary>
    public ObservableCollection<Toast> Items { get; } = [];

    public ToastHost() => DismissCommand = new RelayCommand<Toast>(Dismiss);

    /// <summary>Bound by each toast's close button, with the toast as its parameter.</summary>
    public RelayCommand<Toast> DismissCommand { get; }

    private const int MaxVisible = 4;

    public void Info(string message) => Add(message, ToastKind.Info);
    public void Success(string message) => Add(message, ToastKind.Success);
    public void Warning(string message) => Add(message, ToastKind.Warning);

    public void Error(string message) => Add(message, ToastKind.Error);

    /// <summary>
    /// Report an exception.
    /// <para>
    /// Shows the message, not the type name or the stack: "Access to the path is
    /// denied" is actionable, "UnauthorizedAccessException" is not. The full detail
    /// still reaches the crash log for anything unhandled.
    /// </para>
    /// </summary>
    public void Error(Exception ex) => Add(ex.Message, ToastKind.Error);

    private void Add(string message, ToastKind kind)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        // Marshalled to the UI thread. Toasts are raised from background work --
        // launching a browser, checking a proxy -- and mutating a bound collection off
        // the UI thread throws in Avalonia rather than merely being unsafe.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Add(message, kind));
            return;
        }

        var toast = new Toast(message, kind);
        Items.Add(toast);

        while (Items.Count > MaxVisible) Items.RemoveAt(0);

        // Errors persist until dismissed; everything else clears itself. An error the
        // user did not happen to be looking at is the one thing here worth insisting
        // on, and it is also the one they may need to read twice.
        if (kind != ToastKind.Error) _ = ExpireAsync(toast);
    }

    private async Task ExpireAsync(Toast toast)
    {
        await Task.Delay(TimeSpan.FromSeconds(4));

        // Guarded: the user may have dismissed it already, and removing an absent item
        // is harmless but re-adding a removed one would not be.
        if (Dispatcher.UIThread.CheckAccess()) Items.Remove(toast);
        else Dispatcher.UIThread.Post(() => Items.Remove(toast));
    }

    public void Dismiss(Toast toast) => Items.Remove(toast);
}

public sealed class Toast
{
    public Toast(string message, ToastKind kind)
    {
        Message = message;
        Kind = kind;
    }

    public string Message { get; }
    public ToastKind Kind { get; }

    // Exposed as booleans so the view can style by class without a converter.
    public bool IsError => Kind == ToastKind.Error;
    public bool IsWarning => Kind == ToastKind.Warning;
    public bool IsSuccess => Kind == ToastKind.Success;
}
