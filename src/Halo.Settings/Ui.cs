using System.Windows;

namespace Halo.Settings;

internal static class Ui
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(10)));

    public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject d, CornerRadius value) => d.SetValue(RadiusProperty, value);
}
