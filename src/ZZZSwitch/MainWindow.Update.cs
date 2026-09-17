using System.Diagnostics;
using System.IO;
using System.Reflection;
using ZZZSwitch.Update;

namespace ZZZSwitch;

public partial class MainWindow
{
    private readonly IUpdateService _updates = new UpdateService();
    private readonly CancellationTokenSource _updateLifetime = new();
    private bool _checkingUpdate;
    private ApplicationUpdateModel? _applicationUpdate;
    internal bool IsUpdateSessionActive { get; private set; }
    private string UpdateRoot => Path.Combine(_paths.DataRoot, "Updates");
    private static string UpdateVersionText => typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    private ApplicationUpdateModel ApplicationUpdate => _applicationUpdate ??= new(_updates, new UpdateSettingsStore(_paths.DataRoot),
        UpdateVersionText, UpdateRoot, _localize, async (job, manifest, token) =>
        {
            using var self = Process.GetCurrentProcess();
            var protectedRoots = new[] { _paths.DataRoot, _paths.StorageRoot, _paths.BackupsRoot, _viewModel.GamePath,
                string.IsNullOrWhiteSpace(_viewModel.GamePath) ? "" : _cacheLocations.GetCacheRoot(_viewModel.GamePath) };
            var request = new UpdateRequest(AppContext.BaseDirectory, self.Id, self.StartTime.ToUniversalTime().Ticks, manifest, protectedRoots);
            using var updater = await UpdateHandoff.StartAsync(job, request, token);
        }, RunUpdateExclusiveAsync, () => ((App)System.Windows.Application.Current).ExitForUpdate());

    private async Task RunUpdateExclusiveAsync(Func<Task> operation)
    {
        if (_viewModel.IsBusy || IsUpdateSessionActive || !_operations.TryBegin(out var lease))
            throw new UpdateException(UpdateError.Installation, "Another operation is active. Retry after it completes.");
        using (lease)
        {
            IsUpdateSessionActive = true;
            SetBusy(true, _localize("软件更新", "Application update"));
            try { await operation(); }
            finally { IsUpdateSessionActive = false; SetBusy(false, _localize("更新操作结束", "Update operation finished")); }
        }
    }
    private async Task CheckApplicationUpdateLaterAsync()
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), _updateLifetime.Token);
            if (!new UpdateSettingsStore(_paths.DataRoot).Load().AutomaticCheck) return;
            if (ApplicationUpdate.Release is null)
                await ApplicationUpdate.CheckAsync(automatic: true, _updateLifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UpdateLog.Write(UpdateRoot, "Automatic check: " + ex); }
    }
    internal void StopUpdateChecks() { _updateLifetime.Cancel(); _applicationUpdate?.Cancel(); }
}
