namespace ZZZSwitch.Core.Services;

public interface IFileOperations
{
    bool FileExists(string path);
    long GetLength(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string target, bool overwrite);
    void DeleteFile(string path);
    void DeleteDirectory(string path, bool recursive);
    Stream OpenRead(string path);
    Stream OpenExclusive(string path);
}

public sealed class PhysicalFileOperations : IFileOperations
{
    public bool FileExists(string path) => File.Exists(path);
    public long GetLength(string path) => new FileInfo(path).Length;
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string target, bool overwrite) => File.Copy(source, target, overwrite);
    public void DeleteFile(string path) => File.Delete(path);
    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    public Stream OpenExclusive(string path) => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
}
