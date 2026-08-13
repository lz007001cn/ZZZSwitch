using System.Windows.Input;

namespace ZZZSwitch.Commands;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _handleError;

    public RelayCommand(
        Action execute,
        Func<bool>? canExecute = null,
        Action<Exception>? handleError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _handleError = handleError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            try
            {
                _execute();
            }
            catch (Exception ex) when (_handleError is not null)
            {
                _handleError(ex);
            }
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
