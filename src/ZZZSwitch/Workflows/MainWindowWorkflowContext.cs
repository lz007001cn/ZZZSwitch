using ZZZSwitch.Core.Models;
using MediaBrush = System.Windows.Media.Brush;

namespace ZZZSwitch.Workflows;

public sealed record MainWindowWorkflowContext(
    Func<bool> IsBusy,
    Func<string> GetGamePath,
    Func<InspectionReport?> GetInspectionReport,
    Func<Task> RefreshInspection,
    Func<Task> RefreshInspectionWhileBusy,
    Action<bool, string> SetBusy,
    Action<string> SetBusyStatus,
    Action ShowOperationInProgress,
    Action<OperationProgress> ShowOperationProgress,
    Action<string, string, bool> ShowInlineSwitchResult,
    Func<string?, MediaBrush> ProfileBrush,
    Action<string, bool> OpenDirectory,
    Func<string, string, string> Localize);
