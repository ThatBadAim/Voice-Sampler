namespace VoiceScan.App.Core.ViewModels;

using System;
using System.Windows.Input;

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute) : this(_ => execute(), null) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_canExecute == null) return true;
        if (parameter is null && default(T) is null) return _canExecute(default);
        if (parameter is T typed) return _canExecute(typed);
        return false;
    }

    public void Execute(object? parameter)
    {
        if (parameter is null && default(T) is null)
        {
            _execute(default);
        }
        else if (parameter is T typed)
        {
            _execute(typed);
        }
        else if (parameter is not null && typeof(T) == typeof(string))
        {
            _execute((T)(object)parameter.ToString()!);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
