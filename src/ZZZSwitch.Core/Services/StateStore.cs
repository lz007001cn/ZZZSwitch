using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class StateStore
{
    private readonly AppPaths _paths;

    public StateStore(AppPaths paths) => _paths = paths;

    public AppState? Load() => LoadWithStatus().State;

    public StateLoadResult LoadWithStatus()
    {
        if (!File.Exists(_paths.StateFile))
        {
            return new();
        }

        try
        {
            using var stream = File.OpenRead(_paths.StateFile);
            var state = JsonSerializer.Deserialize<AppState>(stream, JsonSupport.Options);
            if (state is null)
            {
                return new() { Warning = "状态文件内容为空，已忽略该记录。" };
            }

            if (!string.IsNullOrWhiteSpace(state.GamePath))
            {
                try
                {
                    state.GamePath = Path.GetFullPath(state.GamePath).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                }
                catch (Exception ex) when (
                    ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    return new() { Warning = $"状态文件中的游戏路径无效，已忽略该记录：{ex.Message}" };
                }
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentProfile) &&
                !ProfileIds.All.Contains(state.CurrentProfile, StringComparer.Ordinal))
            {
                return new() { Warning = "状态文件中的服务器标识无效，已忽略该记录。" };
            }

            return new() { State = state };
        }
        catch (Exception ex) when (
            ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new() { Warning = $"状态文件无法读取，已忽略该记录：{ex.Message}" };
        }
    }

    public void Save(AppState state)
    {
        _paths.EnsureWritableDirectories();
        AtomicJsonFile.Write(_paths.StateFile, state);
    }
}
