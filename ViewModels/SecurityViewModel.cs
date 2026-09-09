using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Models;
using Aether.Services;

namespace Aether.ViewModels;

public partial class SecurityViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _processesChecked;
    [ObservableProperty] private int _processesTotal;
    [ObservableProperty] private string _stage = "En attente";
    [ObservableProperty] private int _threatLevel;
    [ObservableProperty] private string _resultText = "Aucune analyse effectuée";
    [ObservableProperty] private bool _hasScanned;

    /// <summary>Les vrais éléments signalés par la dernière analyse.</summary>
    public ObservableCollection<ScanItem> Items { get; } = new();

    public double SecurityScore => ThreatLevel switch { 2 => 35, 1 => 68, _ => 92 };

    public SecurityViewModel(MainViewModel main) => _main = main;

    [RelayCommand]
    private async Task Scan()
    {
        if (IsScanning) return;
        IsScanning = true;
        Progress = 0; ProcessesChecked = 0; ThreatLevel = 0; ResultText = "";
        Items.Clear();

        // 1) Collecte de la liste réelle (rapide) sur un thread de fond.
        Stage = "Inventaire du système";
        var units = await Task.Run(SecurityScanner.Collect);
        ProcessesTotal = units.Count;

        int worst = 0;
        var stages = new[] { "Processus actifs", "Signatures numériques", "Emplacements d'exécution", "Démarrage Windows" };

        // 2) Inspection unité par unité, avec progression fluide.
        for (int i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            var item = await Task.Run(() => SecurityScanner.Inspect(unit));
            if (item != null) { Items.Add(item); worst = System.Math.Max(worst, item.Risk); }

            ProcessesChecked = i + 1;
            Progress = (i + 1) / (double)units.Count * 100;
            Stage = stages[System.Math.Min(stages.Length - 1, (int)(Progress / 100 * stages.Length))];
        }

        // 3) Bilan réel.
        ThreatLevel = worst;
        int flagged = Items.Count;
        ResultText = worst switch
        {
            2 => $"{flagged} élément(s) suspect(s) détecté(s) — voir la liste ci-dessous. Aucune action automatique effectuée.",
            1 => $"{flagged} élément(s) à surveiller — aucun danger immédiat.",
            _ => $"Système sain — {ProcessesTotal} éléments analysés, rien à signaler."
        };
        Stage = "Terminé";
        HasScanned = true;
        IsScanning = false;
        OnPropertyChanged(nameof(SecurityScore));
    }

    /// <summary>Ouvre une recherche web sur l'élément (nom + « process ») pour l'identifier.</summary>
    [RelayCommand]
    private void SearchWeb(ScanItem? item)
    {
        if (item is null) return;
        var query = System.Uri.EscapeDataString($"{item.Name} process {System.IO.Path.GetFileName(item.Path)}");
        var url = $"https://www.google.com/search?q={query}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* navigateur indisponible */ }
    }

    public string ThreatLabel => ThreatLevel switch { 2 => "THREAT DETECTED", 1 => "ATTENTION", _ => "SAFE ZONE" };
    partial void OnThreatLevelChanged(int value) => OnPropertyChanged(nameof(ThreatLabel));
}
