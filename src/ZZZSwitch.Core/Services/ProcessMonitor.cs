using System.Diagnostics;

namespace ZZZSwitch.Core.Services;

public interface IProcessMonitor
{
    IReadOnlyList<string> FindRelatedProcesses();
}

public sealed class ProcessMonitor : IProcessMonitor
{
    private static readonly string[] Names =
    [
        "ZenlessZoneZero",
        "launcher",
        "HYP",
        "HYUpdater",
        "PCGamePlatform",
        "game_security_protection",
        "ZZZSwitch"
    ];

    public IReadOnlyList<string> FindRelatedProcesses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId)
                    {
                        continue;
                    }

                    result.Add($"{process.ProcessName}.exe (PID {process.Id})");
                }
            }
        }

        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
