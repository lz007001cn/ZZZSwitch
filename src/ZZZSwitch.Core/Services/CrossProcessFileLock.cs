using System.Text;

namespace ZZZSwitch.Core.Services;

internal sealed class CrossProcessFileLock : IDisposable
{
    private FileStream? _stream;

    private CrossProcessFileLock(FileStream stream) => _stream = stream;

    public static bool TryAcquire(
        string path,
        out CrossProcessFileLock? lease,
        out string? error)
    {
        lease = null;
        error = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            try
            {
                stream.SetLength(0);
                var metadata = Encoding.UTF8.GetBytes(
                    $"pid={Environment.ProcessId}{Environment.NewLine}" +
                    $"acquiredAt={DateTimeOffset.Now:O}{Environment.NewLine}");
                stream.Write(metadata);
                stream.Flush(true);
                lease = new CrossProcessFileLock(stream);
                return true;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = $"无法访问锁文件：{ex.Message}";
            return false;
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _stream, null)?.Dispose();
}

public sealed class ApplicationInstanceLock : IDisposable
{
    private CrossProcessFileLock? _lease;

    private ApplicationInstanceLock(CrossProcessFileLock lease) => _lease = lease;

    public static bool TryAcquire(
        AppPaths paths,
        out ApplicationInstanceLock? applicationLock,
        out string? error)
    {
        applicationLock = null;
        if (!CrossProcessFileLock.TryAcquire(paths.ApplicationLockFile, out var lease, out error))
        {
            return false;
        }

        applicationLock = new ApplicationInstanceLock(lease!);
        return true;
    }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
