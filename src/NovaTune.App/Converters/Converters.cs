using System.Globalization;
using System.Windows;
using System.Windows.Data;
using NovaTune.Core.Analysis;

namespace NovaTune.App.Converters;

/// <summary>bool → Visibility. Параметр «invert» инвертирует.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter as string == "invert")
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility.Visible;
}

/// <summary>null/пустая строка/0 → Collapsed. Параметр «invert» инвертирует.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i != 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        if (parameter as string == "invert")
        {
            has = !has;
        }

        return has ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Уровень критерия → кисть состояния из темы (Brush.Success/Warning/Error). Параметр «soft» — мягкий фон.</summary>
public sealed class LevelToBrushConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var soft = parameter as string == "soft" ? "Soft" : "";
        var key = value switch
        {
            CriterionLevel.Good => "Brush.Success",
            CriterionLevel.Warning => "Brush.Warning",
            CriterionLevel.Bad => "Brush.Error",
            _ => "Brush.Unavailable",
        } + soft;
        return Application.Current.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Уровень критерия → геометрия иконки.</summary>
public sealed class LevelToIconConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value switch
        {
            CriterionLevel.Good => "Icon.Check",
            CriterionLevel.Warning => "Icon.Warning",
            _ => "Icon.Error",
        });

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Процент 0–100 → ширина относительно родителя (для полос заполнения). values[0] — процент, values[1] — ширина.</summary>
public sealed class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double pct || values[1] is not double width || double.IsNaN(pct))
        {
            return 0.0;
        }

        return Math.Max(0, width * Math.Clamp(pct, 0, 100) / 100.0);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => [];
}

/// <summary>Инверсия bool (например, IsEnabled = !IsBusy).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Строковое значение равно параметру → Visible (для вкладок). Для IsChecked возвращает bool.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>Ширина всплывающего окна: как у блока-владельца, но не уже 340 px.</summary>
public sealed class PopupWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double w && !double.IsNaN(w) ? Math.Max(340, w) : 340.0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
