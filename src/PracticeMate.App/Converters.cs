using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PracticeMate.App.ViewModels;

namespace PracticeMate.App;

/// <summary>enum ↔ int（TabControl.SelectedIndex 綁定頁面）。</summary>
public sealed class PageToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => (int)(Page)value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => (Page)(int)value;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b && !b;
}

/// <summary>null 或空字串 → Collapsed。</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0–1 比例 ↔ 百分比文字（設定頁的重疊比例）。</summary>
public sealed class FractionToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? (d * 100).ToString("0", culture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        double.TryParse(value as string, NumberStyles.Float, culture, out var p) ? p / 100 : DependencyProperty.UnsetValue;
}

public sealed class TranspositionNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Core.Music.Transposition.C => "C 調（長笛、雙簧管、長號…）",
        Core.Music.Transposition.BFlat => "B♭ 調（單簧管、小號、次中音薩克斯風…）",
        Core.Music.Transposition.EFlat => "E♭ 調（中音、上低音薩克斯風…）",
        Core.Music.Transposition.F => "F 調（法國號…）",
        _ => value?.ToString() ?? string.Empty,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
