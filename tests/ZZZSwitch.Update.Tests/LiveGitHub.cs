using System.Net.Http;
using System.Text.Json;
using ZZZSwitch.Update;

namespace ZZZSwitch.Update.Tests;
internal static partial class Program
{
    // Opt-in live verification; downloads only into a new fixture directory, never installs.
    private static async Task<int> VerifyLiveGitHubAsync(string root)
    {
        try
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("Use a new verification directory.");
            Directory.CreateDirectory(root);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ZZZSwitch/1.3.7");
            var json = await http.GetStringAsync(GitHubReleaseUpdateSource.Endpoint);
            using var doc = JsonDocument.Parse(json);
            var release = doc.RootElement;
            var version = release.GetProperty("tag_name").GetString()![1..];
            var name = GitHubReleaseUpdateSource.PackageName(version);
            var asset = release.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
            var digest = asset.GetProperty("digest").GetString()!;
            Assert(digest.StartsWith("sha256:"));
            var service = new UpdateService();
            var latest = await service.CheckForUpdatesAsync(GitHubReleaseUpdateSource.Endpoint, version, "stable", default);
            Assert(latest.Status == UpdateCheckStatus.UpToDate && latest.Release!.Version == version);
            var older = await service.CheckForUpdatesAsync(GitHubReleaseUpdateSource.Endpoint, "0.0.0", "stable", default);
            Assert(older.Status is UpdateCheckStatus.Available or UpdateCheckStatus.Unavailable);
            // Exercise the public source and production downloader without installing the legacy ZIP.
            var manifest = Manifest() with { Version = version, PublishedAt = release.GetProperty("published_at").GetDateTimeOffset(),
                Package = new() { Url = asset.GetProperty("browser_download_url").GetString()!, Size = asset.GetProperty("size").GetInt64(), Sha256 = digest[7..] } };
            var path = await service.DownloadUpdateAsync(manifest, Path.Combine(root, "download"), null, default);
            await service.VerifyPackageAsync(path, manifest.Package, default);
            UpdatePaths.WriteJson(Path.Combine(root, "result.json"), new { version, name, manifest.Package.Size, manifest.Package.Sha256,
                checkedEqual = latest.Status.ToString(), checkedOlder = older.Status.ToString(), downloadedAndVerified = true,
                installed = false, reason = older.Release?.UnavailableReason });
            Console.WriteLine("PASS Live GitHub API -> exact ZIP -> restricted redirect -> streaming download -> size/SHA256 (no installation)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
