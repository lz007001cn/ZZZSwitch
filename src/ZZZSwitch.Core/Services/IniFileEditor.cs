using System.Text;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class IniFileEditor
{
    public void Apply(string path, IniFilePatch patch)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("需要修改的 INI 文件不存在。", path);
        }

        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var offset = hasBom ? Encoding.UTF8.Preamble.Length : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("INI 文件不是有效的 UTF-8，拒绝自动修改。", ex);
        }

        if (text.IndexOf('\0') >= 0)
        {
            throw new InvalidDataException("INI 文件包含二进制空字符，拒绝自动修改。");
        }

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailingNewline = text.EndsWith("\n", StringComparison.Ordinal);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (hadTrailingNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var sectionStart = FindSection(lines, patch.Section);
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{patch.Section}]");
            foreach (var pair in patch.Values)
            {
                lines.Add($"{pair.Key}={pair.Value}");
            }
        }
        else
        {
            var sectionEnd = FindNextSection(lines, sectionStart + 1);
            foreach (var pair in patch.Values)
            {
                var matches = Enumerable.Range(sectionStart + 1, sectionEnd - sectionStart - 1)
                    .Where(index => IsKey(lines[index], pair.Key))
                    .ToArray();
                if (matches.Length == 0)
                {
                    lines.Insert(sectionEnd, $"{pair.Key}={pair.Value}");
                    sectionEnd++;
                    continue;
                }

                lines[matches[0]] = $"{pair.Key}={pair.Value}";
                // Duplicate keys are ambiguous. Normalize them while the original file
                // remains recoverable in the transaction backup.
                for (var index = matches.Length - 1; index >= 1; index--)
                {
                    lines.RemoveAt(matches[index]);
                    sectionEnd--;
                }
            }
        }

        var output = string.Join(newline, lines) + (hadTrailingNewline ? newline : string.Empty);
        var encoding = new UTF8Encoding(hasBom);
        var temp = path + $".zzzswitch-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding))
            {
                writer.Write(output);
                writer.Flush();
                stream.Flush(true);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public bool Matches(string path, IniFilePatch patch)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = File.ReadAllText(path, new UTF8Encoding(false, true));
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        var sectionStart = FindSection(lines, patch.Section);
        if (sectionStart < 0)
        {
            return false;
        }

        var sectionEnd = FindNextSection(lines, sectionStart + 1);
        return patch.Values.All(pair =>
            Enumerable.Range(sectionStart + 1, sectionEnd - sectionStart - 1)
                .Any(index => IsKeyValue(lines[index], pair.Key, pair.Value)));
    }

    private static int FindSection(IReadOnlyList<string> lines, string section)
    {
        var expected = $"[{section}]";
        for (var index = 0; index < lines.Count; index++)
        {
            if (string.Equals(lines[index].Trim(), expected, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindNextSection(IReadOnlyList<string> lines, int start)
    {
        for (var index = start; index < lines.Count; index++)
        {
            var value = lines[index].Trim();
            if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            {
                return index;
            }
        }

        return lines.Count;
    }

    private static bool IsKey(string line, string key)
    {
        var separator = line.IndexOf('=');
        return separator >= 0 &&
               string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyValue(string line, string key, string value)
    {
        var separator = line.IndexOf('=');
        return separator >= 0 &&
               string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(line[(separator + 1)..], value, StringComparison.Ordinal);
    }
}
