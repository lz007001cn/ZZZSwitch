using System.Windows;
using ZZZSwitch.Core.Models;
using ZZZSwitch.Presentation;

namespace ZZZSwitch;

public partial class OnlineDifferencePreviewWindow : Window
{
    public OnlineDifferencePreviewWindow(OnlineDifferencePackagePreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        InitializeComponent();
        var app = (App)System.Windows.Application.Current;
        var localization = app.Localization;
        SourceInitialized += (_, _) => app.Theme.ApplyWindow(this);

        var package = preview.Package;
        DirectionText.Text =
            $"{localization.ProfileName(package.SourceProfile)} → " +
            $"{localization.ProfileName(package.TargetProfile)} · Manifest {ShortId(package.ManifestId)}";
        VersionText.Text = package.GameVersion;
        StateText.Text = StateName(package.State, localization);
        FileCountText.Text = localization.Choose($"{preview.Files.Count:N0} 个", $"{preview.Files.Count:N0}");
        SizeText.Text = DisplayFormatting.FormatBytes(package.TotalBytes);
        NotesText.Text = BuildNotes(preview, localization);
        FileList.ItemsSource = preview.Files.Select(file => new PreviewRow(
            file.Path,
            file.Length.HasValue ? DisplayFormatting.FormatBytes(file.Length.Value) : "—",
            file.State)).ToArray();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string BuildNotes(OnlineDifferencePackagePreview preview, LocalizationManager localization)
    {
        var deletes = preview.DeleteFiles.Count == 0
            ? localization.Choose("无自动删除项", "No automatic deletions")
            : localization.Choose(
                $"{preview.DeleteFiles.Count:N0} 个删除项",
                $"{preview.DeleteFiles.Count:N0} deletion entries");
        return string.IsNullOrWhiteSpace(preview.Notes)
            ? deletes
            : $"{deletes} · {preview.Notes}";
    }

    private static string StateName(OnlineDifferencePackageState state, LocalizationManager localization) => state switch
    {
        OnlineDifferencePackageState.Ready => localization.Choose("已下载", "Ready"),
        OnlineDifferencePackageState.Incomplete => localization.Choose("未完成", "Incomplete"),
        _ => localization.Choose("需修复", "Needs repair")
    };

    private static string ShortId(string value) => value.Length <= 16 ? value : value[..16] + "…";

    private sealed record PreviewRow(string Path, string Size, string State);
}
