using System.Windows;
using ZZZSwitch.Core.Services;

namespace ZZZSwitch;

public partial class GameDirectorySelectionWindow : Window
{
    public GameDirectorySelectionWindow(
        IReadOnlyList<GameDirectoryCandidate> candidates)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkWindowHelper.Apply(this);
        PathsList.ItemsSource = candidates;
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
