using System.Windows;

namespace Halo.Settings;

// WinUI's Button carries CornerRadius; WPF's does not, and the design needs three different radii on the
// same template (10 for a control, 14 for a home card, 16 for a flyout). An attached property is the
// substitute - a separate Style per radius would have meant three copies of the template to keep in step.
internal static class Ui
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(10)));

    public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);

    public static void SetRadius(DependencyObject d, CornerRadius value) => d.SetValue(RadiusProperty, value);
}
