using System.Diagnostics;

namespace ZZZSwitch.Update;

public sealed record UpdateRequest(string InstallRoot, int ParentPid, long ParentStartTime, UpdateManifest Manifest, string[] ProtectedRoots, bool HealthProbe = false);
public sealed record UpdateHealth(string Id, string Version, int Pid);

public static class UpdateHandoff
{
    public static string CreateJob(string updatesRoot)
    {
        CleanupCompletedJobs(updatesRoot);
        var directory = UpdatePaths.Under(updatesRoot, "jobs/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
    private static void CleanupCompletedJobs(string updatesRoot)
    {
        try
        {
            var jobs = UpdatePaths.Under(updatesRoot, "jobs");
            if (!Directory.Exists(jobs)) return;
            foreach (var job in Directory.EnumerateDirectories(jobs))
            {
                if (!Guid.TryParseExact(Path.GetFileName(job), "N", out _)) continue;
                try
                {
                    var result = UpdatePaths.Under(job, "result.json");
                    if (!File.Exists(result) || !UpdatePaths.ReadJson<System.Text.Json.JsonElement>(result).GetProperty("success").GetBoolean()) continue;
                    UpdatePaths.EnsureTree(job);
                    var helper = UpdatePaths.Under(job, "ZZZSwitch.Updater.exe");
                    if (File.Exists(helper)) { using var probe = new FileStream(helper, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                    UpdatePaths.DeleteTree(job);
                }
                catch (Exception ex) { UpdateLog.Write(updatesRoot, "Deferred job cleanup: " + ex.Message); }
            }
        }
        catch (Exception ex) { UpdateLog.Write(updatesRoot, "Deferred update cleanup: " + ex.Message); }
    }
    public static async Task<Process> StartAsync(string job, UpdateRequest request, CancellationToken token)
    {
        var helper = UpdatePaths.Under(job, "ZZZSwitch.Updater.exe");
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            ValidateRequest(job, request);
            var source = UpdatePaths.Under(request.InstallRoot, "ZZZSwitch.Updater.exe");
            File.Copy(source, helper, false);
            if (UpdatePaths.Hash(source) != UpdatePaths.Hash(helper)) throw new UpdateException(UpdateError.HashMismatch, "Updater copy could not be verified.");
            UpdatePaths.WriteJson(UpdatePaths.Under(job, "request.json"), request);
            token.ThrowIfCancellationRequested();
        }, token).ConfigureAwait(false);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job };
        start.ArgumentList.Add("--install"); start.ArgumentList.Add(job);
        var process = Process.Start(start) ?? throw new UpdateException(UpdateError.Installation, "Updater could not start.");
        var clock = Stopwatch.StartNew();
        try
        {
            while (!File.Exists(UpdatePaths.Under(job, "ready.json")))
            {
                token.ThrowIfCancellationRequested();
                if (process.HasExited || clock.Elapsed > TimeSpan.FromSeconds(20)) throw new UpdateException(UpdateError.Installation, "Updater did not acknowledge handoff. See Updates/update.log.");
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new UpdateException(UpdateError.Installation, "Updater exited during handoff.");
            return process;
        }
        catch
        {
            // No install can start until this parent exits. Explicit abort prevents a later surprise install.
            try
            {
                try { UpdatePaths.WriteJson(UpdatePaths.Under(job, "abort.json"), new { aborted = true }); }
                catch (Exception ex) { UpdateLog.Write(Path.GetDirectoryName(Path.GetDirectoryName(job))!, "Abort marker: " + ex.Message); }
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().ConfigureAwait(false); }
            }
            finally { process.Dispose(); }
            throw;
        }
    }
    public static void ValidateRequest(string job, UpdateRequest request)
    {
        if (!Guid.TryParseExact(Path.GetFileName(UpdatePaths.Normalize(job)), "N", out _) || request.ParentPid <= 0 || request.ParentStartTime <= 0 || request.ProtectedRoots is null)
            throw new UpdateException(UpdateError.Installation, "Invalid updater request.");
        UpdatePaths.EnsureOrdinary(job);
        UpdateManifestParser.Validate(request.Manifest);
        UpdatePayload.ValidateInstallation(request.InstallRoot, request.ProtectedRoots.Concat([job, UpdatePaths.DefaultRoot]));
        var current = FileVersionInfo.GetVersionInfo(UpdatePaths.Under(request.InstallRoot, "ZZZSwitch.exe"));
        if (current.ProductName != "ZZZSwitch" || UpdateVersion.ParseForUpdateCheck(current.ProductVersion, request.Manifest.Channel).CompareTo(UpdateVersion.Parse(request.Manifest.Version)) >= 0 ||
            UpdateVersion.Parse(current.ProductVersion).CompareTo(UpdateVersion.Parse(request.Manifest.MinSupportedVersion)) < 0)
            throw new UpdateException(UpdateError.Installation, "Installed application is not eligible for this update.");
    }
    public static Process? FindParent(int pid, long startTime, string? expectedPath)
    {
        try
        {
            var p = Process.GetProcessById(pid);
            if (p.StartTime.ToUniversalTime().Ticks != startTime || (expectedPath is not null && !UpdatePaths.Same(p.MainModule!.FileName, expectedPath)))
            { p.Dispose(); throw new UpdateException(UpdateError.Installation, "Parent process identity does not match."); }
            return p;
        }
        catch (ArgumentException) { return null; }
    }
    public static void AssertApplicationStopped(string root)
    {
        foreach (var p in Process.GetProcessesByName("ZZZSwitch"))
        {
            using (p)
            {
                try
                {
                    if (!p.HasExited && UpdatePaths.Same(p.MainModule!.FileName, UpdatePaths.Under(root, "ZZZSwitch.exe")))
                        throw new UpdateException(UpdateError.Installation, "An application process is still running; close it and retry recovery.");
                }
                catch (System.ComponentModel.Win32Exception) { throw new UpdateException(UpdateError.Installation, "Cannot verify application process state."); }
                catch (InvalidOperationException) { }
            }
        }
    }
    public static void StartRecovery(string installRoot)
    {
        var job = CreateJob(UpdatePaths.DefaultRoot);
        var helper = UpdatePaths.Under(job, "ZZZSwitch.Updater.exe");
        File.Copy(UpdatePaths.Under(installRoot, "ZZZSwitch.Updater.exe"), helper);
        using var self = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job };
        foreach (var arg in new[] { "--recover", installRoot, self.Id.ToString(), self.StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new UpdateException(UpdateError.Recovery, "Cannot start updater recovery.");
    }

    public static void ValidateStartup(string root, string id, string version)
    {
        var transaction = new UpdateTransaction(root);
        var j = transaction.Load();
        if (j.Id != id || j.Phase != "AwaitingHealth" || UpdateVersion.Parse(version).CompareTo(UpdateVersion.Parse(j.Version)) != 0)
            throw new UpdateException(UpdateError.Recovery, "Restart identity does not match the update transaction.");
    }
    // Normal app: after MainWindow construction, before game startup work. Probe: before any user data access.
    public static bool ConfirmStartup(string root, string id, string version)
    {
        ValidateStartup(root, id, version);
        var transaction = new UpdateTransaction(root);
        UpdatePaths.WriteJson(UpdatePaths.Under(transaction.Root, "health.json"), new UpdateHealth(id, version, Environment.ProcessId));
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (!File.Exists(transaction.JournalPath)) return true;
            string phase;
            try { phase = transaction.Load().Phase; }
            catch (FileNotFoundException) when (!File.Exists(transaction.JournalPath)) { return true; }
            if (phase == "Committed") return true;
            if (phase != "AwaitingHealth") return false;
            Thread.Sleep(100);
        }
        return false;
    }
}
