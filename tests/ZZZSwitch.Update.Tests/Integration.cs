using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ZZZSwitch.Update;

namespace ZZZSwitch.Update.Tests;

internal static partial class Program
{
    private static async Task<int> IntegrationAsync(string[] args)
    {
        try
        {
            if (args[0] == "--handoff")
            {
                var root = args[1]; var endpoint = args[2]; var updates = args[3];
                var service = new UpdateService();
                var current = FileVersionInfo.GetVersionInfo(Path.Combine(root, "ZZZSwitch.exe")).ProductVersion!;
                var result = await service.CheckForUpdatesAsync(endpoint, current, "beta", default);
                Assert(result.Status == UpdateCheckStatus.Available);
                var job = UpdateHandoff.CreateJob(updates);
                await service.DownloadUpdateAsync(result.Manifest!, job, null, default);
                using var self = Process.GetCurrentProcess();
                using var helper = await UpdateHandoff.StartAsync(job,
                    new(root, self.Id, self.StartTime.ToUniversalTime().Ticks, result.Manifest!, [Path.Combine(Path.GetDirectoryName(root)!, "protected-data")], true), default);
                File.WriteAllText(Path.Combine(updates, "updater-pid.txt"), helper.Id.ToString());
                File.WriteAllText(Path.Combine(updates, "job.txt"), job);
                return 0;
            }
            if (args[0] != "--e2e" || args.Length != 4) return 1;
            var source = Path.GetFullPath(args[1]); var target = Path.GetFullPath(args[2]); var runRoot = Path.GetFullPath(args[3]);
            if (Directory.Exists(runRoot)) throw new Exception("Integration output must be a new directory.");
            Directory.CreateDirectory(runRoot);
            var protectedData = Path.Combine(runRoot, "protected-data"); Directory.CreateDirectory(protectedData);
            foreach (var name in new[] { "ui-settings.json", "state.json", "cache/block.bin", "Packages/archive.bin", "backup/old.bin", "logs/log.txt", "game/game.bin" })
            { var path = UpdatePaths.Under(protectedData, name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, Guid.NewGuid().ToString()); }
            var protectedHashes = Directory.GetFiles(protectedData, "*", SearchOption.AllDirectories).ToDictionary(p => p, UpdatePaths.Hash);
            var results = new List<object>();
            foreach (var failure in new[] { false, true })
            {
                var caseRoot = Path.Combine(runRoot, failure ? "startup-failure" : "success"); Directory.CreateDirectory(caseRoot);
                var app = Path.Combine(caseRoot, "app"); CopyTree(source, app);
                File.WriteAllText(Path.Combine(app, "user-untouched.txt"), "preserve");
                var before = Directory.GetFiles(app, "*", SearchOption.AllDirectories).ToDictionary(p => p, UpdatePaths.Hash);
                var payload = Path.Combine(caseRoot, "payload"); CopyTree(target, payload);
                // A valid product/version apphost without its runtime dependencies exercises real restart failure.
                if (failure) File.Copy(Path.Combine(AppContext.BaseDirectory, "ZZZSwitch.Update.Tests.exe"), Path.Combine(payload, "ZZZSwitch.exe"), true);
                var zip = Path.Combine(caseRoot, "package.zip"); ZipFile.CreateFromDirectory(payload, zip);
                var listenerProbe = new TcpListener(IPAddress.Loopback, 0); listenerProbe.Start();
                var port = ((IPEndPoint)listenerProbe.LocalEndpoint).Port; listenerProbe.Stop();
                var endpoint = $"http://127.0.0.1:{port}/";
                var manifest = Manifest() with { Version = "1.3.8-test", Channel = "beta", Package = new() { Url = endpoint + "package.zip", Size = new FileInfo(zip).Length, Sha256 = UpdatePaths.Hash(zip) } };
                var json = JsonSerializer.Serialize(manifest, UpdateManifestParser.Json);
                File.WriteAllText(Path.Combine(caseRoot, "latest.json"), json);
                using var server = new HttpListener(); server.Prefixes.Add(endpoint); server.Start();
                using var stop = new CancellationTokenSource();
                var serving = Task.Run(async () =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        HttpListenerContext context;
                        try { context = await server.GetContextAsync().WaitAsync(stop.Token); } catch (OperationCanceledException) { break; }
                        try
                        {
                            if (context.Request.RawUrl == "/latest.json")
                            { var bytes = System.Text.Encoding.UTF8.GetBytes(json); context.Response.ContentLength64 = bytes.Length; await context.Response.OutputStream.WriteAsync(bytes); }
                            else if (context.Request.RawUrl == "/package.zip")
                            { context.Response.ContentLength64 = new FileInfo(zip).Length; await using var stream = File.OpenRead(zip); await stream.CopyToAsync(context.Response.OutputStream); }
                            else context.Response.StatusCode = 404;
                        }
                        finally { context.Response.Close(); }
                    }
                });
                var updates = Path.Combine(caseRoot, "Updates");
                var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ZZZSwitch.Update.Tests.exe")) { UseShellExecute = false, CreateNoWindow = true };
                foreach (var arg in new[] { "--handoff", app, endpoint + "latest.json", updates }) start.ArgumentList.Add(arg);
                using var child = Process.Start(start)!;
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); Assert(child.ExitCode == 0);
                var updaterPid = int.Parse(File.ReadAllText(Path.Combine(updates, "updater-pid.txt")));
                int? exit = null;
                try { using var updater = Process.GetProcessById(updaterPid); await updater.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2)); exit = updater.ExitCode; }
                catch (ArgumentException) { }
                stop.Cancel(); await serving; server.Stop();
                if (failure)
                {
                    foreach (var (path, hash) in before) Assert(UpdatePaths.Hash(path) == hash);
                    Assert(!File.Exists(new UpdateTransaction(app).JournalPath));
                }
                else
                {
                    Assert(UpdatePaths.Hash(Path.Combine(app, "ZZZSwitch.exe")) == UpdatePaths.Hash(Path.Combine(target, "ZZZSwitch.exe")));
                    var job = File.ReadAllText(Path.Combine(updates, "job.txt"));
                    Assert(File.Exists(Path.Combine(job, "result.json"))); Assert(!File.Exists(Path.Combine(job, "package.zip")));
                    Assert(!File.Exists(new UpdateTransaction(app).JournalPath));
                }
                Assert(File.ReadAllText(Path.Combine(app, "user-untouched.txt")) == "preserve");
                foreach (var (path, hash) in protectedHashes) Assert(UpdatePaths.Hash(path) == hash);
                results.Add(new { scenario = failure ? "startup-failure-rollback" : "http-download-verify-updater-restart", passed = true, updaterExit = exit, oldVersion = "1.3.7", targetVersion = "1.3.8-test", protectedFiles = protectedHashes.Count });
                Console.WriteLine("PASS E2E " + (failure ? "restart failure rolled back" : "HTTP -> SHA256 -> updater -> real WPF runtime restart"));
            }
            UpdatePaths.WriteJson(Path.Combine(runRoot, "results.json"), results);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file).Replace('\\', '/');
            if (!UpdatePayload.Allowed.Contains(relative)) continue;
            var destination = UpdatePaths.Under(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination);
        }
    }
}
