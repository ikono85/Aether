using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Aether.Models;

namespace Aether;

/// <summary>Santé (0/1/2) -> pinceau vert/orange/rouge.</summary>
public class HealthToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        int h = value is int i ? i : 0;
        var key = h switch { 2 => "AccentDangerBrush", 1 => "AccentWarnBrush", _ => "AccentOptimalBrush" };
        return Application.Current.Resources[key];
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>État système -> pinceau d'accent.</summary>
public class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var key = value switch
        {
            SystemState.Critical => "AccentDangerBrush",
            SystemState.Elevated => "AccentWarnBrush",
            SystemState.Analyzing => "AccentAiBrush",
            _ => "AccentOptimalBrush"
        };
        return Application.Current.Resources[key];
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>bool -> bool inversé.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => !(value is bool b && b);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => !(v is bool b && b);
}

/// <summary>bool -> Visibility (avec paramètre "invert").</summary>
public class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool x && x;
        if (p as string == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Niveau d'impact -> pinceau du badge (vert / jaune / rouge).</summary>
public class ImpactToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        var key = value switch
        {
            ImpactLevel.Moderate => "AccentWarnBrush",
            ImpactLevel.NotRecommended => "AccentDangerBrush",
            _ => "AccentOptimalBrush"
        };
        return Application.Current.Resources[key];
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Niveau d'impact -> libellé court du badge.</summary>
public class ImpactToLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is ImpactLevel level ? Services.WindowsServices.WindowsServiceCatalog.ImpactLabel(level) : "";

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
