using System.Windows;
using System.Windows.Input;

namespace LocalNote.App.Commands;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;

    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await execute();
        }
        catch (Exception ex)
        {
            // ICommand requires a void Execute entry point. Without an explicit catch,
            // exceptions raised after an await escape the async-void method and can tear
            // down the WPF dispatcher. Keep the app alive and surface a clear message.
            var owner = Application.Current?.MainWindow;
            MessageBox.Show(owner, ex.Message, "LocalNote · 操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
