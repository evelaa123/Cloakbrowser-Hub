using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CloakHub.App.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base.
/// <para>
/// Hand-written rather than pulling in CommunityToolkit.Mvvm or ReactiveUI. The
/// binding needs here are ordinary — set a property, raise a notification — and
/// both libraries bring source generators or a reactive stack that would have to
/// be understood by anyone touching the UI. The whole base class is thirty lines,
/// so the dependency would cost more than it saves.
/// </para>
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Assign and notify, returning whether the value actually changed.
    /// <para>
    /// The equality check is not just an optimisation. A two-way bound TextBox
    /// writes back on every keystroke, and re-raising the notification for an
    /// unchanged value makes the control reset its caret to the end — so typing in
    /// the middle of a word jumps the cursor. Skipping the no-op fixes that.
    /// </para>
    /// </summary>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
