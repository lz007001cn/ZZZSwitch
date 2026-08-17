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
        SourceInitialized += (_, _) => ((App)System.Windows.Application.Current).Theme.ApplyWindow(this);

        var package = preview.Package;
        DirectionText.Text =
            $"{DisplayFormatting.ShortProfileName(package.SourceProfile)} → " +
            $"{DisplayFormatting.ShortProfileName(package.TargetProfile)} · Manifest {ShortId(package.ManifestId)}";
        VersionText.Text = package.GameVersion;
        StateText.Text = StateName(package.State);
        FileCountText.Text = $"{preview.Files.Count:N0} 个";
        SizeText.Text = DisplayFormatting.FormatBytes(package.TotalBytes);
        NotesText.Text = BuildNotes(preview);
        FileList.ItemsSource = preview.Files.Select(file => new PreviewRow(
            file.Path,
            file.Length.HasValue ? DisplayFormatting.FormatBytes(file.Length.Value) : "—",
            file.State,
            file.Integrity)).ToArray();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string BuildNotes(OnlineDifferencePackagePreview preview)
    {
        var deletes = preview.DeleteFiles.Count == 0
            ? "无自动删除项"
            : $"{preview.DeleteFiles.Count:N0} 个删除项";
        return string.IsNullOrWhiteSpace(preview.Notes)
            ? deletes
            : $"{deletes} · {preview.Notes}";
    }

    private static string StateName(OnlineDifferencePackageState state) => state switch
    {
        OnlineDifferencePackageState.Ready => "已下载",
        OnlineDifferencePackageState.Incomplete => "未完成",
        _ => "需修复"
    };

    private static string ShortId(string value) => value.Length <= 16 ? value : value[..16] + "…";

    private sealed record PreviewRow(string Path, string Size, string State, string Integrity);
}
