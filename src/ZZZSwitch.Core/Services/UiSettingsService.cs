using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZSwitch.Core.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThemePreference
{
    FollowWindows,
    Dark,
    Light
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppLanguage
{
    Chinese,
    English
}

public sealed class UiSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.FollowWindows;
    public AppLanguage Language { get; set; } = AppLanguage.Chinese;
    public bool OnboardingCompleted { get; set; }
    public bool StartInCompactMode { get; set; }
    public bool ExitOnClose { get; set; }
    public bool AutoDetectGameDirectory { get; set; }
    public bool AutoInspectOnStartup { get; set; } = true;
    public bool ShowLastGameDirectory { get; set; } = true;
    public bool RememberWindowPlacement { get; set; } = true;
    public bool ShowDetailedStatus { get; set; }
    public int LogRetentionDays { get; set; } = 30;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}

public sealed class UiSettingsService
{
    private readonly AppPaths _paths;

    public UiSettingsService(AppPaths paths) => _paths = paths;

    public UiSettings Load()
    {
        if (!File.Exists(_paths.UiSettingsFile))
        {
            return new();
        }

        try
        {
            using var stream = File.OpenRead(_paths.UiSettingsFile);
            return Normalize(JsonSerializer.Deserialize<UiSettings>(stream, JsonSupport.Options) ?? new());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(UiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicJsonFile.Write(_paths.UiSettingsFile, Normalize(settings));
    }

    public ThemePreference LoadThemePreference() => Load().Theme;

    public void SaveThemePreference(ThemePreference preference)
    {
        if (!Enum.IsDefined(preference))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        var settings = Load();
        settings.Theme = preference;
        Save(settings);
    }

    public AppLanguage LoadLanguage() => Load().Language;

    public void SaveLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language))
        {
            throw new ArgumentOutOfRangeException(nameof(language));
        }

        var settings = Load();
        settings.Language = language;
        Save(settings);
    }

    private static UiSettings Normalize(UiSettings settings)
    {
        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = ThemePreference.FollowWindows;
        }

        if (!Enum.IsDefined(settings.Language))
        {
            settings.Language = AppLanguage.Chinese;
        }

        if (settings.LogRetentionDays is not (7 or 30))
        {
            settings.LogRetentionDays = 30;
        }

        if (settings.WindowWidth is < 820 or > 7680 || settings.WindowHeight is < 640 or > 4320)
        {
            settings.WindowLeft = null;
            settings.WindowTop = null;
            settings.WindowWidth = null;
            settings.WindowHeight = null;
            settings.WindowMaximized = false;
        }

        return settings;
    }
}
