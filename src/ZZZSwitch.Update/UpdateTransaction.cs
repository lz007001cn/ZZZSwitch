namespace ZZZSwitch.Update;

public sealed record UpdateFileRecord(string Path, string? OldHash, long OldSize, string NewHash, long NewSize);
public sealed record UpdateJournal
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Version { get; init; }
    public required string Phase { get; set; }
    public List<UpdateFileRecord> Files { get; init; } = [];
}

// Only program files are touched. All recovery paths are recomputed from this installation root.
public sealed class UpdateTransaction
{
    public string InstallRoot { get; }
    public string Root { get; }
    public string JournalPath => UpdatePaths.Under(Root, "journal.json");
    public Action<string>? Checkpoint { get; init; }
    public UpdateTransaction(string installRoot)
    { InstallRoot = UpdatePaths.Normalize(installRoot); Root = UpdatePaths.Under(InstallRoot, ".zzzswitch-update"); }
    public FileStream AcquireLock()
    {
        UpdatePaths.EnsureOrdinary(Root); Directory.CreateDirectory(Root);
        return new FileStream(UpdatePaths.Under(Root, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public UpdateJournal Load()
    {
        var j = UpdatePaths.ReadJson<UpdateJournal>(JournalPath);
        if (j.SchemaVersion != 1 || !Guid.TryParseExact(j.Id, "N", out _) || j.Phase is not ("Preparing" or "Prepared" or "Applying" or "AwaitingHealth" or "Committed" or "RolledBack") || j.Files is null || j.Files.Count > UpdatePayload.Allowed.Count)
            throw new UpdateException(UpdateError.Recovery, "Invalid update journal; preserve for manual investigation.");
        UpdateVersion.Parse(j.Version);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in j.Files)
        {
            if (!UpdatePayload.Allowed.Contains(f.Path) || !seen.Add(f.Path) || !UpdateManifestParser.IsHash(f.NewHash) ||
                (f.OldHash is not null && !UpdateManifestParser.IsHash(f.OldHash)) || f.NewSize <= 0 || f.NewSize > UpdatePayload.MaxExpandedSize || f.OldSize < 0 || f.OldSize > UpdatePayload.MaxExpandedSize)
                throw new UpdateException(UpdateError.Recovery, "Unsafe update journal file record.");
            UpdatePaths.Under(InstallRoot, f.Path);
        }
        if (j.Phase != "Preparing" && UpdatePayload.Required.Any(p => !seen.Contains(p)))
            throw new UpdateException(UpdateError.Recovery, "Incomplete update journal.");
        return j;
    }
    private void Save(UpdateJournal j, string phase) { j.Phase = phase; UpdatePaths.WriteJson(JournalPath, j); Checkpoint?.Invoke(phase); }

    public void Prepare(string archive, string version, string id)
    {
        if (File.Exists(JournalPath)) throw new UpdateException(UpdateError.Recovery, "An update journal already exists. Recover it first.");
        if (!Guid.TryParseExact(id, "N", out _)) throw new UpdateException(UpdateError.Installation, "Invalid job ID.");
        if (Directory.EnumerateFileSystemEntries(Root).Any(p => Path.GetFileName(p) != "update.lock"))
            throw new UpdateException(UpdateError.Recovery, "Unclaimed update files require manual inspection.");
        var journal = new UpdateJournal { Id = id, Version = version, Phase = "Preparing" };
        Save(journal, "Preparing");
        var staging = UpdatePaths.Under(Root, "staging");
        UpdatePayload.Extract(archive, staging, version);
        long oldSize = 0;
        foreach (var path in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(staging, path).Replace('\\', '/');
            var original = UpdatePaths.Under(InstallRoot, relative);
            if (Directory.Exists(original)) throw new UpdateException(UpdateError.Installation, "Program file path is a directory.");
            var exists = File.Exists(original);
            var length = exists ? new FileInfo(original).Length : 0;
            oldSize = checked(oldSize + length);
            journal.Files.Add(new(relative, exists ? UpdatePaths.Hash(original) : null, length, UpdatePaths.Hash(path), new FileInfo(path).Length));
        }
        UpdatePayload.EnsureSpace(Root, oldSize * 2 + journal.Files.Max(f => f.NewSize) + 64 * 1024 * 1024);
        foreach (var file in journal.Files)
        {
            if (file.OldHash is null) continue;
            var source = UpdatePaths.Under(InstallRoot, file.Path);
            var backup = UpdatePaths.Under(Root, "backup/" + file.Path);
            Checkpoint?.Invoke("Backup:" + file.Path);
            CopyVerified(source, backup, file.OldHash, file.OldSize);
        }
        Save(journal, "Prepared");
    }

    public void Apply()
    {
        var j = Load();
        if (j.Phase != "Prepared") throw new UpdateException(UpdateError.Installation, "Update is not prepared.");
        foreach (var f in j.Files)
        {
            Verify(UpdatePaths.Under(Root, "staging/" + f.Path), f.NewHash, f.NewSize);
            var path = UpdatePaths.Under(InstallRoot, f.Path);
            if (f.OldHash is null)
            { if (File.Exists(path)) throw new UpdateException(UpdateError.Installation, "Program directory changed during preparation."); }
            else
            {
                Verify(path, f.OldHash, f.OldSize);
                Verify(UpdatePaths.Under(Root, "backup/" + f.Path), f.OldHash, f.OldSize);
                using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        Save(j, "Applying");
        foreach (var f in j.Files.OrderBy(f => f.Path.Equals("ZZZSwitch.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
        {
            InstallAtomic(UpdatePaths.Under(Root, "staging/" + f.Path), UpdatePaths.Under(InstallRoot, f.Path), f.NewHash, f.NewSize);
            Checkpoint?.Invoke("Replaced:" + f.Path);
        }
        Save(j, "AwaitingHealth");
    }

    public void Commit()
    {
        var j = Load();
        if (j.Phase != "AwaitingHealth") throw new UpdateException(UpdateError.Installation, "No update awaiting health confirmation.");
        foreach (var f in j.Files) Verify(UpdatePaths.Under(InstallRoot, f.Path), f.NewHash, f.NewSize);
        Save(j, "Committed");
        Cleanup();
    }

    public void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        var j = Load();
        if (j.Phase is "Committed" or "RolledBack" or "Preparing") { Cleanup(); return; }
        // Verify every undo input before modifying even a single program file.
        foreach (var f in j.Files.Where(f => f.OldHash is not null))
            Verify(UpdatePaths.Under(Root, "backup/" + f.Path), f.OldHash!, f.OldSize);
        foreach (var f in j.Files)
        {
            var target = UpdatePaths.Under(InstallRoot, f.Path);
            if (f.OldHash is not null)
            {
                // Unchanged files need no replacement; this also recovers an unsupported/denied ReplaceFile attempt.
                if (File.Exists(target) && new FileInfo(target).Length == f.OldSize && string.Equals(UpdatePaths.Hash(target), f.OldHash, StringComparison.OrdinalIgnoreCase)) continue;
                InstallAtomic(UpdatePaths.Under(Root, "backup/" + f.Path), target, f.OldHash, f.OldSize);
            }
            else if (File.Exists(target))
            {
                Verify(target, f.NewHash, f.NewSize);
                Retry(() => File.Delete(target));
            }
        }
        Save(j, "RolledBack");
        Cleanup();
    }

    private void Cleanup()
    {
        // Journal is removed last: interrupted cleanup remains safe to repeat.
        foreach (var folder in new[] { "staging", "backup", "next" }) UpdatePaths.DeleteTree(UpdatePaths.Under(Root, folder));
        foreach (var name in new[] { "health.json", "launch.json", "journal.json.tmp" })
        { var path = UpdatePaths.Under(Root, name); if (File.Exists(path)) File.Delete(path); }
        File.Delete(JournalPath);
    }

    private void InstallAtomic(string source, string target, string hash, long size)
    {
        var next = UpdatePaths.Under(Root, "next/file.tmp");
        if (File.Exists(next)) File.Delete(next);
        CopyVerified(source, next, hash, size);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Retry(() =>
        {
            UpdatePaths.EnsureOrdinary(target); UpdatePaths.EnsureOrdinary(next);
            if (File.Exists(target)) File.Replace(next, target, null);
            else File.Move(next, target);
        });
        Verify(target, hash, size);
    }
    private static void CopyVerified(string source, string destination, string hash, long size)
    {
        UpdatePaths.EnsureOrdinary(source); UpdatePaths.EnsureOrdinary(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { input.CopyTo(output); output.Flush(true); }
        Verify(destination, hash, size);
    }
    public static void Verify(string path, string hash, long size)
    {
        UpdatePaths.EnsureOrdinary(path);
        if (!File.Exists(path) || new FileInfo(path).Length != size || !string.Equals(UpdatePaths.Hash(path), hash, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException(UpdateError.Recovery, "Program file integrity check failed: " + path);
    }
    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < 4) { Thread.Sleep(200 * (i + 1)); }
        }
    }
}
