using System.Net;
using System.Text.Json;
using ZZZSwitch.Update;

namespace ZZZSwitch.Update.Tests;
internal static partial class Program
{
    private static void AddGitHubTests(List<(string, Func<Task>)> tests)
    {
        const string endpoint = GitHubReleaseUpdateSource.Endpoint;
        const string zip = "ZZZSwitch-win-x64-v1.10.0.zip";
        var hash = new string('a', 64);
        object Asset(string name, string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") => new { name, digest, size = 1024, browser_download_url = "https://github.com/lz007001cn/ZZZSwitch/releases/download/v1.10.0/" + name };
        string Release(object[]? assets, string tag = "v1.10.0", string? body = "notes") => JsonSerializer.Serialize(new
        { tag_name = tag, name = "Release", body, published_at = DateTimeOffset.UtcNow, html_url = "https://github.com/lz007001cn/ZZZSwitch/releases/tag/" + tag, assets });
        tests.Add(("GitHub new/equal/older, numeric order, null body, explicit package and UA", async () =>
        {
            using var http = Client(r =>
            {
                Assert(r.Headers.UserAgent.ToString().StartsWith("ZZZSwitch/"));
                Assert(r.RequestUri!.AbsoluteUri == endpoint); // No checksum-file or ZIP request while checking.
                return Response(Release([Asset("ZZZSwitch-Packages-3.0.0.zip"), Asset("other.zip"), Asset(zip)], body: null));
            });
            var service = new UpdateService(http);
            var newer = await service.CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default);
            Assert(newer.Status == UpdateCheckStatus.Available && newer.Manifest!.Package.Url.EndsWith(zip));
            Assert(newer.Manifest!.Package.Sha256 == hash && newer.Release!.Body == "");
            Assert((await service.CheckForUpdatesAsync(endpoint, "1.10.0", "stable", default)).Status == UpdateCheckStatus.UpToDate);
            Assert((await service.CheckForUpdatesAsync(endpoint, "1.10.0-test.20260917+4ff638fc", "stable", default)).Status == UpdateCheckStatus.UpToDate);
            Assert((await service.CheckForUpdatesAsync(endpoint, "1.11.0", "stable", default)).Status == UpdateCheckStatus.UpToDate);
        }));
        foreach (var mode in new[] { "none", "wrong", "missing-hash", "duplicate", "wrong-digest-algorithm", "invalid-hash" })
            tests.Add(("GitHub unavailable " + mode, async () =>
            {
                object[] assets = mode switch { "none" => [], "wrong" => [Asset("ZZZSwitch-Packages-3.0.0.zip")],
                    "missing-hash" => [Asset(zip, null)], "duplicate" => [Asset(zip), Asset(zip), Asset(zip + ".sha256")], _ => [Asset(zip, mode == "wrong-digest-algorithm" ? "sha512:" + hash : "sha256:bad")] };
                using var http = Client(r => { Assert(r.RequestUri!.AbsoluteUri == endpoint); return Response(Release(assets)); });
                var result = await new UpdateService(http).CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default);
                Assert(result.Status == UpdateCheckStatus.Unavailable && result.Manifest is null && result.Release!.UnavailableReason is not null);
                Assert(result.Release!.Version == "1.10.0" && result.Release.Body == "notes");
            }));
        foreach (var tag in new[] { "oops", "v01.2.3", "v1.10.0-beta.1" })
            tests.Add(("GitHub invalid stable tag " + tag, async () =>
            {
                using var http = Client(_ => Response(Release([], tag)));
                await ThrowsAsync(() => new UpdateService(http).CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default), UpdateError.InvalidManifest);
            }));
        foreach (var status in new[] { 403, 404, 429 })
            tests.Add(("GitHub HTTP " + status, async () =>
            {
                using var http = Client(_ => new((HttpStatusCode)status));
                await ThrowsAsync(() => new UpdateService(http).CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default), status == 429 ? UpdateError.RateLimited : UpdateError.Http);
            }));
        tests.Add(("GitHub 403 rate limit", async () =>
        {
            using var http = Client(_ => { var r = new HttpResponseMessage(HttpStatusCode.Forbidden); r.Headers.Add("X-RateLimit-Remaining", "0"); return r; });
            await ThrowsAsync(() => new UpdateService(http).CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default), UpdateError.RateLimited);
        }));
        foreach (var json in new[] { "{", "null", "{}", "{\"published_at\":\"invalid\"}" })
            tests.Add(("GitHub malformed JSON " + json, async () =>
            {
                using var http = Client(_ => Response(json));
                await ThrowsAsync(() => new UpdateService(http).CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default));
            }));
        tests.Add(("GitHub timeout and cancellation", async () =>
        {
            using var http = new HttpClient(new Handler(async (_, t) => { await Task.Delay(10000, t); return Response(""); }));
            var service = new UpdateService(http, TimeSpan.FromMilliseconds(30));
            await ThrowsAsync(() => service.CheckForUpdatesAsync(endpoint, "1.9.0", "stable", default), UpdateError.Timeout);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await service.CheckForUpdatesAsync(endpoint, "1.9.0", "stable", cts.Token); throw new Exception("Cancellation expected"); }
            catch (OperationCanceledException) { }
        }));
        foreach (var target in new[] { "https://release-assets.githubusercontent.com/asset", "https://evil.test/asset", "http://release-assets.githubusercontent.com/asset", "https://release-assets.githubusercontent.com.evil.test/asset", "loop" })
            tests.Add(("GitHub bounded redirect " + target, async () =>
            {
                using var f = new Fixture(); var bytes = new byte[1024];
                var m = WithBytes(bytes); m = m with { Package = m.Package with { Url = "https://github.com/lz007001cn/ZZZSwitch/releases/download/v1.10.0/" + zip } };
                var requests = 0;
                using var http = Client(_ =>
                {
                    if (++requests > 1 && target != "loop") return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    var r = new HttpResponseMessage(HttpStatusCode.Redirect);
                    r.Headers.Location = new Uri(target == "loop" ? m.Package.Url : target); return r;
                });
                if (target == "https://release-assets.githubusercontent.com/asset")
                    Assert(File.Exists(await new UpdateService(http).DownloadUpdateAsync(m, f.Job, null, default)));
                else await ThrowsAsync(() => new UpdateService(http).DownloadUpdateAsync(m, f.Job, null, default), UpdateError.Http);
            }));
        tests.Add(("Empty saved endpoint migrates to GitHub without losing automatic preference", () =>
        {
            using var f = new Fixture(); var store = new UpdateSettingsStore(f.Root); store.Save(new("", "beta", false));
            var s = store.Load(); Assert(s.Endpoint == endpoint && s.Channel == "stable" && !s.AutomaticCheck);
            return Task.CompletedTask;
        }));
    }
}
