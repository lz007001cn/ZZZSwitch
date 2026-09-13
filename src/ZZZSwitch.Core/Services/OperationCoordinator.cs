namespace ZZZSwitch.Core.Services;

public sealed class OperationCoordinator
{
    private readonly string? _lockFile;
    private readonly AppPaths? _paths;
    private int _active;

    public OperationCoordinator(AppPaths? paths = null) { _paths = paths; _lockFile = paths?.OperationLockFile; }

    public bool IsBusy => Volatile.Read(ref _active) != 0;
    public string? LastFailure { get; private set; }

    public bool TryBegin(out IDisposable? lease, bool allowPendingRecovery = false)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            lease = null;
            LastFailure = "当前窗口已有写操作正在进行。";
            return false;
        }

        CrossProcessFileLock? processLease = null;
        if (_lockFile is not null &&
            !CrossProcessFileLock.TryAcquire(_lockFile, out processLease, out var error))
        {
            Release();
            lease = null;
            LastFailure = error ?? "另一个 ZZZSwitch 进程正在执行写操作。";
            return false;
        }

        if (!allowPendingRecovery && _paths is not null &&
            (File.Exists(_paths.FileTransactionJournalFile) || File.Exists(_paths.HotUpdateJournalFile)))
        {
            processLease?.Dispose();
            Release();
            lease = null;
            LastFailure = "有未完成事务，请在检查中重试恢复。";
            return false;
        }
        LastFailure = null;
        lease = new OperationLease(this, processLease);
        return true;
    }

    private void Release() => Volatile.Write(ref _active, 0);

    private sealed class OperationLease : IDisposable
    {
        private OperationCoordinator? _owner;
        private CrossProcessFileLock? _processLease;

        public OperationLease(
            OperationCoordinator owner,
            CrossProcessFileLock? processLease)
        {
            _owner = owner;
            _processLease = processLease;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _processLease, null)?.Dispose();
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
