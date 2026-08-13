using System.Text.Json;

namespace ZZZSwitch.Core.Services;

internal static class AtomicJsonFile
{
    public static void Write<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
                        ?? throw new InvalidOperationException($"无法确定 JSON 文件目录：{path}");
        Directory.CreateDirectory(directory);

        var temporary = fullPath + ".tmp";
        try
        {
            // 临时文件与目标文件位于同一目录，最终替换不会跨卷；Flush(true)
            // 确保关键状态先落盘，降低断电或强制结束进程留下半份 JSON 的概率。
            using (var stream = new FileStream(
                       temporary,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, value, options ?? JsonSupport.Options);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporary(temporary);
            throw;
        }
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort only; the original write exception is more useful to the caller.
        }
    }
}
