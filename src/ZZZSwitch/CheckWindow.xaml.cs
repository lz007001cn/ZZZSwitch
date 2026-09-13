using ZZZSwitch.Workflows;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace ZZZSwitch;

public partial class CheckWindow : Window
{
    private readonly Func<CheckAction, Task<string>> _run;
    private bool _running;
    public CheckWindow(Func<CheckAction, Task<string>> run, string initialText)
    {
        InitializeComponent();
        _run = run;
        ResultText.Text = initialText;
        var app = (App)System.Windows.Application.Current;
        Title = string.Empty;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        Closing += (_, e) => e.Cancel = _running;
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_running || sender is not Button { Tag: string tag } || !Enum.TryParse<CheckAction>(tag, out var action)) return;
        _running = true;
        Actions.IsEnabled = CloseButton.IsEnabled = false;
        ResultText.Text = ((App)System.Windows.Application.Current).Localization.Choose("正在处理…", "Working…");
        try { ResultText.Text = await _run(action); }
        catch (Exception ex) { ResultText.Text = ex.Message; }
        finally { _running = false; Actions.IsEnabled = CloseButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
