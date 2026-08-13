using System.Text.Json;
using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Core.Services;

public sealed class OperationLogger
{
    private readonly AppPaths _paths;
    private static readonly JsonSerializerOptions CompactOptions = new(JsonSupport.Options) { WriteIndented = false };

    public OperationLogger(AppPaths paths) => _paths = paths;

    public void Write(OperationLogEntry entry)
    {
        _paths.EnsureWritableDirectories();
        var path = Path.Combine(_paths.LogsRoot, $"{DateTimeOffset.Now:yyyy-MM-dd}.jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(entry, CompactOptions) + Environment.NewLine);
    }
}
