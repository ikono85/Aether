namespace Aether.Models;

/// <summary>État global du système, pilote la couleur d'accent de toute l'interface.</summary>
public enum SystemState
{
    Optimal,   // vert
    Elevated,  // orange
    Critical,  // rouge
    Analyzing  // violet : aucune mesure encore disponible
}
