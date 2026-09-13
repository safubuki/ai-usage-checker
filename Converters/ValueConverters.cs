using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AIUsageChecker.Converters;

public class RemainingToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush YellowBrush = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double pct)
        {
            if (pct <= 20.0) return RedBrush;
            if (pct <= 50.0) return YellowBrush;
            return GreenBrush;
        }
        return GreenBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromArgb(0x40, 0x22, 0xC5, 0x5E));
    private static readonly SolidColorBrush OrangeBrush = new(Color.FromArgb(0x40, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush RedBrush = new(Color.FromArgb(0x40, 0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush GrayBrush = new(Color.FromArgb(0x40, 0x6B, 0x72, 0x80));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            if (text.Contains("最新")) return GreenBrush;
            if (text.Contains("更新")) return OrangeBrush;
            if (text.Contains("未契約")) return RedBrush;
            if (text.Contains("ログイン") || text.Contains("認証")) return OrangeBrush;
            return GrayBrush;
        }
        return GrayBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class StatusBadgeTextBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush GreenText = new(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly SolidColorBrush OrangeText = new(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly SolidColorBrush RedText = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush GrayText = new(Color.FromRgb(0x9C, 0xA3, 0xAF));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            if (text.Contains("最新")) return GreenText;
            if (text.Contains("更新")) return OrangeText;
            if (text.Contains("未契約")) return RedText;
            if (text.Contains("ログイン") || text.Contains("認証")) return OrangeText;
            return GrayText;
        }
        return GrayText;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool flag && flag;
        if (parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            b = !b;
        }
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNotNull = value != null;
        if (parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            isNotNull = !isNotNull;
        }
        return isNotNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
