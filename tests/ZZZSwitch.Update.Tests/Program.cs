using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZZZSwitch.Update;

namespace ZZZSwitch.Update.Tests;

internal static partial class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--github-live") return await VerifyLiveGitHubAsync(args[1]);
        if (args.Length > 0) return await IntegrationAsync(args);
        var tests = new List<(string, Func<Task>)>();
        void Add(string name, Action test) => tests.Add((name, () => { test(); return Task.CompletedTask; }));
        Add("SemVer numeric order, equality, downgrade and prerelease", () =>
        {
            Assert(V("1.3.7").CompareTo(V("1.3.8")) < 0); Assert(V("1.3.7").CompareTo(V("1.3.7+abc")) == 0);
            Assert(V("1.10.0").CompareTo(V("1.9.99")) > 0); Assert(V("1.3.8-test").CompareTo(V("1.3.8")) < 0);
            Assert(V("1.3.8-beta.10").CompareTo(V("1.3.8-beta.2")) > 0);
            foreach (var s in new[] { "1.3", "v1.3.8", "01.3.8", "1.3.-1", "1.3.8-beta.01", "1.3.8-", "99999999999.0.0" }) Throws(() => V(s));
            Assert(UpdateVersion.ParseForUpdateCheck("1.3.7-test.20260917+4ff638fc", "stable").CompareTo(V("1.3.7")) == 0);
            Assert(UpdateVersion.ParseForUpdateCheck("1.3.7-test.20260917+4ff638fc", "stable").CompareTo(V("1.3.8")) < 0);
            Assert(UpdateVersion.ParseForUpdateCheck("1.3.7-beta.1", "stable").CompareTo(V("1.3.7")) < 0);
            Assert(UpdateVersion.ParseForUpdateCheck("1.3.7-test.20260917+4ff638fc", "beta").CompareTo(V("1.3.7")) < 0);
        });
        Add("Manifest valid, unknown fields, required metadata and duplicate keys", () =>
        {
            var json = JsonSerializer.Serialize(Manifest(), UpdateManifestParser.Json);
            Assert(UpdateManifestParser.Parse(json).Version == "1.3.8");
            Assert(UpdateManifestParser.Parse(json.Replace("\"schemaVersion\": 1", "\"future\": true, \"schemaVersion\": 1")).Version == "1.3.8");
            foreach (var property in new[] { "schemaVersion", "version", "channel", "mandatory", "minSupportedVersion", "publishedAt", "releaseNotes", "package" })
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(json)!; node.AsObject().Remove(property);
                Throws(() => UpdateManifestParser.Parse(node.ToJsonString()));
            }
            Throws(() => UpdateManifestParser.Parse("{"), UpdateError.InvalidJson);
            Throws(() => UpdateManifestParser.Parse(json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1, \"schemaVersion\": 1")), UpdateError.InvalidJson);
            Throws(() => UpdateManifestParser.Validate(Manifest() with { SchemaVersion = 2 }), UpdateError.UnsupportedSchema);
            Throws(() => UpdateManifestParser.Validate(Manifest() with { Version = "1.3.8-test" }));
            Throws(() => UpdateManifestParser.Validate(Manifest() with { ReleaseNotes = null! }));
        });
        Add("Manifest URL, SHA256 and size boundaries", () =>
        {
            foreach (var s in new[] { "file:///c:/a", "http://example.com/a", "/relative", "https://u:p@example.com/a", "https://example.com/a#x", "\\\\server\\share" })
                Throws(() => UpdateManifestParser.ValidateUrl(s));
            UpdateManifestParser.ValidateUrl("http://127.0.0.1:1234/latest.json");
            foreach (var size in new[] { -1L, 0, UpdateManifestParser.MaxPackageSize + 1 }) Throws(() => UpdateManifestParser.Validate(Manifest() with { Package = Manifest().Package with { Size = size } }));
            Throws(() => UpdateManifestParser.Validate(Manifest() with { Package = Manifest().Package with { Sha256 = "z" + new string('0', 63) } }));
        });
        tests.Add(("Check available / equal / older / minimum / channel", async () =>
        {
            using var http = Client(_ => Response(JsonSerializer.Serialize(Manifest(), UpdateManifestParser.Json)));
            var service = new UpdateService(http);
            Assert((await service.CheckForUpdatesAsync(Url, "1.3.7", "stable", default)).Status == UpdateCheckStatus.Available);
            Assert((await service.CheckForUpdatesAsync(Url, "1.3.8", "stable", default)).Status == UpdateCheckStatus.UpToDate);
            Assert((await service.CheckForUpdatesAsync(Url, "1.4.0", "stable", default)).Status == UpdateCheckStatus.UpToDate);
            Assert((await service.CheckForUpdatesAsync(Url, "1.2.0", "stable", default)).Status == UpdateCheckStatus.ManualUpgradeRequired);
            await ThrowsAsync(() => service.CheckForUpdatesAsync(Url, "1.3.7", "beta", default), UpdateError.InvalidManifest);
        }));
        foreach (var status in new[] { 404, 500, 302 }) tests.Add(($"HTTP {status} typed failure", async () =>
        {
            using var f = new Fixture(); using var http = Client(_ => new((HttpStatusCode)status)); var service = new UpdateService(http);
            await ThrowsAsync(() => service.CheckForUpdatesAsync(Url, "1.3.7", "stable", default), UpdateError.Http);
            await ThrowsAsync(() => service.DownloadUpdateAsync(Manifest(), f.Job, null, default), UpdateError.Http);
            Assert(!File.Exists(Path.Combine(f.Job, "package.part")));
        }));
        tests.Add(("Check timeout and cancellation are distinct", async () =>
        {
            using var http = new HttpClient(new Handler(async (_, token) => { await Task.Delay(10000, token); return Response(""); }));
            var service = new UpdateService(http, TimeSpan.FromMilliseconds(30));
            await ThrowsAsync(() => service.CheckForUpdatesAsync(Url, "1.3.7", "stable", default), UpdateError.Timeout);
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await ThrowsAsync(() => service.CheckForUpdatesAsync(Url, "1.3.7", "stable", cancel.Token));
        }));
        tests.Add(("Streaming download and SHA256 success", async () =>
        {
            using var f = new Fixture(); var bytes = Encoding.UTF8.GetBytes("verified package");
            using var http = Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            var m = WithBytes(bytes); var service = new UpdateService(http); var progress = new CaptureProgress();
            var zip = await service.DownloadUpdateAsync(m, f.Job, progress, default);
            Assert(File.ReadAllBytes(zip).SequenceEqual(bytes)); Assert(progress.Verified);
            await service.VerifyPackageAsync(zip, m.Package, default);
        }));
        foreach (var mode in new[] { "hash", "short", "long", "header", "interrupt", "timeout", "cancel" })
            tests.Add(($"Download {mode} rejects and cleans partial", async () =>
            {
                using var f = new Fixture(); var bytes = new byte[1024]; var m = WithBytes(bytes);
                if (mode == "hash") m = m with { Package = m.Package with { Sha256 = new string('1', 64) } };
                using var cancel = new CancellationTokenSource();
                using var http = Client(_ =>
                {
                    HttpContent content = mode switch
                    {
                        "short" => new StreamContent(new MemoryStream(new byte[100])),
                        "long" => new StreamContent(new MemoryStream(new byte[2048])),
                        "header" => new ByteArrayContent(new byte[100]),
                        "interrupt" => new StreamContent(new BrokenStream(false)),
                        "timeout" or "cancel" => new StreamContent(new BrokenStream(true)),
                        _ => new ByteArrayContent(bytes)
                    };
                    if (mode is "short" or "long") content.Headers.ContentLength = null;
                    return new(HttpStatusCode.OK) { Content = content };
                });
                if (mode == "cancel") cancel.CancelAfter(30);
                await ThrowsAsync(() => new UpdateService(http, readTimeout: TimeSpan.FromMilliseconds(50)).DownloadUpdateAsync(m, f.Job, null, cancel.Token));
                Assert(!File.Exists(Path.Combine(f.Job, "package.part")) && !File.Exists(Path.Combine(f.Job, "package.zip")));
            }));
        Add("Relative, absolute, ADS, device and outside paths refused", () =>
        {
            using var f = new Fixture();
            foreach (var path in new[] { "../escape", "C:/escape", "/escape", "config/../../x", "config/a:stream", "CON", "a./x", "a//b", "\\\\host\\x" }) Throws(() => UpdatePaths.Under(f.Root, path));
            Throws(() => UpdatePayload.ValidateInstallation(f.Install, [f.Install]));
        });
        Add("Directory junction/reparse refused", () =>
        {
            using var f = new Fixture(); var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside);
            var junction = Path.Combine(f.Root, "junction");
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(junction); start.ArgumentList.Add(outside);
            using var p = Process.Start(start)!; p.WaitForExit(); Assert(p.ExitCode == 0);
            try { Throws(() => UpdatePaths.Under(junction, "file"), UpdateError.UnsafePath); } finally { Directory.Delete(junction); }
        });
        foreach (var bad in new[] { "../outside", "C:/outside", "unknown.txt", "duplicate", "symlink", "missing", "corrupt" })
            Add($"ZIP rejects {bad} before touching installation", () =>
            {
                using var f = new Fixture(); var zip = f.MakeZip(bad); var before = f.Snapshot();
                Throws(() => { using var l = f.Tx.AcquireLock(); f.Tx.Prepare(zip, "1.3.8-test", Guid.NewGuid().ToString("N")); });
                f.AssertSnapshot(before);
                using var lease = f.Tx.AcquireLock(); f.Tx.Recover();
            });
        Add("Prepare/apply/commit preserves arbitrary user data", () =>
        {
            using var f = new Fixture(); using var l = f.Tx.AcquireLock();
            f.Tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N")); f.Tx.Apply();
            Assert(FileVersionInfo.GetVersionInfo(Path.Combine(f.Install, "ZZZSwitch.exe")).ProductVersion!.StartsWith("1.3.8-test"));
            f.Tx.Commit(); Assert(!File.Exists(f.Tx.JournalPath)); Assert(File.ReadAllText(Path.Combine(f.Install, "user-data.txt")) == "preserve");
        });
        foreach (var phase in new[] { "Prepared", "Replaced:config/profiles/global.json", "Replaced:ZZZSwitch.exe", "AwaitingHealth" })
            Add($"Interrupted at {phase}: durable rollback is repeatable", () =>
            {
                using var f = new Fixture(); var snapshot = f.Snapshot(); using var l = f.Tx.AcquireLock();
                var tx = new UpdateTransaction(f.Install) { Checkpoint = p => { if (p == phase) throw new IOException("simulated crash"); } };
                Throws(() => { tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N")); tx.Apply(); });
                f.Tx.Recover(); f.Tx.Recover(); f.AssertSnapshot(snapshot);
            });
        Add("Backup creation failure preserves all installed files", () =>
        {
            using var f = new Fixture(); var snapshot = f.Snapshot(); using var l = f.Tx.AcquireLock();
            var tx = new UpdateTransaction(f.Install) { Checkpoint = p => { if (p.StartsWith("Backup:")) throw new UnauthorizedAccessException("backup denied"); } };
            Throws(() => tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N"))); f.AssertSnapshot(snapshot); f.Tx.Recover();
        });
        Add("Locked destination and missing staged source refuse apply", () =>
        {
            using var f = new Fixture(); var snapshot = f.Snapshot(); using var l = f.Tx.AcquireLock();
            f.Tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N"));
            using (var locked = new FileStream(Path.Combine(f.Install, "ZZZSwitch.exe"), FileMode.Open, FileAccess.Read, FileShare.None)) Throws(f.Tx.Apply);
            File.Delete(Path.Combine(f.Tx.Root, "staging", "ZZZSwitch.exe")); Throws(f.Tx.Apply); f.Tx.Recover(); f.AssertSnapshot(snapshot);
        });
        Add("Missing archive never mutates installed program", () =>
        {
            using var f = new Fixture(); var before = f.Snapshot(); using var l = f.Tx.AcquireLock();
            Throws(() => f.Tx.Prepare(Path.Combine(f.Root, "absent.zip"), "1.3.8-test", Guid.NewGuid().ToString("N"))); f.Tx.Recover(); f.AssertSnapshot(before);
        });
        Add("Corrupt backup stops rollback and preserves evidence", () =>
        {
            using var f = new Fixture(); using var l = f.Tx.AcquireLock();
            f.Tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N")); f.Tx.Apply();
            File.WriteAllText(Path.Combine(f.Tx.Root, "backup", "ZZZSwitch.exe"), "damaged"); Throws(f.Tx.Recover); Assert(File.Exists(f.Tx.JournalPath)); Assert(File.Exists(Path.Combine(f.Install, "ZZZSwitch.exe")));
        });
        Add("Mid-replace file lock retains rollback journal and supports retry", () =>
        {
            using var f = new Fixture(); var before = f.Snapshot(); using var lease = f.Tx.AcquireLock();
            FileStream? locked = null;
            var tx = new UpdateTransaction(f.Install) { Checkpoint = phase =>
            {
                if (phase == "Replaced:ZZZSwitch.Updater.exe")
                    locked = new FileStream(Path.Combine(f.Install, "ZZZSwitch.exe"), FileMode.Open, FileAccess.Read, FileShare.None);
            } };
            try
            {
                tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N")); Throws(tx.Apply);
                Throws(f.Tx.Recover); Assert(File.Exists(f.Tx.JournalPath));
            }
            finally { locked?.Dispose(); }
            f.Tx.Recover(); f.AssertSnapshot(before);
        });
        Add("Committed cleanup interruption never rolls back new files", () =>
        {
            using var f = new Fixture(); using var lease = f.Tx.AcquireLock();
            var tx = new UpdateTransaction(f.Install) { Checkpoint = phase => { if (phase == "Committed") throw new IOException("power loss after commit"); } };
            tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N")); tx.Apply();
            var installed = f.Snapshot(); Throws(tx.Commit); Assert(f.Tx.Load().Phase == "Committed");
            f.Tx.Recover(); f.AssertSnapshot(installed);
        });
        Add("Corrupt/outside journal fails closed", () =>
        {
            using var f = new Fixture(); using var l = f.Tx.AcquireLock();
            f.Tx.Prepare(f.MakeZip(), "1.3.8-test", Guid.NewGuid().ToString("N"));
            var j = f.Tx.Load(); j.Files[0] = j.Files[0] with { Path = "../outside" }; UpdatePaths.WriteJson(f.Tx.JournalPath, j); Throws(f.Tx.Recover);
            File.WriteAllText(f.Tx.JournalPath, "{broken"); Throws(f.Tx.Recover);
        });
        Add("Update lock prevents concurrent installer", () =>
        { using var f = new Fixture(); using var l = f.Tx.AcquireLock(); Throws(() => f.Tx.AcquireLock()); });
        AddGitHubTests(tests);
        var failures = 0;
        foreach (var (name, test) in tests)
        { try { await test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + "\n" + ex); } }
        Console.WriteLine($"Update: {tests.Count - failures}/{tests.Count} passed");
        return failures == 0 ? 0 : 1;
    }

    private const string Url = "https://updates.example.test/latest.json";
    private static UpdateVersion V(string s) => UpdateVersion.Parse(s);
    private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
    private static void Throws(Action action, UpdateError? code = null)
    {
        try { action(); } catch (Exception ex) { if (code is not null) Assert(ex is UpdateException update && update.Error == code); return; }
        throw new Exception("Expected exception.");
    }
    private static async Task ThrowsAsync(Func<Task> action, UpdateError? code = null)
    {
        try { await action(); } catch (Exception ex) { if (code is not null) Assert(ex is UpdateException update && update.Error == code); return; }
        throw new Exception("Expected exception.");
    }
    private static UpdateManifest Manifest() => new() { SchemaVersion = 1, Version = "1.3.8", Channel = "stable", Mandatory = false, MinSupportedVersion = "1.3.0", PublishedAt = DateTimeOffset.UtcNow, ReleaseNotes = new() { ["zh-CN"] = "更新测试", ["en-US"] = "Update test" }, Package = new() { Url = "https://updates.example.test/package.zip", Size = 1024, Sha256 = new string('0', 64) } };
    private static UpdateManifest WithBytes(byte[] bytes) => Manifest() with { Package = Manifest().Package with { Size = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } };
    private static HttpResponseMessage Response(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> action) => new(new Handler((r, _) => Task.FromResult(action(r))));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private sealed class CaptureProgress : IProgress<UpdateDownloadProgress> { public bool Verified; public void Report(UpdateDownloadProgress value) => Verified |= value.Verifying; }
    private sealed class BrokenStream(bool hang) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (hang) await Task.Delay(10000, cancellationToken); throw new IOException("connection lost"); }
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ZZZSwitch.Update.Tests", Guid.NewGuid().ToString("N"));
        public string Install => Path.Combine(Root, "app");
        public string Job => Path.Combine(Root, "download");
        public UpdateTransaction Tx => new(Install);
        public Fixture()
        {
            foreach (var path in UpdatePayload.Required)
            { var file = UpdatePaths.Under(Install, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, "old:" + path); }
            File.WriteAllText(Path.Combine(Install, "user-data.txt"), "preserve");
        }
        public Dictionary<string, string> Snapshot() => Directory.GetFiles(Install, "*", SearchOption.AllDirectories).Where(p => !p.Contains(".zzzswitch-update")).ToDictionary(p => p, UpdatePaths.Hash);
        public void AssertSnapshot(Dictionary<string, string> snapshot) { foreach (var (path, hash) in snapshot) Assert(UpdatePaths.Hash(path) == hash); Assert(Snapshot().Count == snapshot.Count); }
        public string MakeZip(string? bad = null)
        {
            var file = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
            if (bad == "corrupt") { File.WriteAllText(file, "broken"); return file; }
            using var archive = ZipFile.Open(file, ZipArchiveMode.Create);
            foreach (var path in UpdatePayload.Required)
            {
                if (bad == "missing" && path == "ZZZSwitch.exe") continue;
                var e = archive.CreateEntry(path); using var stream = e.Open();
                if (path.EndsWith(".exe")) { using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "ZZZSwitch.Update.Tests.exe")); source.CopyTo(stream); }
                else stream.Write(Encoding.UTF8.GetBytes("{\"new\":true}"));
            }
            if (bad is not null and not "missing")
            {
                var entry = archive.CreateEntry(bad == "duplicate" ? "ZZZSWITCH.EXE" : bad == "symlink" ? "README.md" : bad);
                if (bad == "symlink") entry.ExternalAttributes = 0xA000 << 16;
                using var s = entry.Open(); s.WriteByte(65);
            }
            return file;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
