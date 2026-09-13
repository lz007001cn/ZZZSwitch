namespace ZZZSwitch.Core.Services;

public sealed class SourceIntegrityException(string sourcePath) : IOException($"复制源文件完整性不匹配：{sourcePath}")
{
    public string SourcePath { get; } = sourcePath;
}
