using System.Security.Cryptography;
using System.Text.Json;

namespace ZZZSwitch.Update;

public static class UpdatePaths
{
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZZZSwitch", "Updates");
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    public static bool Overlaps(string a, string b) => Same(a, b) || Normalize(a).StartsWith(Normalize(b) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Normalize(b).StartsWith(Normalize(a) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static string Under(string root, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Any(p => string.IsNullOrWhiteSpace(p) || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            p.Contains(':') || IsDevice(p))) throw new UpdateException(UpdateError.UnsafePath, "Unsafe update path: " + relative);
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        if (!full.StartsWith(Normalize(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UpdateException(UpdateError.UnsafePath, "Path escapes update root.");
        EnsureOrdinary(full);
        return full;
    }
    private static bool IsDevice(string p)
    {
        p = p.Split('.')[0].ToUpperInvariant();
        return p is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            p.Length == 4 && (p.StartsWith("COM") || p.StartsWith("LPT")) && p[3] is >= '0' and <= '9';
    }
    public static void EnsureOrdinary(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            var attributes = new FileInfo(p).Attributes;
            if (attributes != (FileAttributes)(-1) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new UpdateException(UpdateError.UnsafePath, "Reparse points are not allowed: " + p);
        }
    }
    public static void EnsureTree(string root)
    {
        EnsureOrdinary(root);
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        { EnsureOrdinary(entry); if (Directory.Exists(entry)) EnsureTree(entry); }
    }
    public static string Hash(string path)
    {
        EnsureOrdinary(path);
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    public static void WriteJson<T>(string path, T value)
    {
        EnsureOrdinary(path); EnsureOrdinary(path + ".tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, value, UpdateManifestParser.Json); stream.Flush(true); }
        File.Move(path + ".tmp", path, true);
    }
    public static T ReadJson<T>(string path)
    {
        EnsureOrdinary(path);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Update record too large.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), UpdateManifestParser.Json) ?? throw new InvalidDataException("Empty update record.");
    }
    public static void DeleteTree(string root)
    {
        if (!Directory.Exists(root)) return;
        EnsureTree(root);
        Directory.Delete(root, true);
    }
}

public sealed record UpdateSettings(string Endpoint = GitHubReleaseUpdateSource.Endpoint, string Channel = "stable", bool AutomaticCheck = true);
public sealed class UpdateSettingsStore(string dataRoot)
{
    private readonly string _file = UpdatePaths.Under(dataRoot, "update-settings.json");
    public UpdateSettings Load()
    {
        var settings = File.Exists(_file) ? UpdatePaths.ReadJson<UpdateSettings>(_file) : new();
        return string.IsNullOrWhiteSpace(settings.Endpoint) ? settings with { Endpoint = GitHubReleaseUpdateSource.Endpoint, Channel = "stable" } : settings;
    }
    public void Save(UpdateSettings settings)
    {
        if (settings.Endpoint.Length != 0) UpdateManifestParser.ValidateUrl(settings.Endpoint);
        if (settings.Channel is not ("stable" or "beta")) throw new UpdateException(UpdateError.InvalidManifest, "Invalid channel.");
        UpdatePaths.WriteJson(_file, settings);
    }
}

public static class UpdateLog
{
    public static void Write(string root, string message)
    {
        try
        {
            var file = UpdatePaths.Under(root, "update.log"); Directory.CreateDirectory(root);
            if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, UpdatePaths.Under(root, "update.previous.log"), true);
            File.AppendAllText(file, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (Exception) { /* Logging never blocks the game or recovery. */ }
    }
}
