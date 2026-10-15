using System.Windows;
using System.Windows.Controls;

namespace NovaTune.App.Controls;

/// <summary>
/// Стеклянная панель: полупрозрачная заливка, светлая обводка, блик сверху и мягкая тень.
/// Тень рисуется отдельным слоем позади, поэтому эффект не размывает текст внутри панели.
/// Шаблон — в Themes/Controls.xaml.
/// </summary>
public sealed class GlassPanel : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(GlassPanel), new FrameworkPropertyMetadata(new CornerRadius(18)));

    public static readonly DependencyProperty HasShadowProperty = DependencyProperty.Register(
        nameof(HasShadow), typeof(bool), typeof(GlassPanel), new FrameworkPropertyMetadata(true));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public bool HasShadow
    {
        get => (bool)GetValue(HasShadowProperty);
        set => SetValue(HasShadowProperty, value);
    }
}
