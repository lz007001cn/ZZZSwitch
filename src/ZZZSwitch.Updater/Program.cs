using System.Diagnostics;
using System.Runtime.InteropServices;
using ZZZSwitch.Update;

return await UpdaterProgram.RunAsync(args);

internal static class UpdaterProgram
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);

    public static async Task<int> RunAsync(string[] args)
    {
        string? root = null;
        var logRoot = UpdatePaths.DefaultRoot;
        var probe = false;
        Process? launched = null;
        try
        {
            if (args.Length == 4 && args[0] == "--recover")
            {
                root = UpdatePaths.Normalize(args[1]);
                UpdatePayload.ValidateInstallation(root, [UpdatePaths.DefaultRoot]);
                using var parent = UpdateHandoff.FindParent(int.Parse(args[2]), long.Parse(args[3]), UpdatePaths.Under(root, "ZZZSwitch.exe"));
                if (parent is not null) await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
                var recovery = new UpdateTransaction(root);
                using (recovery.AcquireLock()) { UpdateHandoff.AssertApplicationStopped(root); recovery.Recover(); }
                using var restarted = StartApplication(root, null, false);
                return 0;
            }
            if (args.Length != 2 || args[0] != "--install") throw new InvalidDataException("Usage: ZZZSwitch.Updater --install <job>");
            var job = UpdatePaths.Normalize(args[1]);
            logRoot = Path.GetDirectoryName(Path.GetDirectoryName(job))!;
            var request = UpdatePaths.ReadJson<UpdateRequest>(UpdatePaths.Under(job, "request.json"));
            UpdateHandoff.ValidateRequest(job, request);
            root = request.InstallRoot; probe = request.HealthProbe;
            using var parentProcess = UpdateHandoff.FindParent(request.ParentPid, request.ParentStartTime,
                probe ? null : UpdatePaths.Under(root, "ZZZSwitch.exe"));
            if (parentProcess is null) throw new UpdateException(UpdateError.Installation, "Parent exited before handoff acknowledgement.");
            var tx = new UpdateTransaction(root);
            using var lease = tx.AcquireLock();
            if (File.Exists(tx.JournalPath)) throw new UpdateException(UpdateError.Recovery, "Recover the previous update first.");
            var package = UpdatePaths.Under(job, "package.zip");
            await new UpdateService().VerifyPackageAsync(package, request.Manifest.Package, CancellationToken.None);
            UpdatePaths.WriteJson(UpdatePaths.Under(job, "ready.json"), new { pid = Environment.ProcessId });
            await parentProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            if (File.Exists(UpdatePaths.Under(job, "abort.json"))) return 2;
            UpdateHandoff.AssertApplicationStopped(root);
            UpdateHandoff.ValidateRequest(job, request);
            await new UpdateService().VerifyPackageAsync(package, request.Manifest.Package, CancellationToken.None);
            try
            {
                tx.Prepare(package, request.Manifest.Version, Path.GetFileName(job));
                tx.Apply();
                launched = StartApplication(root, Path.GetFileName(job), probe);
                var clock = Stopwatch.StartNew();
                var health = UpdatePaths.Under(tx.Root, "health.json");
                while (true)
                {
                    if (File.Exists(health))
                    {
                        var ack = UpdatePaths.ReadJson<UpdateHealth>(health);
                        if (ack.Id != Path.GetFileName(job) || ack.Pid != launched.Id || UpdateVersion.Parse(ack.Version).CompareTo(UpdateVersion.Parse(request.Manifest.Version)) != 0)
                            throw new UpdateException(UpdateError.Installation, "Invalid restart confirmation.");
                        break;
                    }
                    if (launched.HasExited || clock.Elapsed > TimeSpan.FromSeconds(45)) throw new UpdateException(UpdateError.Installation, "New version did not confirm startup.");
                    await Task.Delay(100);
                }
                tx.Commit();
            }
            catch (Exception installError)
            {
                // A cleanup error after the durable commit must never kill the healthy new app or roll it back.
                if (File.Exists(tx.JournalPath) && tx.Load().Phase == "Committed")
                {
                    UpdateLog.Write(logRoot, "Committed; cleanup will retry on next launch: " + installError);
                }
                else
                {
                if (launched is not null && !launched.HasExited) { launched.Kill(); await launched.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                UpdateHandoff.AssertApplicationStopped(root);
                if (File.Exists(tx.JournalPath)) tx.Recover();
                throw;
                }
            }
            try { File.Delete(package); } catch (IOException ex) { UpdateLog.Write(logRoot, "Package cleanup: " + ex.Message); }
            UpdatePaths.WriteJson(UpdatePaths.Under(job, "result.json"), new { success = true, version = request.Manifest.Version });
            UpdateLog.Write(logRoot, "Update committed: " + request.Manifest.Version);
            return 0;
        }
        catch (Exception ex)
        {
            UpdateLog.Write(logRoot, ex.ToString());
            if (!probe) MessageBoxW(IntPtr.Zero,
                "更新未完成。旧程序备份与恢复记录将保留（若需要恢复）。请重新启动 ZZZSwitch 重试恢复；不要删除 .zzzswitch-update。\n\nUpdate did not complete. Restart ZZZSwitch to retry recovery; preserve .zzzswitch-update.\n\n" + ex.Message,
                "ZZZSwitch Updater", 0x10);
            return 1;
        }
        finally { launched?.Dispose(); }
    }
    private static Process StartApplication(string root, string? id, bool probe)
    {
        var start = new ProcessStartInfo(UpdatePaths.Under(root, "ZZZSwitch.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        if (id is not null) { start.ArgumentList.Add(probe ? "--update-health-probe" : "--update-session"); start.ArgumentList.Add(id); }
        return Process.Start(start) ?? throw new UpdateException(UpdateError.Installation, "Could not restart ZZZSwitch.");
    }
}
