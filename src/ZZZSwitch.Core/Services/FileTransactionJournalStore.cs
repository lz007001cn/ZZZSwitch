using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class FileTransactionJournalStore
{
    private readonly AppPaths _paths;

    public FileTransactionJournalStore(AppPaths paths) => _paths = paths;

    public bool Exists => File.Exists(_paths.FileTransactionJournalFile);

    public FileTransactionJournal? Load()
    {
        if (!Exists)
        {
            return null;
        }

        using var stream = File.OpenRead(_paths.FileTransactionJournalFile);
        return JsonSerializer.Deserialize<FileTransactionJournal>(stream, JsonSupport.Options)
               ?? throw new InvalidDataException("普通文件事务日志内容为空。");
    }

    public void Save(FileTransactionJournal journal)
    {
        _paths.EnsureWritableDirectories();
        AtomicJsonFile.Write(_paths.FileTransactionJournalFile, journal);
    }

    public bool TryDelete()
    {
        try
        {
            File.Delete(_paths.FileTransactionJournalFile);
            File.Delete(_paths.FileTransactionJournalFile + ".tmp");
            return !File.Exists(_paths.FileTransactionJournalFile);
        }
        catch
        {
            return false;
        }
    }
}
