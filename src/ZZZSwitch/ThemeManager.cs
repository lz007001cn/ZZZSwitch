using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using ZZZSwitch.Core.Services;
using WpfApplication = System.Windows.Application;

namespace ZZZSwitch;

public sealed class ThemeManager : IDisposable
{
    private readonly WpfApplication _application;
    private readonly UiSettingsService _settings;
    private bool _disposed;

    public ThemeManager(WpfApplication application, AppPaths paths)
    {
        _application = application;
        _settings = new UiSettingsService(paths);
        Preference = _settings.LoadThemePreference();
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Apply();
    }

    public ThemePreference Preference { get; private set; }
    public bool IsDark { get; private set; }

    public event EventHandler? Changed;

    public void SetPreference(ThemePreference preference)
    {
        if (Preference == preference)
        {
            return;
        }

        _settings.SaveThemePreference(preference);
        Preference = preference;
        Apply();
    }

    public void ApplyWindow(Window window) => DarkWindowHelper.Apply(window, IsDark);

    public static string DisplayName(ThemePreference preference) => preference switch
    {
        ThemePreference.Dark => "深色",
        ThemePreference.Light => "浅色",
        _ => "跟随 Windows"
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
        _disposed = true;
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (Preference != ThemePreference.FollowWindows)
        {
            return;
        }

        _application.Dispatcher.BeginInvoke(Apply);
    }

    private void Apply()
    {
        IsDark = Preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => WindowsUsesDarkApps()
        };

        var palette = IsDark ? DarkPalette : LightPalette;
        foreach (var (key, color) in palette)
        {
            _application.Resources[key] = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        }

        foreach (Window window in _application.Windows)
        {
            ApplyWindow(window);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                0);
            return value is not int number || number == 0;
        }
        catch
        {
            return true;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#1B1B1B",
            ["SidebarBrush"] = "#161616",
            ["SurfaceBrush"] = "#222222",
            ["RaisedSurfaceBrush"] = "#272727",
            ["SwitchButtonBrush"] = "#222222",
            ["HoverBrush"] = "#2E2E2E",
            ["BorderBrush"] = "#343434",
            ["StrongBorderBrush"] = "#484848",
            ["TextBrush"] = "#F2F2F2",
            ["MutedTextBrush"] = "#A0A0A0",
            ["SubtleTextBrush"] = "#707070",
            ["InputBrush"] = "#191919",
            ["SelectionBrush"] = "#506B92",
            ["ProgressTrackBrush"] = "#171717",
            ["ProgressIndicatorBrush"] = "#DADADA",
            ["ScrollThumbBrush"] = "#555555",
            ["ScrollThumbHoverBrush"] = "#707070",
            ["PrimaryBrush"] = "#EEEEEE",
            ["PrimaryHoverBrush"] = "#FFFFFF",
            ["PrimaryTextBrush"] = "#151515",
            ["TableHeaderBrush"] = "#1D1D1D",
            ["WarningSurfaceBrush"] = "#28251D",
            ["WarningSurfaceBorderBrush"] = "#4A4027",
            ["WarningSurfaceTextBrush"] = "#D8CDAF"
        };

    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>
        {
            ["WindowBrush"] = "#F6F6F6",
            ["SidebarBrush"] = "#F6F6F6",
            ["SurfaceBrush"] = "#FFFFFF",
            ["RaisedSurfaceBrush"] = "#F1F1F1",
            ["SwitchButtonBrush"] = "#FFFFFF",
            ["HoverBrush"] = "#E8E8E8",
            ["BorderBrush"] = "#D8D8D8",
            ["StrongBorderBrush"] = "#A8A8A8",
            ["TextBrush"] = "#1C1C1C",
            ["MutedTextBrush"] = "#606060",
            ["SubtleTextBrush"] = "#888888",
            ["InputBrush"] = "#FFFFFF",
            ["SelectionBrush"] = "#9CC2F0",
            ["ProgressTrackBrush"] = "#E5E5E5",
            ["ProgressIndicatorBrush"] = "#555555",
            ["ScrollThumbBrush"] = "#B8B8B8",
            ["ScrollThumbHoverBrush"] = "#929292",
            ["PrimaryBrush"] = "#202020",
            ["PrimaryHoverBrush"] = "#000000",
            ["PrimaryTextBrush"] = "#FFFFFF",
            ["TableHeaderBrush"] = "#F0F0F0",
            ["WarningSurfaceBrush"] = "#FFF8E1",
            ["WarningSurfaceBorderBrush"] = "#D6BE75",
            ["WarningSurfaceTextBrush"] = "#5E4A16"
        };
}
