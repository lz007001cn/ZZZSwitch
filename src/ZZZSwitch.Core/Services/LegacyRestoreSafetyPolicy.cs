using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed record RestoreSafetyResult(bool CanRestore, string? Reason)
{
    public static RestoreSafetyResult Allowed { get; } = new(true, null);
}

public sealed class LegacyRestoreSafetyPolicy
{
    private const string HotCacheReason =
        "启用双服 Blocks 缓存后，请使用“切换到国际服/国服”恢复另一服务器。旧备份恢复不包含 Blocks，已为安全起见禁用。";

    private readonly StateStore _stateStore;
    private readonly HotUpdateCacheService _hotUpdateCaches;

    public LegacyRestoreSafetyPolicy(
        StateStore stateStore,
        HotUpdateCacheService hotUpdateCaches)
    {
        _stateStore = stateStore;
        _hotUpdateCaches = hotUpdateCaches;
    }

    public RestoreSafetyResult Evaluate(string currentGamePath, BackupRecord record)
    {
        if (!TryNormalizePath(currentGamePath, out var normalizedCurrent) ||
            !TryNormalizePath(record.GamePath, out var normalizedBackup))
        {
            return new(false, "游戏目录路径无效，无法安全恢复此备份。");
        }

        if (!string.Equals(normalizedCurrent, normalizedBackup, StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "此备份属于其他游戏目录，已拒绝恢复。");
        }

        try
        {
            var state = _stateStore.Load();
            var activeProfile =
                state is not null &&
                TryNormalizePath(state.GamePath, out var normalizedState) &&
                string.Equals(normalizedState, normalizedCurrent, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(state.GameVersion, record.GameVersion, StringComparison.Ordinal)
                    ? state.CurrentProfile
                    : null;

            var hasInitializedCache = ProfileIds.HotUpdateProfiles.Any(profile =>
                _hotUpdateCaches.GetStatus(
                    profile,
                    record.GameVersion,
                    normalizedCurrent,
                    activeProfile).IsInitialized);
            return hasInitializedCache
                ? new(false, HotCacheReason)
                : RestoreSafetyResult.Allowed;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new(false, $"无法确认 Blocks 缓存状态，已停止恢复：{ex.Message}");
        }
    }

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            normalized = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
