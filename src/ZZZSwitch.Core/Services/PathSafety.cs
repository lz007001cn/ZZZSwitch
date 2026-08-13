namespace ZZZSwitch.Core.Services;

public static class PathSafety
{
    private static readonly char[] Wildcards = ['*', '?'];

    public static bool TryResolveUnderRoot(string root, string relativePath, out string resolved, out string error)
    {
        resolved = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            error = "路径为空。";
            return false;
        }

        if (Path.IsPathRooted(relativePath))
        {
            error = "不允许绝对路径。";
            return false;
        }

        if (relativePath.IndexOfAny(Wildcards) >= 0)
        {
            error = "不允许通配符。";
            return false;
        }

        if (relativePath.Contains('%', StringComparison.Ordinal))
        {
            error = "不允许未解析的环境变量。";
            return false;
        }

        var segments = relativePath.Replace('/', Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(x => x is "." or ".."))
        {
            error = "不允许 . 或 .. 路径段。";
            return false;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "路径超出允许的根目录。";
            return false;
        }

        resolved = candidate;
        return true;
    }

    public static string ResolveOrThrow(string root, string relativePath)
    {
        if (!TryResolveUnderRoot(root, relativePath, out var resolved, out var error))
        {
            throw new InvalidDataException($"非法相对路径“{relativePath}”：{error}");
        }

        return resolved;
    }
}
