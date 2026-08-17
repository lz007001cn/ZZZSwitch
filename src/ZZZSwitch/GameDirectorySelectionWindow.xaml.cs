using System.Windows;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class GameDirectorySelectionWindow : Window
{
    public GameDirectorySelectionWindow(
        IReadOnlyList<GameDirectoryCandidate> candidates)
    {
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);
        PathsList.ItemsSource = candidates.Select(candidate => candidate with
        {
            Source = candidate.Source switch
            {
                "上次使用" => app.Localization.Choose("上次使用", "Last used"),
                "启动器记录" => app.Localization.Choose("启动器记录", "Launcher record"),
                "常见安装位置" => app.Localization.Choose("常见安装位置", "Common install location"),
                "固定磁盘" => app.Localization.Choose("固定磁盘", "Fixed drive"),
                _ => candidate.Source
            }
        }).ToArray();
        PathsList.SelectedIndex = candidates.Count > 0 ? 0 : -1;
    }

    public GameDirectoryCandidate? SelectedCandidate =>
        PathsList.SelectedItem as GameDirectoryCandidate;

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            return;
        }

        DialogResult = true;
    }
}
