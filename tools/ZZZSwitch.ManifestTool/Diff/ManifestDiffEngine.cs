namespace ZZZSwitch.ManifestTool.Diff;

public sealed class ManifestDiffEngine
{
    public ManifestDiff Compare(ManifestSnapshot source, ManifestSnapshot target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!string.Equals(source.Game, target.Game, StringComparison.Ordinal))
        {
            throw new ArgumentException("Source and target snapshots are for different games.");
        }

        var sourceIndex = source.Entries.ToDictionary(
            entry => entry.Path,
            StringComparer.OrdinalIgnoreCase);
        var targetIndex = target.Entries.ToDictionary(
            entry => entry.Path,
            StringComparer.OrdinalIgnoreCase);
        var allPaths = sourceIndex.Keys.Concat(targetIndex.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

        var same = 0;
        var modified = new List<FileDifference>();
        var added = new List<FileDifference>();
        var removed = new List<FileDifference>();
        foreach (var path in allPaths)
        {
            var hasSource = sourceIndex.TryGetValue(path, out var sourceEntry);
            var hasTarget = targetIndex.TryGetValue(path, out var targetEntry);
            if (hasSource && hasTarget)
            {
                if (sourceEntry!.Size == targetEntry!.Size &&
                    string.Equals(sourceEntry.Md5, targetEntry.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    same++;
                }
                else
                {
                    modified.Add(Create(path, sourceEntry, targetEntry));
                }
            }
            else if (hasTarget)
            {
                added.Add(Create(path, null, targetEntry));
            }
            else
            {
                removed.Add(Create(path, sourceEntry, null));
            }
        }

        var version = string.Equals(source.Version, target.Version, StringComparison.Ordinal)
            ? source.Version
            : $"{source.Version}-to-{target.Version}";
        return new ManifestDiff(
            source.Game,
            version,
            source.Region,
            target.Region,
            new ManifestDiffSummary(same, modified.Count, added.Count, removed.Count),
            modified,
            added,
            removed);
    }

    private static FileDifference Create(
        string path,
        ManifestEntry? source,
        ManifestEntry? target) => new(
            path,
            source?.Size,
            target?.Size,
            source?.Md5,
            target?.Md5);
}
