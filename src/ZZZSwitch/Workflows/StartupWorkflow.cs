using ZZZSwitch.Core.Models;

namespace ZZZSwitch.Workflows;

public sealed record StartupWorkflowResult(
    PendingRecoveryResult Recovery,
    string? StateWarning,
    bool BackupPruneAttempted,
    bool BackupPruneSucceeded);

public sealed class StartupWorkflow
{
    private readonly Func<PendingRecoveryResult> _recoverPending;
    private readonly Func<AppState?> _loadState;
    private readonly Action<string?> _pruneBackups;

    public StartupWorkflow(
        Func<PendingRecoveryResult> recoverPending,
        Func<AppState?> loadState,
        Action<string?> pruneBackups)
    {
        _recoverPending = recoverPending;
        _loadState = loadState;
        _pruneBackups = pruneBackups;
    }

    public async Task<StartupWorkflowResult> RunAsync(string? stateWarning)
    {
        var recovery = await Task.Run(_recoverPending);
        var pruneSucceeded = false;
        try
        {
            _pruneBackups(_loadState()?.LastBackupPath);
            pruneSucceeded = true;
        }
        catch
        {
            // Startup retention remains best effort and never blocks inspection.
        }

        return new(recovery, stateWarning, BackupPruneAttempted: true, pruneSucceeded);
    }
}
