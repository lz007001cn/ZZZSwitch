namespace ZZZSwitch.Core.Services;

// One short-lived instance per validation phase. Shared parents are queried once;
// never reuse this cache after a phase that can change the filesystem.
internal sealed class OrdinaryPathGuard
{
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    internal void Ensure(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null && !_checked.Contains(current); current = Path.GetDirectoryName(current))
        {
            // FileSystemInfo represents absent paths with -1 without throwing for
            // each not-yet-created backup/staging file. Access errors still throw.
            var attributes = new FileInfo(current).Attributes;
            if (attributes == (FileAttributes)(-1)) { _checked.Add(current); continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("拒绝通过重解析点读写文件：" + current);
            _checked.Add(current);
        }
    }
}
