using System.IO.Compression;
using System.Diagnostics;

namespace ZZZSwitch.Update;

public static class UpdatePayload
{
    public static readonly string[] Required = ["ZZZSwitch.exe", "ZZZSwitch.Updater.exe",
        "config/profiles/global.json", "config/profiles/cn_official.json", "config/profiles/bilibili.json",
        "config/transitions/global-to-cn-official.json", "config/transitions/global-to-bilibili.json",
        "config/transitions/cn-official-to-global.json", "config/transitions/cn-official-to-bilibili.json",
        "config/transitions/bilibili-to-global.json", "config/transitions/bilibili-to-cn-official.json"];
    public static readonly HashSet<string> Allowed = new(Required.Concat(["README.md", "ZZZSwitch.ManifestTool.runtimeconfig.json"]), StringComparer.OrdinalIgnoreCase);
    public const long MaxExpandedSize = 2L * 1024 * 1024 * 1024;

    public static void ValidateInstallation(string root, IEnumerable<string> protectedRoots)
    {
        root = UpdatePaths.Normalize(root); UpdatePaths.EnsureOrdinary(root);
        if (UpdatePaths.Same(root, Path.GetPathRoot(root)!) || !File.Exists(UpdatePaths.Under(root, "ZZZSwitch.exe")))
            throw new UpdateException(UpdateError.Installation, "Choose an existing portable ZZZSwitch installation.");
        foreach (var p in protectedRoots.Where(p => !string.IsNullOrWhiteSpace(p)))
            if (UpdatePaths.Overlaps(root, p)) throw new UpdateException(UpdateError.UnsafePath, "Application and user/game storage must be separate directories.");
        if (File.Exists(Path.Combine(root, "version_info")) || Directory.Exists(Path.Combine(root, "ZenlessZoneZero_Data")) || Directory.Exists(Path.Combine(root, ".zzzswitch")))
            throw new UpdateException(UpdateError.UnsafePath, "Refusing to update inside a game directory.");
    }

    public static void Extract(string archive, string staging, string version)
    {
        UpdatePaths.EnsureOrdinary(archive); UpdatePaths.EnsureOrdinary(staging);
        if (Directory.Exists(staging)) throw new UpdateException(UpdateError.InvalidPackage, "Staging must be new.");
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count > 64) throw new UpdateException(UpdateError.InvalidPackage, "Too many ZIP entries.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var directory = name.EndsWith('/');
            UpdatePaths.Under(staging, directory ? name.TrimEnd('/') : name);
            var type = (entry.ExternalAttributes >> 16) & 0xF000;
            if (type is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new UpdateException(UpdateError.UnsafePath, "ZIP links/devices are forbidden.");
            if (directory)
            {
                if (name is not ("config/" or "config/profiles/" or "config/transitions/")) throw new UpdateException(UpdateError.InvalidPackage, "Unexpected ZIP directory.");
                continue;
            }
            if (!Allowed.Contains(name) || !paths.Add(name) || entry.Length <= 0 || entry.Length > MaxExpandedSize || total > MaxExpandedSize - entry.Length)
                throw new UpdateException(UpdateError.InvalidPackage, "Unexpected, duplicate or oversized ZIP file: " + name);
            total += entry.Length;
        }
        if (Required.Any(p => !paths.Contains(p))) throw new UpdateException(UpdateError.InvalidPackage, "Update ZIP is incomplete.");
        EnsureSpace(staging, total * 3 + 64 * 1024 * 1024);
        Directory.CreateDirectory(staging);
        foreach (var entry in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            var destination = UpdatePaths.Under(staging, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[131072]; long written = 0; int n;
            while ((n = input.Read(buffer)) != 0)
            {
                written += n;
                if (written > entry.Length) throw new UpdateException(UpdateError.InvalidPackage, "ZIP expanded beyond declared size.");
                output.Write(buffer, 0, n);
            }
            if (written != entry.Length) throw new UpdateException(UpdateError.InvalidPackage, "Truncated ZIP entry.");
            output.Flush(true);
        }
        ValidateStaging(staging, version);
    }

    public static void ValidateStaging(string staging, string version)
    {
        UpdatePaths.EnsureTree(staging);
        foreach (var p in Required)
            if (!File.Exists(UpdatePaths.Under(staging, p))) throw new UpdateException(UpdateError.InvalidPackage, "Missing required program file: " + p);
        foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, file).Replace('\\', '/');
            if (!Allowed.Contains(relative)) throw new UpdateException(UpdateError.InvalidPackage, "Unknown staging file.");
            if (relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new UpdateException(UpdateError.InvalidPackage, "Configuration too large.");
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                if (json.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new UpdateException(UpdateError.InvalidPackage, "Invalid program configuration.");
            }
        }
        var info = FileVersionInfo.GetVersionInfo(UpdatePaths.Under(staging, "ZZZSwitch.exe"));
        if (info.ProductName != "ZZZSwitch" || UpdateVersion.Parse(info.ProductVersion).CompareTo(UpdateVersion.Parse(version)) != 0)
            throw new UpdateException(UpdateError.InvalidPackage, "Program product/version differs from manifest.");
        foreach (var exe in new[] { "ZZZSwitch.exe", "ZZZSwitch.Updater.exe" })
        {
            using var stream = File.OpenRead(UpdatePaths.Under(staging, exe));
            if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') throw new UpdateException(UpdateError.InvalidPackage, "Invalid Windows executable.");
        }
    }
    public static void EnsureSpace(string path, long needed)
    {
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace < needed)
            throw new UpdateException(UpdateError.Storage, "Insufficient disk space for staging and rollback copies.");
    }
}
