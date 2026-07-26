using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace CloakHub.App.ViewModels;

/// <summary>
/// An <see cref="ICommand"/> over a delegate.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    /// <summary>Re-query <c>CanExecute</c>, so bound controls update their enabled state.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An <see cref="ICommand"/> that takes a typed parameter.
/// <para>
/// Exists so XAML can pass <c>CommandParameter</c> — a route to navigate to, a toast
/// to dismiss — without the view model exposing one command per possible value. The
/// cast is guarded rather than blind: a binding that supplies the wrong type is a
/// mistake to ignore quietly, not one to crash the UI thread over, because the
/// alternative is an unhandled exception from a button press.
/// </para>
/// </summary>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is T value ? _canExecute?.Invoke(value) ?? true : parameter is null;

    public void Execute(object? parameter)
    {
        if (parameter is T value) _execute(value);
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An async <see cref="ICommand"/> that cannot be re-entered.
/// <para>
/// Re-entrancy protection is the reason this exists rather than an
/// <c>async void</c> handler. Starting a browser takes seconds, and a user who
/// clicks Launch twice would otherwise get two sessions for one profile — two
/// Chromium processes on the same user-data directory, which corrupts the profile's
/// cookie database. Disabling the command while it runs makes that impossible
/// rather than merely unlikely.
/// </para>
/// <para>
/// It also swallows nothing: an <c>async void</c> exception cannot be caught by the
/// caller and becomes an unhandled crash, so failures are routed to a handler that
/// can show them.
/// </para>
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncRelayCommand(
        Func<Task> execute,
        Func<bool>? canExecute = null,
        Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (Exception ex)
        {
            // Reported rather than rethrown: this is an async void boundary, so a
            // rethrow here would reach the synchronisation context as an unhandled
            // exception and take the app down over a failed button press.
            _onError?.Invoke(ex);
        }
        finally
        {
            // In a finally so a failure re-enables the button. Without this a single
            // error would leave the control permanently dead, and the user's only
            // recourse would be restarting the app.
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
