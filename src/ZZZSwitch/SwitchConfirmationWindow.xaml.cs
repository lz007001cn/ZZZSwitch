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
        var localization = ((App)System.Windows.Application.Current).Localization;

        SourceProfileText.Text = ProfileName(sourceProfile, sourceName, localization);
        TargetProfileText.Text = ProfileName(targetProfile, targetName, localization);
        SourceProfileImage.Source = LoadProfileImage(sourceProfile);
        TargetProfileImage.Source = LoadProfileImage(targetProfile);
        GameVersionText.Text = gameVersion;
        FileOperationText.Text = string.Format(
            localization.Text("L.SwitchConfirm.FileOperationFormat"),
            replaceCount,
            deleteCount);
        BackupPathText.Text = backupPath;
    }

    private static string ProfileName(
        string profile,
        string fallback,
        LocalizationManager localization) => profile switch
        {
            Core.Models.ProfileIds.Global => localization.Text("L.Server.Global"),
            Core.Models.ProfileIds.CnOfficial => localization.Text("L.Server.Cn"),
            Core.Models.ProfileIds.Bilibili => localization.Text("L.Server.Bilibili"),
            _ => fallback
        };

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
