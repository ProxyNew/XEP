using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace XEnchantmentPatcher;

internal static class UiTheme
{
    private static readonly ResourceDictionary Styles = new()
        { Source = new Uri("/XEnchantmentPatcher;component/UiStyles.xaml", UriKind.Relative) };
    public static bool IsDark { get; private set; }

    public static void Attach(Window window)
    {
        window.Resources.MergedDictionaries.Add(Styles);
        window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
    }

    public static void Apply(bool dark)
    {
        IsDark = dark;
        var colors = dark
            ? new[] { "#242424", "#303030", "#383838", "#F2F2F2", "#C5C5C5", "#737373", "#484848", "#595959", "#FFFFFF", "#B8B8B8", "#FFC0B8" }
            : new[] { "#FAFAFA", "#FFFFFF", "#F0F0F0", "#202020", "#555555", "#A0A0A0", "#E0E0E0", "#D2D2D2", "#222222", "#666666", "#972B20" };
        string[] keys = ["WindowBrush", "SurfaceBrush", "AlternateBrush", "TextBrush", "MutedBrush", "BorderBrush", "HoverBrush", "SelectionBrush", "FocusBrush", "DisabledBrush", "ErrorBrush"];
        for (var i = 0; i < keys.Length; i++)
            Styles[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
}

public sealed class BooleanSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? "✓" : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
