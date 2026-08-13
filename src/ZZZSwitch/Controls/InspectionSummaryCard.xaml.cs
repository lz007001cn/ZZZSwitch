using System.Windows;
using System.Windows.Input;

namespace ZZZSwitch.Controls;

public partial class InspectionSummaryCard : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty CacheManagementCommandProperty = DependencyProperty.Register(
        nameof(CacheManagementCommand),
        typeof(ICommand),
        typeof(InspectionSummaryCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty InitializeCacheCommandProperty = DependencyProperty.Register(
        nameof(InitializeCacheCommand),
        typeof(ICommand),
        typeof(InspectionSummaryCard),
        new PropertyMetadata(null));

    public InspectionSummaryCard() => InitializeComponent();

    public ICommand? CacheManagementCommand
    {
        get => (ICommand?)GetValue(CacheManagementCommandProperty);
        set => SetValue(CacheManagementCommandProperty, value);
    }

    public ICommand? InitializeCacheCommand
    {
        get => (ICommand?)GetValue(InitializeCacheCommandProperty);
        set => SetValue(InitializeCacheCommandProperty, value);
    }
}
