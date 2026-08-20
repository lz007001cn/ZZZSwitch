using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ZZZSwitch.Core.Services;
using MediaBrush = System.Windows.Media.Brush;

namespace ZZZSwitch;

public partial class OnboardingWindow : Window
{
    private readonly App _app;
    private readonly UiSettings _originalSettings;
    private readonly GameDirectoryService _gameDirectory = new();
    private readonly GameDirectoryDiscoveryService _discovery;
    private int _step = 1;
    private bool _updatingAppearance;
    private bool _completed;

    public OnboardingWindow(UiSettings settings, string initialGamePath)
    {
        InitializeComponent();
        _app = (App)System.Windows.Application.Current;
        _originalSettings = settings;
        _discovery = new GameDirectoryDiscoveryService(_gameDirectory);
        SourceInitialized += (_, _) => _app.Theme.ApplyWindow(this);
        Closing += OnClosing;

        LanguageComboBox.ItemsSource = new[]
        {
            new Choice<AppLanguage>(AppLanguage.Chinese, "中文"),
            new Choice<AppLanguage>(AppLanguage.English, "English")
        };
        Select(LanguageComboBox, settings.Language);
        RebuildThemeChoices(settings.Theme);
        GamePathTextBox.Text = initialGamePath;
        FullModeRadio.IsChecked = !settings.StartInCompactMode;
        CompactModeRadio.IsChecked = settings.StartInCompactMode;
        CloseToTrayRadio.IsChecked = !settings.ExitOnClose;
        ExitOnCloseRadio.IsChecked = settings.ExitOnClose;
        UpdateStep();
        ValidateDirectory(showValid: false);
    }

    public UiSettings? ResultSettings { get; private set; }

    public string? SelectedGamePath { get; private set; }

    private void Appearance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingAppearance || !IsInitialized)
        {
            return;
        }

        var language = Selected(LanguageComboBox, _originalSettings.Language);
        var theme = Selected(ThemeComboBox, _originalSettings.Theme);
        _app.Localization.SetLanguage(language);
        _app.Theme.SetPreference(theme);
        RebuildThemeChoices(theme);
        UpdateStep();
        ValidateDirectory(showValid: !string.IsNullOrWhiteSpace(GamePathTextBox.Text));
    }

    private void RebuildThemeChoices(ThemePreference selected)
    {
        _updatingAppearance = true;
        ThemeComboBox.ItemsSource = new[]
        {
            new Choice<ThemePreference>(ThemePreference.FollowWindows, _app.Localization.Text("L.Settings.FollowWindows")),
            new Choice<ThemePreference>(ThemePreference.Light, _app.Localization.Text("L.Settings.Light")),
            new Choice<ThemePreference>(ThemePreference.Dark, _app.Localization.Text("L.Settings.Dark"))
        };
        Select(ThemeComboBox, selected);
        _updatingAppearance = false;
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        SetDirectoryControlsEnabled(false);
        DirectoryStatusText.Text = _app.Localization.Choose(
            "正在检测绝区零游戏目录…",
            "Detecting the Zenless Zone Zero game directory…");
        try
        {
            var candidates = await Task.Run(() => _discovery.Discover([GamePathTextBox.Text]));
            if (candidates.Count == 0)
            {
                ValidateDirectory(showValid: true);
                return;
            }

            GameDirectoryCandidate? selected = candidates.Count == 1
                ? candidates[0]
                : SelectCandidate(candidates);
            if (selected is not null)
            {
                GamePathTextBox.Text = selected.Path;
            }

            ValidateDirectory(showValid: true);
        }
        finally
        {
            SetDirectoryControlsEnabled(true);
        }
    }

    private GameDirectoryCandidate? SelectCandidate(IReadOnlyList<GameDirectoryCandidate> candidates)
    {
        var dialog = new GameDirectorySelectionWindow(candidates) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.SelectedCandidate : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = _app.Localization.Choose(
                "选择绝区零游戏根目录",
                "Select the Zenless Zone Zero game root"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(GamePathTextBox.Text) ? GamePathTextBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            GamePathTextBox.Text = dialog.SelectedPath;
            ValidateDirectory(showValid: true);
        }
    }

    private void GamePath_TextChanged(object sender, TextChangedEventArgs e) =>
        ValidateDirectory(showValid: false);

    private bool ValidateDirectory(bool showValid)
    {
        if (GamePathTextBox is null || DirectoryStatusText is null)
        {
            return false;
        }

        var validation = _gameDirectory.Validate(GamePathTextBox.Text.Trim());
        var valid = validation.IsValid;
        DirectoryStatusText.Text = valid
            ? showValid ? _app.Localization.Text("L.Onboarding.Valid") : string.Empty
            : _app.Localization.Text("L.Onboarding.Invalid");
        DirectoryStatusText.Foreground = valid
            ? (MediaBrush)FindResource("GreenBrush")
            : (MediaBrush)FindResource("WarningBrush");
        return valid;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            UpdateStep();
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 2 && !ValidateDirectory(showValid: true))
        {
            return;
        }

        if (_step < 3)
        {
            _step++;
            UpdateStep();
            return;
        }

        Complete();
    }

    private void Complete()
    {
        if (!ValidateDirectory(showValid: true))
        {
            _step = 2;
            UpdateStep();
            return;
        }

        ResultSettings = new UiSettings
        {
            Language = Selected(LanguageComboBox, _originalSettings.Language),
            Theme = Selected(ThemeComboBox, _originalSettings.Theme),
            OnboardingCompleted = true,
            StartInCompactMode = CompactModeRadio.IsChecked == true,
            ExitOnClose = ExitOnCloseRadio.IsChecked == true,
            AutoDetectGameDirectory = _originalSettings.AutoDetectGameDirectory,
            AutoInspectOnStartup = true,
            ShowLastGameDirectory = true,
            RememberWindowPlacement = _originalSettings.RememberWindowPlacement,
            ShowDetailedStatus = _originalSettings.ShowDetailedStatus,
            LogRetentionDays = _originalSettings.LogRetentionDays,
            WindowLeft = _originalSettings.WindowLeft,
            WindowTop = _originalSettings.WindowTop,
            WindowWidth = _originalSettings.WindowWidth,
            WindowHeight = _originalSettings.WindowHeight,
            WindowMaximized = _originalSettings.WindowMaximized
        };
        SelectedGamePath = _gameDirectory.Validate(GamePathTextBox.Text.Trim()).GamePath;
        _completed = true;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        _app.Localization.SetLanguage(_originalSettings.Language);
        _app.Theme.SetPreference(_originalSettings.Theme);
    }

    private void UpdateStep()
    {
        AppearancePanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        GameDirectoryPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        BehaviorPanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _step > 1;
        NextButtonText.Text = _app.Localization.Text(
            _step == 3 ? "L.Onboarding.Finish" : "L.Onboarding.Next");
        StepText.Text = string.Format(
            _app.Localization.Text("L.Onboarding.StepFormat"),
            _step);
        StepOneIndicator.Background = (MediaBrush)FindResource(_step >= 1 ? "TextBrush" : "BorderBrush");
        StepTwoIndicator.Background = (MediaBrush)FindResource(_step >= 2 ? "TextBrush" : "BorderBrush");
        StepThreeIndicator.Background = (MediaBrush)FindResource(_step >= 3 ? "TextBrush" : "BorderBrush");
    }

    private void SetDirectoryControlsEnabled(bool enabled)
    {
        DetectButton.IsEnabled = enabled;
        BrowseButton.IsEnabled = enabled;
        GamePathTextBox.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
    }

    private static void Select<T>(System.Windows.Controls.ComboBox comboBox, T value)
    {
        comboBox.SelectedItem = comboBox.Items.Cast<Choice<T>>()
            .FirstOrDefault(item => EqualityComparer<T>.Default.Equals(item.Value, value));
        comboBox.SelectedIndex = Math.Max(0, comboBox.SelectedIndex);
    }

    private static T Selected<T>(System.Windows.Controls.ComboBox comboBox, T fallback) =>
        comboBox.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private sealed record Choice<T>(T Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
