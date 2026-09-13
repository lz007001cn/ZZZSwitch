namespace ZZZSwitch.Core.Services;

internal static class RecoveryGuard
{
    // Recovery can legitimately encounter missing core files. Only the version
    // witness must be intact; requiring a fully detected client prevents rollback.
    internal static void EnsureVersion(string gamePath, string expectedVersion)
    {
        var current = new GameDirectoryService().Validate(gamePath).GameVersion;
        if (string.IsNullOrWhiteSpace(current))
            throw new InvalidDataException("无法读取当前游戏版本，已停止恢复；请先检查 version_info。");
        if (!string.Equals(current, expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException($"当前游戏版本 {current} 与操作版本 {expectedVersion} 不一致，不能跨版本恢复；已保留记录。");
    }

    internal static void EnsureOrdinaryPath(string path)
    {
        new OrdinaryPathGuard().Ensure(path);
    }
}
