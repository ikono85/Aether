namespace Aether.Models;

/// <summary>Un élément réel détecté par l'analyse (processus ou entrée de démarrage).</summary>
public class ScanItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Category { get; set; } = "";   // "Processus" | "Démarrage"
    public string Reason { get; set; } = "";      // pourquoi il est signalé
    public int Risk { get; set; }                 // 0 sain, 1 à surveiller, 2 suspect
}
