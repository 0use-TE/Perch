using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Perch.ViewModels;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    // Changed("") refreshes every binding on this object.
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> action) : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action(parameter);
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
