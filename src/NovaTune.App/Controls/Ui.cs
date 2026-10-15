using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using NovaTune.Core.Localization;

namespace NovaTune.App.Controls;

/// <summary>Присоединённые свойства для шаблонов: иконка кнопки, радиус и т.п.</summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static Geometry? GetIcon(DependencyObject d) => (Geometry?)d.GetValue(IconProperty);

    public static void SetIcon(DependencyObject d, Geometry? value) => d.SetValue(IconProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(10)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);

    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    /// <summary>Подсказка-заполнитель для TextBox.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);
}

/// <summary>
/// Текст на двух языках прямо в XAML: <c>Text="{c:Tr 'Обновить', 'Refresh'}"</c>.
/// Для свойств зависимостей возвращает привязку — текст меняется сразу при смене языка.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string ru, string en)
    {
        Ru = ru;
        En = en;
    }

    [ConstructorArgument("ru")]
    public string Ru { get; set; } = "";

    [ConstructorArgument("en")]
    public string En { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget { TargetObject: DependencyObject, TargetProperty: DependencyProperty })
        {
            var binding = new Binding(nameof(Loc.Language))
            {
                Source = Loc.Instance,
                Mode = BindingMode.OneWay,
                Converter = LanguageTextConverter.Instance,
                ConverterParameter = this,
            };
            return binding.ProvideValue(serviceProvider);
        }

        return Loc.T(Ru, En);
    }

    private sealed class LanguageTextConverter : IValueConverter
    {
        public static readonly LanguageTextConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tr = (TrExtension)parameter;
            return value is AppLanguage.En ? tr.En : tr.Ru;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
