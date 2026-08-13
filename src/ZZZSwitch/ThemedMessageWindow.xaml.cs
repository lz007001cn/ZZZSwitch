using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;

namespace ZZZSwitch;

public enum MessageTone
{
    Information,
    Success,
    Warning,
    Error
}

public partial class ThemedMessageWindow : Window
{
    public ThemedMessageWindow(
        string title,
        string message,
        MessageTone tone = MessageTone.Information,
        string? subtitle = null,
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkWindowHelper.Apply(this);

        Title = title;
        TitleText.Text = title;
        SubtitleText.Text = subtitle ?? ToneSubtitle(tone);
        MessageText.Text = message;
        ToneDot.Fill = accentBrush ?? ToneBrush(tone);
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = primaryText;
    }

    public static bool? Show(
        Window? owner,
        string title,
        string message,
        MessageTone tone = MessageTone.Information,
        string? subtitle = null,
        bool showCancel = false,
        string primaryText = "知道了",
        MediaBrush? accentBrush = null)
    {
        var window = new ThemedMessageWindow(
            title,
            message,
            tone,
            subtitle,
            showCancel,
            primaryText,
            accentBrush);
        if (owner is not null)
        {
            window.Owner = owner;
        }

        return window.ShowDialog();
    }

    private static string ToneSubtitle(MessageTone tone) => tone switch
    {
        MessageTone.Success => "操作已完成",
        MessageTone.Warning => "需要确认",
        MessageTone.Error => "操作未完成",
        _ => "ZZZSwitch"
    };

    private static MediaBrush ToneBrush(MessageTone tone) => tone switch
    {
        MessageTone.Success => ResourceBrush("GreenBrush", MediaColor.FromRgb(103, 197, 154)),
        MessageTone.Warning => ResourceBrush("WarningBrush", MediaColor.FromRgb(217, 181, 91)),
        MessageTone.Error => ResourceBrush("ErrorBrush", MediaColor.FromRgb(210, 105, 105)),
        _ => ResourceBrush("NeutralBrush", MediaColor.FromRgb(200, 200, 200))
    };

    private static MediaBrush ResourceBrush(string key, MediaColor fallback) =>
        System.Windows.Application.Current?.TryFindResource(key) as MediaBrush ?? new SolidColorBrush(fallback);

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
