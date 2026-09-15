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

/// <summary>État système -> clé d'accent.</summary>
internal static class StateAccent
{
    public static string Key(object? state) => state switch
    {
        SystemState.Critical => "AccentDanger",
        SystemState.Elevated => "AccentWarn",
        SystemState.Analyzing => "AccentAi",
        _ => "AccentOptimal"
    };
}

/// <summary>État système -> pinceau d'accent (Fill, Stroke, Foreground…).</summary>
public class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        Application.Current.Resources[StateAccent.Key(value) + "Brush"];

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// État système -> couleur. Nécessaire pour les propriétés de type Color (GradientStop,
/// DropShadowEffect) : un pinceau y est refusé et la liaison échoue sans erreur visible.
/// </summary>
public class StateToColorConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        Application.Current.Resources[StateAccent.Key(value) + "Color"] is Color color ? color : Colors.Transparent;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>
/// Valeur == paramètre -> vrai. En retour, vrai renvoie le paramètre : un groupe de boutons
/// radio lié à une seule propriété (page active) reste synchronisé dans les deux sens,
/// y compris quand la page change par raccourci clavier.
/// </summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.Equals(value?.ToString(), p as string, StringComparison.Ordinal);

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        v is true ? p : Binding.DoNothing;
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
