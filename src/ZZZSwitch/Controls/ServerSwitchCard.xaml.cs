using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ZZZSwitch.Controls;

public partial class ServerSwitchCard : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ServerNameProperty = DependencyProperty.Register(
        nameof(ServerName),
        typeof(string),
        typeof(ServerSwitchCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(ServerSwitchCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.Register(
        nameof(IconSource),
        typeof(ImageSource),
        typeof(ServerSwitchCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(ServerSwitchCard),
        new PropertyMetadata(null));

    public ServerSwitchCard() => InitializeComponent();

    public string ServerName
    {
        get => (string)GetValue(ServerNameProperty);
        set => SetValue(ServerNameProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }
}
