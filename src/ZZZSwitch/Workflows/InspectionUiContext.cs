using ZZZSwitch.Core.Models;
using ZZZSwitch.ViewModels;

namespace ZZZSwitch.Workflows;

public sealed record InspectionUiContext(
    MainWindowViewModel State,
    Action<InspectionReport?, bool> PublishReport,
    Action<bool, string> SetBusy,
    Func<string, string, string> Localize);
