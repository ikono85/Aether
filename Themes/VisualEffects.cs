using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Aether.Themes;

/// <summary>
/// Point unique de pilotage des ombres et lueurs. Chaque effet de l'interface référence une
/// ressource dynamique ; désactiver les effets RETIRE ces ressources, ce qui supprime réellement
/// le coût de rendu (et pas seulement l'ombre des panneaux).
///
/// Retirer, et non affecter null : WPF traite une ressource valant null comme absente et
/// continue la recherche dans les dictionnaires fusionnés — l'effet resterait alors actif.
/// </summary>
public static class VisualEffects
{
    private static readonly string[] Keys =
    {
        "PanelShadow", "GlowAccentEffect", "GlowNodeEffect", "GlowLineEffect",
        "GlowBarEffect", "GlowButtonEffect", "CoreGlowEffect"
    };

    private static bool _enabled = true;
    private static Color _coreColor = Color.FromRgb(0x2B, 0xE8, 0xA4);

    public static void Apply(bool enabled)
    {
        _enabled = enabled;
        if (Application.Current?.Resources is not { } r) return;

        if (!enabled)
        {
            foreach (var key in Keys) r.Remove(key);
            return;
        }

        r["PanelShadow"] = Shadow(Colors.Black, 40, 0.55);
        r["GlowAccentEffect"] = Shadow(Resource(r, "AccentOptimalColor"), 60, 0.6);
        r["GlowNodeEffect"] = Shadow(Resource(r, "AccentAiColor"), 40, 0.8);
        r["GlowLineEffect"] = Shadow(Resource(r, "AccentOptimalColor"), 10, 0.8);
        r["GlowBarEffect"] = Shadow(Colors.White, 10, 0.25);
        r["GlowButtonEffect"] = Shadow(Colors.White, 24, 0.2);
        r["CoreGlowEffect"] = Shadow(_coreColor, 34, 1);
    }

    /// <summary>La lueur du noyau système suit la couleur d'état.</summary>
    public static void SetCoreColor(Color color)
    {
        _coreColor = color;
        if (_enabled && Application.Current?.Resources is { } r) r["CoreGlowEffect"] = Shadow(color, 34, 1);
    }

    private static Color Resource(ResourceDictionary r, string key) =>
        r[key] is Color c ? c : Colors.White;

    private static DropShadowEffect Shadow(Color color, double blur, double opacity)
    {
        var effect = new DropShadowEffect { Color = color, BlurRadius = blur, ShadowDepth = 0, Opacity = opacity };
        effect.Freeze();   // partagé par de nombreux éléments
        return effect;
    }
}
