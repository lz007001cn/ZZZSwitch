using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZManifestDiffDemo;

namespace ZZZManifestDiffDemo.Tests;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Test)> Tests =
    [
        ("内置演示包含四类结果", DemoContainsAllStates),
        ("差异行包含大小与 MD5", DifferenceRowsContainDetails),
        ("snapshot JSON 可重新载入", SnapshotCanBeLoaded),
        ("支持 JSON CSV TXT 导出", ReportsCanBeExported)
    ];

    public static async Task<int> Main()
    {
        var passed = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                await test();
                Console.WriteLine($"PASS  {name}");
                passed++;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"FAIL  {name}");
                Console.WriteLine($"      {exception.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"结果：{passed}/{Tests.Count} 通过");
        return passed == Tests.Count ? 0 : 1;
    }

    private static Task DemoContainsAllStates()
    {
        var service = new ManifestComparisonService();
        var demo = ManifestComparisonService.CreateDemoSnapshots();
        var result = service.Compare(demo.Source, demo.Target);

        Equal(3, result.Diff.Summary.Modified);
        Equal(2, result.Diff.Summary.Added);
        Equal(1, result.Diff.Summary.Removed);
        Equal(3, result.Diff.Summary.Same);
        Equal(9, result.Rows.Count);
        Equal(4, result.Rows.Select(row => row.State).Distinct().Count());
        return Task.CompletedTask;
    }

    private static Task DifferenceRowsContainDetails()
    {
        var service = new ManifestComparisonService();
        var demo = ManifestComparisonService.CreateDemoSnapshots();
        var result = service.Compare(demo.Source, demo.Target);

        var modified = result.Rows.Single(row => row.Path.EndsWith("app.info", StringComparison.Ordinal));
        Equal(DifferenceState.Modified, modified.State);
        Equal(1_284L, modified.SourceSize);
        Equal(1_412L, modified.TargetSize);
        True(modified.SourceMd5 is { Length: 32 });
        True(modified.TargetMd5 is { Length: 32 });

        var added = result.Rows.Single(row => row.Path.EndsWith("PCGameSDK.dll", StringComparison.Ordinal));
        Equal(DifferenceState.Added, added.State);
        Equal(null, added.SourceSize);
        Equal(6_291_456L, added.TargetSize);
        return Task.CompletedTask;
    }

    private static async Task SnapshotCanBeLoaded()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var snapshot = ManifestComparisonService.CreateDemoSnapshots().Source;
            var path = Path.Combine(directory, "snapshot.json");
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot, options));

            var loaded = await new ManifestComparisonService().LoadAsync(path);
            Equal(snapshot.Region, loaded.Region);
            Equal(snapshot.Version, loaded.Version);
            Equal(snapshot.ManifestId, loaded.ManifestId);
            Equal(snapshot.Entries.Count, loaded.Entries.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ReportsCanBeExported()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var service = new ManifestComparisonService();
            var demo = ManifestComparisonService.CreateDemoSnapshots();
            var result = service.Compare(demo.Source, demo.Target);

            var json = Path.Combine(directory, "report.json");
            var csv = Path.Combine(directory, "report.csv");
            var text = Path.Combine(directory, "report.txt");
            await service.ExportAsync(result, json);
            await service.ExportAsync(result, csv);
            await service.ExportAsync(result, text);

            True((await File.ReadAllTextAsync(json)).Contains("\"modified\": 3", StringComparison.Ordinal));
            True((await File.ReadAllTextAsync(csv)).Contains("Modified", StringComparison.Ordinal));
            True((await File.ReadAllTextAsync(text)).Contains("Modified: 3", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ZZZManifestDiffDemo.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }
}
