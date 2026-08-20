using System.ComponentModel;
using System.Windows;

namespace ZZZSwitch;

public partial class CompactModeWindow : Window
{
    public CompactModeWindow(object dataContext)
    {
        InitializeComponent();
        DataContext = dataContext;
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);
        Closing += OnClosing;
    }

    private void FullMode_Click(object sender, RoutedEventArgs e) =>
        ((App)System.Windows.Application.Current).ShowFullWindow();

    private void OnClosing(object? sender, CancelEventArgs e) =>
        ((App)System.Windows.Application.Current).HandleWindowClosing(this, e);
}
