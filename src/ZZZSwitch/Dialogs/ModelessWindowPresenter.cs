using System.Windows;

namespace ZZZSwitch.Dialogs;

public static class ModelessWindowPresenter
{
    public static Task ShowAsync(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            completion.TrySetResult();
        }

        window.Closed += OnClosed;
        try
        {
            window.Show();
        }
        catch
        {
            window.Closed -= OnClosed;
            throw;
        }

        return completion.Task;
    }
}
