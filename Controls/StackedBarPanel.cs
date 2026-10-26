using System;
using System.Windows;
using System.Windows.Controls;

namespace AIUsageChecker.Controls;

/// <summary>
/// 子要素を左から順に、全体幅に対する割合 (Percent: 0〜100) の幅で並べる積み上げ横棒グラフ用パネル。
/// 割合の合計が100未満の場合、残りは空き（未使用）として背景が見える。
/// </summary>
public class StackedBarPanel : Panel
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.RegisterAttached(
        "Percent", typeof(double), typeof(StackedBarPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetPercent(DependencyObject element) => (double)element.GetValue(PercentProperty);

    public static void SetPercent(DependencyObject element, double value) => element.SetValue(PercentProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        return new Size(width, double.IsInfinity(availableSize.Height) ? height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            double percent = Math.Clamp(GetPercent(child), 0.0, 100.0);
            double width = Math.Min(finalSize.Width * percent / 100.0, Math.Max(0, finalSize.Width - x));
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
