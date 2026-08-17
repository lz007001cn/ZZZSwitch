using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace ZZZSwitch;

public static class OverlayWindowDragBehavior
{
    public static bool CanStartDragFrom(DependencyObject? origin, Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        for (var current = origin;
             current is not null && !ReferenceEquals(current, window);
             current = GetParent(current))
        {
            if (IsInteractive(current))
            {
                return false;
            }
        }

        return origin is not null;
    }

    private static bool IsInteractive(DependencyObject element) =>
        element is ButtonBase or
            TextBoxBase or
            PasswordBox or
            Selector or
            RangeBase or
            ScrollViewer or
            Thumb or
            Hyperlink;

    private static DependencyObject? GetParent(DependencyObject element)
    {
        if (element is ContentElement contentElement)
        {
            return ContentOperations.GetParent(contentElement) ??
                   (contentElement as FrameworkContentElement)?.Parent;
        }

        if (element is FrameworkElement frameworkElement && frameworkElement.Parent is not null)
        {
            return frameworkElement.Parent;
        }

        try
        {
            return VisualTreeHelper.GetParent(element);
        }
        catch (InvalidOperationException)
        {
            return LogicalTreeHelper.GetParent(element);
        }
    }
}
