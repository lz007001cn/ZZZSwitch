using System.Windows;
using System.Windows.Media.Imaging;

namespace ZZZSwitch;

public partial class SwitchConfirmationWindow : Window
{
    public SwitchConfirmationWindow(
        string sourceProfile,
        string sourceName,
        string targetProfile,
        string targetName,
        string gameVersion,
        int replaceCount,
        int deleteCount,
        string backupPath)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);

        SourceProfileText.Text = sourceName;
        TargetProfileText.Text = targetName;
        SourceProfileImage.Source = LoadProfileImage(sourceProfile);
        TargetProfileImage.Source = LoadProfileImage(targetProfile);
        GameVersionText.Text = gameVersion;
        FileOperationText.Text = $"替换 {replaceCount} 个文件 · 删除 {deleteCount} 个文件";
        BackupPathText.Text = backupPath;
    }

    private static BitmapImage LoadProfileImage(string profile)
    {
        var fileName = profile switch
        {
            Core.Models.ProfileIds.Global => "Server-Global.png",
            Core.Models.ProfileIds.Bilibili => "Server-Bilibili.png",
            _ => "Server-CN.png"
        };
        return new BitmapImage(new Uri(
            $"pack://application:,,,/ZZZSwitch;component/Assets/{fileName}",
            UriKind.Absolute));
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
