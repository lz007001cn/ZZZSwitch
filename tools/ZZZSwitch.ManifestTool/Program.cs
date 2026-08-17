using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZSwitch.ManifestTool.Classification;
using ZZZSwitch.ManifestTool.Diff;
using ZZZSwitch.ManifestTool.Download;
using ZZZSwitch.ManifestTool.Output;
using ZZZSwitch.ManifestTool.Sophon;

namespace ZZZSwitch.ManifestTool;

internal static class Program
{
    private const string DownloadMarkerFile = ".zzzswitch-manifest-download.json";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            PrintHelp();
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var command = args[0].ToLowerInvariant();
            var options = CommandOptions.Parse(args[1..]);
            return command switch
            {
                "branches" => await RunBranchesAsync(options, cancellation.Token),
                "manifest" => await RunManifestAsync(options, cancellation.Token),
                "diff" => await RunDiffAsync(options, cancellation.Token),
                "classify" => await RunClassifyAsync(options, cancellation.Token),
                "download" => await RunDownloadAsync(options, cancellation.Token),
                _ => throw new CommandLineException($"Unknown command '{args[0]}'.")
            };
        }
        catch (CommandLineException ex)
        {
            Console.Error.WriteLine($"参数错误：{ex.Message}");
            Console.Error.WriteLine("使用 --help 查看示例。");
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"失败：{ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunBranchesAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        options.EnsureAllowed(["region"], ["verbose"]);
        var region = ParseRegion(options.Required("region"));
        using var transport = new HttpSophonTransport();
        var client = new SophonClient(transport, Verbose(options));
        var catalog = await client.GetGameBranchesAsync(region, cancellationToken);
        foreach (var branch in catalog.Branches)
        {
            Console.WriteLine($"Region: {region}");
            Console.WriteLine($"Game: {SophonRegionConfig.Game}");
            Console.WriteLine($"Game id: {branch.GameId}");
            Console.WriteLine($"Main package_id: {branch.Main.PackageId}");
            Console.WriteLine($"Main version: {branch.Main.Version}");
            Console.WriteLine($"Password present: {branch.Main.HasPassword}");
            if (branch.PreDownload is null)
            {
                Console.WriteLine("Pre-download: none");
            }
            else
            {
                Console.WriteLine($"Pre-download package_id: {branch.PreDownload.PackageId}");
                Console.WriteLine($"Pre-download version: {branch.PreDownload.Version}");
            }
        }

        return 0;
    }

    private static async Task<int> RunManifestAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        options.EnsureAllowed(
            ["region", "version", "category", "output"],
            ["verbose", "no-cache"]);
        var region = ParseRegion(options.Required("region"));
        var version = options.Required("version");
        var output = options.ValueOrDefault("output") ?? Path.Combine(Environment.CurrentDirectory, "manifest-output");
        var result = await FetchAsync(
            region,
            version,
            options.ValueOrDefault("category"),
            !options.HasFlag("no-cache"),
            options,
            cancellationToken);
        PrintCategories(result.Build, result.Category);

        var directory = Path.GetFullPath(output);
        Directory.CreateDirectory(directory);
        var snapshotPath = Path.Combine(directory, $"manifest-{region}-{result.Snapshot.Version}.json");
        await WriteJsonAsync(snapshotPath, result.Snapshot, cancellationToken);
        Console.WriteLine();
        Console.WriteLine($"Manifest id: {result.Snapshot.ManifestId}");
        Console.WriteLine($"File count: {result.Snapshot.Entries.Count:N0}");
        Console.WriteLine($"Cache: {(result.CacheHit ? "hit" : "downloaded")}");
        Console.WriteLine($"Snapshot: {snapshotPath}");
        return 0;
    }

    private static async Task<int> RunDiffAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        options.EnsureAllowed(
            ["source", "target", "version", "source-category", "target-category", "output"],
            ["verbose", "no-cache", "generate-candidate"]);
        var source = ParseRegion(options.Required("source"));
        var target = ParseRegion(options.Required("target"));
        if (source == target)
        {
            throw new CommandLineException("--source and --target must be different.");
        }

        var version = options.Required("version");
        var useCache = !options.HasFlag("no-cache");
        var sourceResult = await FetchAsync(
            source,
            version,
            options.ValueOrDefault("source-category"),
            useCache,
            options,
            cancellationToken);
        var targetResult = await FetchAsync(
            target,
            version,
            options.ValueOrDefault("target-category"),
            useCache,
            options,
            cancellationToken);

        Console.WriteLine($"Source category: {sourceResult.Category.CategoryId} ({sourceResult.Category.MatchingField})");
        Console.WriteLine($"Target category: {targetResult.Category.CategoryId} ({targetResult.Category.MatchingField})");
        var diff = new ManifestDiffEngine().Compare(sourceResult.Snapshot, targetResult.Snapshot);
        var output = options.ValueOrDefault("output") ?? Path.Combine(Environment.CurrentDirectory, "manifest-output");
        var reportPaths = await new DiffReportWriter(Json).WriteAsync(diff, output, cancellationToken);

        Console.WriteLine();
        Console.WriteLine($"Source: {source} {sourceResult.Snapshot.Version}");
        Console.WriteLine($"Target: {target} {targetResult.Snapshot.Version}");
        Console.WriteLine($"Same: {diff.Summary.Same:N0}");
        Console.WriteLine($"Modified: {diff.Summary.Modified:N0}");
        Console.WriteLine($"Added: {diff.Summary.Added:N0}");
        Console.WriteLine($"Removed: {diff.Summary.Removed:N0}");
        Console.WriteLine($"Text report: {reportPaths.Text}");
        Console.WriteLine($"JSON report: {reportPaths.Json}");

        if (options.HasFlag("generate-candidate"))
        {
            var candidate = await new CandidateManifestWriter(Json).WriteAsync(
                diff, output, cancellationToken);
            Console.WriteLine($"Candidate: {candidate}");
            Console.WriteLine("WARNING: GENERATED CANDIDATE - REQUIRES MANUAL REVIEW");
        }

        return 0;
    }

    private static async Task<int> RunClassifyAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        options.EnsureAllowed(
            ["source", "target", "version", "source-category", "target-category", "output"],
            ["verbose", "no-cache"]);
        var source = ParseRegion(options.Required("source"));
        var target = ParseRegion(options.Required("target"));
        if (source == target)
        {
            throw new CommandLineException("--source and --target must be different.");
        }

        var version = options.Required("version");
        var useCache = !options.HasFlag("no-cache");
        var sourceResult = await FetchAsync(
            source,
            version,
            options.ValueOrDefault("source-category"),
            useCache,
            options,
            cancellationToken);
        var targetResult = await FetchAsync(
            target,
            version,
            options.ValueOrDefault("target-category"),
            useCache,
            options,
            cancellationToken);
        var diff = new ManifestDiffEngine().Compare(sourceResult.Snapshot, targetResult.Snapshot);
        var report = new ManifestClassifier().Classify(diff);
        var output = options.ValueOrDefault("output") ?? Path.Combine(Environment.CurrentDirectory, "manifest-output");
        var paths = await new ClassificationReportWriter(Json).WriteAsync(
            report, output, cancellationToken);

        Console.WriteLine($"Source: {source} {sourceResult.Snapshot.Version}");
        Console.WriteLine($"Target: {target} {targetResult.Snapshot.Version}");
        Console.WriteLine($"Base client: {report.Summary.BaseClient:N0}");
        Console.WriteLine($"Base resource: {report.Summary.BaseResource:N0}");
        Console.WriteLine($"Runtime hot update: {report.Summary.RuntimeHotUpdate:N0}");
        Console.WriteLine($"State metadata: {report.Summary.StateMetadata:N0}");
        Console.WriteLine($"Needs observation: {report.Summary.NeedsObservation:N0}");
        Console.WriteLine($"Text report: {paths.Text}");
        Console.WriteLine($"JSON report: {paths.Json}");
        Console.WriteLine($"CSV report: {paths.Csv}");
        return 0;
    }

    private static async Task<int> RunDownloadAsync(
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        options.EnsureAllowed(
            ["region", "version", "category", "output", "path", "classification-report", "include-class"],
            ["verbose", "no-cache", "accept-download"]);
        var region = ParseRegion(options.Required("region"));
        var version = options.Required("version");
        var output = Path.GetFullPath(options.Required("output"));
        ValidateDownloadRoot(output);
        var directPath = options.ValueOrDefault("path");
        var classificationPath = options.ValueOrDefault("classification-report");
        if (string.IsNullOrWhiteSpace(directPath) == string.IsNullOrWhiteSpace(classificationPath))
        {
            throw new CommandLineException(
                "Specify exactly one of --path or --classification-report.");
        }

        var fetched = await FetchAsync(
            region,
            version,
            options.ValueOrDefault("category"),
            !options.HasFlag("no-cache"),
            options,
            cancellationToken);
        IReadOnlyList<string> selectedPaths = !string.IsNullOrWhiteSpace(directPath)
            ? [ManifestEntry.NormalizePath(directPath!)]
            : await LoadClassifiedPathsAsync(
                classificationPath!, region, fetched.Snapshot.Version,
                options.ValueOrDefault("include-class"), cancellationToken);
        var index = fetched.Snapshot.Entries.ToDictionary(
            entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var entries = selectedPaths.Select(path =>
            index.TryGetValue(path, out var entry)
                ? entry
                : throw new InvalidDataException(
                    $"Selected path is not present in the {region} {fetched.Snapshot.Version} manifest: {path}"))
            .DistinctBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length == 0)
        {
            throw new InvalidDataException("The download selection is empty.");
        }

        var totalBytes = entries.Aggregate(0L, (total, entry) => checked(total + entry.Size));
        Console.WriteLine($"Region: {region}");
        Console.WriteLine($"Version: {fetched.Snapshot.Version}");
        Console.WriteLine($"Manifest id: {fetched.Snapshot.ManifestId}");
        Console.WriteLine($"Selected files: {entries.Length:N0}");
        Console.WriteLine($"Uncompressed bytes: {totalBytes:N0}");
        Console.WriteLine($"Output root: {output}");
        if (!options.HasFlag("accept-download"))
        {
            Console.WriteLine("DRY RUN: no chunks were downloaded and no files were written to the output root.");
            Console.WriteLine("Repeat with --accept-download to perform the verified download.");
            return 0;
        }

        await EnsureDownloadWorkspaceAsync(
            output, region, fetched, cancellationToken).ConfigureAwait(false);
        using var transport = new HttpSophonTransport();
        var downloader = new SophonFileDownloader(transport, Verbose(options));
        var downloaded = new List<DownloadedManifestFile>();
        foreach (var entry in entries)
        {
            Console.WriteLine($"Downloading: {entry.Path}");
            downloaded.Add(await downloader.DownloadAsync(
                entry, fetched.Category, output, cancellationToken).ConfigureAwait(false));
        }

        var report = new SophonDownloadReport(
            SophonRegionConfig.Game,
            region,
            fetched.Snapshot.Version,
            fetched.Category.CategoryId,
            fetched.Snapshot.ManifestId,
            DateTimeOffset.UtcNow,
            downloaded);
        var reportPath = await new DownloadReportWriter(Json).WriteAsync(
            report, output, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"Verified files: {downloaded.Count:N0}");
        Console.WriteLine($"SHA-256 report: {reportPath}");
        return 0;
    }

    private static async Task<ManifestFetchResult> FetchAsync(
        SophonRegion region,
        string version,
        string? category,
        bool useCache,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var log = Verbose(options);
        using var transport = new HttpSophonTransport();
        var client = new SophonClient(transport, log);
        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZZZSwitch",
            "ManifestCache");
        var service = new ManifestService(
            client,
            new SophonManifestReader(transport, log),
            new ManifestCache(cacheRoot, Json),
            log);
        return await service.FetchAsync(region, version, category, useCache, cancellationToken);
    }

    private static void PrintCategories(SophonBuild build, ManifestCategory selected)
    {
        Console.WriteLine("Build manifests:");
        foreach (var item in build.Manifests)
        {
            var marker = ReferenceEquals(item, selected) || item == selected ? "*" : " ";
            Console.WriteLine(
                $"{marker} category_id={item.CategoryId}, matching_field={item.MatchingField}, manifest.id={item.ManifestId}");
        }
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await JsonSerializer.SerializeAsync(stream, value, Json, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> LoadClassifiedPathsAsync(
        string reportPath,
        SophonRegion targetRegion,
        string version,
        string? includeClass,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(reportPath);
        await using var stream = File.OpenRead(path);
        var report = await JsonSerializer.DeserializeAsync<ManifestClassificationReport>(
            stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Classification report is empty: {path}");
        if (report.TargetRegion != targetRegion ||
            !string.Equals(report.Version, version, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Classification report target {report.TargetRegion} {report.Version} does not match requested {targetRegion} {version}.");
        }

        var classes = ParseIncludedClasses(includeClass);
        return report.Files
            .Where(file => file.ChangeType != ManifestChangeType.Removed && classes.Contains(file.FileClass))
            .Select(file => file.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(pathValue => pathValue, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pathValue => pathValue, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<ManifestFileClass> ParseIncludedClasses(string? value)
    {
        var values = string.IsNullOrWhiteSpace(value)
            ? [ManifestFileClass.BaseClient.ToString()]
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = new HashSet<ManifestFileClass>();
        foreach (var item in values)
        {
            if (!Enum.TryParse<ManifestFileClass>(item, ignoreCase: true, out var fileClass))
            {
                throw new CommandLineException(
                    $"Unknown --include-class value '{item}'. Use {string.Join(", ", Enum.GetNames<ManifestFileClass>())}.");
            }

            result.Add(fileClass);
        }

        return result;
    }

    private static void ValidateDownloadRoot(string output)
    {
        var root = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new CommandLineException("Download output cannot be a drive root.");
        }

        var segments = root.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => string.Equals(segment, ".zzzswitch", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CommandLineException("Download output cannot be inside .zzzswitch.");
        }

        for (var current = root; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CommandLineException($"Download output path crosses a reparse point: {current}");
            }

            if (File.Exists(Path.Combine(current, "ZenlessZoneZero.exe")) &&
                Directory.Exists(Path.Combine(current, "ZenlessZoneZero_Data")))
            {
                throw new CommandLineException("Download output cannot be inside a live game installation.");
            }
        }

        if (Directory.Exists(root))
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new CommandLineException("Download output cannot be a reparse point.");
            }

            var marker = Path.Combine(root, DownloadMarkerFile);
            if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(root).Any())
            {
                throw new CommandLineException(
                    "Download output must be empty or contain a ManifestTool download marker from an earlier run.");
            }
        }
    }

    private static async Task EnsureDownloadWorkspaceAsync(
        string output,
        SophonRegion region,
        ManifestFetchResult fetched,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        var markerPath = Path.Combine(output, DownloadMarkerFile);
        var expected = new DownloadWorkspaceMarker(
            "ZZZSwitch.ManifestTool",
            SophonRegionConfig.Game,
            region,
            fetched.Snapshot.Version,
            fetched.Category.CategoryId,
            fetched.Snapshot.ManifestId);
        if (File.Exists(markerPath))
        {
            if ((File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Download workspace marker is a reparse point: {markerPath}");
            }

            await using var stream = File.OpenRead(markerPath);
            var actual = await JsonSerializer.DeserializeAsync<DownloadWorkspaceMarker>(
                stream, Json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Download workspace marker is empty: {markerPath}");
            if (actual != expected)
            {
                throw new InvalidDataException(
                    "Download workspace belongs to a different region, version, category, or manifest.");
            }

            return;
        }

        await WriteJsonAsync(markerPath, expected, cancellationToken).ConfigureAwait(false);
    }

    private static Action<string>? Verbose(CommandOptions options) =>
        options.HasFlag("verbose") ? message => Console.WriteLine($"VERBOSE {message}") : null;

    private static SophonRegion ParseRegion(string value) =>
        Enum.TryParse<SophonRegion>(value, ignoreCase: true, out var region)
            ? region
            : throw new CommandLineException($"Unknown region '{value}'. Use OS, CN, or Bilibili.");

    private static void PrintHelp()
    {
        Console.WriteLine("ZZZSwitch Sophon Manifest test tool (metadata only)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  branches --region OS|CN [--verbose]");
        Console.WriteLine("  manifest --region OS|CN --version 3.1.0 [--category ID] [--output PATH] [--no-cache] [--verbose]");
        Console.WriteLine("  diff --source OS|CN --target CN|OS --version 3.1.0 [--source-category ID] [--target-category ID]");
        Console.WriteLine("       [--output PATH] [--no-cache] [--generate-candidate] [--verbose]");
        Console.WriteLine("  classify --source OS|CN --target CN|OS --version 3.1.0 [--output PATH] [--no-cache] [--verbose]");
        Console.WriteLine("  download --region OS|CN --version 3.1.0 --output PATH (--path FILE | --classification-report JSON)");
        Console.WriteLine("       [--include-class BaseClient] [--no-cache] [--verbose] [--accept-download]");
        Console.WriteLine();
        Console.WriteLine("Manifest and diff commands only read metadata. Download is a dry run unless --accept-download is explicit.");
        Console.WriteLine("Download refuses game and .zzzswitch paths and never overwrites config/transitions.");
    }
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CommandOptions Parse(IReadOnlyList<string> args)
    {
        var result = new CommandOptions();
        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                throw new CommandLineException($"Unexpected argument '{token}'.");
            }

            var name = token[2..];
            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (!result._values.TryAdd(name, args[++index]))
                {
                    throw new CommandLineException($"Option '--{name}' was specified more than once.");
                }
            }
            else if (!result._flags.Add(name))
            {
                throw new CommandLineException($"Flag '--{name}' was specified more than once.");
            }
        }

        return result;
    }

    public string Required(string name) =>
        _values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CommandLineException($"Missing required option '--{name}'.");

    public string? ValueOrDefault(string name) =>
        _values.TryGetValue(name, out var value) ? value : null;

    public bool HasFlag(string name) => _flags.Contains(name);

    public void EnsureAllowed(IReadOnlyCollection<string> values, IReadOnlyCollection<string> flags)
    {
        var unknownValue = _values.Keys.FirstOrDefault(key => !values.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknownValue is not null)
        {
            throw new CommandLineException($"Option '--{unknownValue}' is not valid for this command.");
        }

        var unknownFlag = _flags.FirstOrDefault(key => !flags.Contains(key, StringComparer.OrdinalIgnoreCase));
        if (unknownFlag is not null)
        {
            throw new CommandLineException($"Flag '--{unknownFlag}' is not valid for this command.");
        }
    }
}

internal sealed class CommandLineException : ArgumentException
{
    public CommandLineException(string message) : base(message)
    {
    }
}
