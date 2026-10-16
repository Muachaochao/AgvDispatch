using System.Windows;
using System.Windows.Media.Animation;

namespace AgvDispatch.Wpf.Services;

public static class SidebarMotion
{
    public const double ExpandedWidth = 258;
    public const double CollapsedWidth = 84;
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.RegisterAttached(
        "IsExpanded", typeof(bool), typeof(SidebarMotion), new PropertyMetadata(true, OnChanged));
    public static bool GetIsExpanded(DependencyObject target) => (bool)target.GetValue(IsExpandedProperty);
    public static void SetIsExpanded(DependencyObject target, bool value) => target.SetValue(IsExpandedProperty, value);
    private static void OnChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element) return;
        var width = (bool)args.NewValue ? ExpandedWidth : CollapsedWidth;
        var from = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        element.SetValue(FrameworkElement.WidthProperty, width);
        if (!SystemParameters.ClientAreaAnimation || from <= 0)
        { element.BeginAnimation(FrameworkElement.WidthProperty, null); return; }
        element.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(from, width, TimeSpan.FromMilliseconds(240))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }, HandoffBehavior.SnapshotAndReplace);
    }
}
