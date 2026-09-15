using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;

namespace Aether.ViewModels;

public sealed record OutageBar(double X, double Width);
public sealed record OutageRow(string Start, string Duration);

/// <summary>
/// Onglet Historique : tout ce qu'AETHER a modifié (restaurable élément par élément ou d'un coup)
/// et 24 h de mesures réseau (coupures, latence).
/// </summary>
public partial class HistoryViewModel : ObservableObject
{
    public const double ChartWidth = 720;
    public const double ChartHeight = 120;
    private const int Buckets = 288;          // tranches de 5 minutes
    private const double PingCapMs = 200;

    private readonly ChangeHistoryService _history;
    private readonly MeasurementHistory _measures;
    private readonly IDialogService _dialogs;

    public ObservableCollection<ChangeItem> Changes { get; } = new();
    public ObservableCollection<OutageRow> Outages { get; } = new();
    public ObservableCollection<OutageBar> OutageBars { get; } = new();

    [ObservableProperty] private PointCollection _pingPoints = new();
    [ObservableProperty] private string _availabilityText = "—";
    [ObservableProperty] private string _outageCountText = "0";
    [ObservableProperty] private string _pingAverageText = "—";
    [ObservableProperty] private string _pingMaxText = "—";
    [ObservableProperty] private string _coverageText = "—";
    [ObservableProperty] private string _status = "Chaque ligne indique ce qui a été modifié et permet de revenir à l'état d'origine.";
    [ObservableProperty] private bool _isBusy;

    public bool HasChanges => Changes.Count > 0;
    public bool HasOutages => Outages.Count > 0;

    public HistoryViewModel(ChangeHistoryService history, MeasurementHistory measures, IDialogService dialogs)
    {
        _history = history;
        _measures = measures;
        _dialogs = dialogs;

        _history.Changed += RefreshChanges;
        _measures.Updated += RefreshMeasures;

        RefreshChanges();
        RefreshMeasures();
    }

    // ------------------------------------------------------------------ Modifications

    private void RefreshChanges()
    {
        Changes.Clear();
        foreach (var item in _history.List()) Changes.Add(item);
        OnPropertyChanged(nameof(HasChanges));
        RestoreAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Refresh()
    {
        RefreshChanges();
        RefreshMeasures();
    }

    private bool CanRestore(ChangeItem? item) => !IsBusy && item is not null;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore(ChangeItem? item)
    {
        if (item is null) return;
        if (!_dialogs.Confirm("AETHER — restaurer", $"Revenir à l'état d'origine ?{Environment.NewLine}{Environment.NewLine}{item.Title}"))
            return;

        IsBusy = true;
        try
        {
            Status = $"Restauration : {item.Title}…";
            Status = await _history.RestoreAsync(item);
        }
        catch (Exception ex)
        {
            Log.Error($"Restauration de « {item.Title} » interrompue.", ex);
            Status = $"Erreur : {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshChanges();
        }
    }

    private bool CanRestoreAll() => !IsBusy && Changes.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRestoreAll))]
    private async Task RestoreAll()
    {
        if (!_dialogs.Confirm("AETHER — tout restaurer",
                $"Rétablir l'état d'origine des {Changes.Count} modification(s) en place ?{Environment.NewLine}" +
                "Un élément qui ne peut pas être restauré garde sa sauvegarde et reste dans la liste."))
            return;

        IsBusy = true;
        try
        {
            var (restored, failed) = await _history.RestoreAllAsync(message => Status = message);
            Status = failed == 0
                ? $"{restored} modification(s) restaurée(s) : Windows est revenu à l'état d'origine."
                : $"{restored} restaurée(s), {failed} en échec (sauvegardes conservées, détails dans le journal).";
        }
        finally
        {
            IsBusy = false;
            RefreshChanges();
        }
    }

    partial void OnIsBusyChanged(bool value)
    {
        RestoreCommand.NotifyCanExecuteChanged();
        RestoreAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ExportChanges()
    {
        var path = _dialogs.AskSavePath("Exporter l'historique des modifications",
            $"aether-modifications-{DateTime.Now:yyyyMMdd-HHmm}.json", "Fichier JSON|*.json");
        if (path is null) return;

        try
        {
            File.WriteAllText(path, _history.ExportJson());
            Status = $"Historique exporté : {path}";
        }
        catch (Exception ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    // ------------------------------------------------------------------ Mesures 24 h

    private void RefreshMeasures()
    {
        var timeline = _measures.Network;
        var samples = timeline.Samples;
        var end = DateTime.Now;
        var start = end - NetworkTimeline.Window;

        AvailabilityText = double.IsNaN(timeline.AvailabilityPercent) ? "—" : $"{timeline.AvailabilityPercent:0.##} %";
        PingAverageText = double.IsNaN(timeline.AveragePingMs) ? "—" : $"{timeline.AveragePingMs:0} ms";
        PingMaxText = double.IsNaN(timeline.MaxPingMs) ? "—" : $"{timeline.MaxPingMs:0} ms";
        CoverageText = samples.Count == 0
            ? "aucun relevé"
            : $"depuis {samples[0].Time:dd/MM HH:mm} · {samples.Count} relevés";

        // Latence moyenne par tranche de 5 minutes.
        var sums = new double[Buckets];
        var counts = new int[Buckets];
        foreach (var s in samples)
        {
            if (double.IsNaN(s.PingMs) || s.Time < start) continue;
            int i = Math.Clamp((int)((s.Time - start).TotalSeconds / NetworkTimeline.Window.TotalSeconds * Buckets), 0, Buckets - 1);
            sums[i] += s.PingMs;
            counts[i]++;
        }

        var points = new PointCollection();
        double step = ChartWidth / (Buckets - 1);
        for (int i = 0; i < Buckets; i++)
        {
            if (counts[i] == 0) continue;
            double avg = Math.Min(sums[i] / counts[i], PingCapMs);
            points.Add(new Point(i * step, ChartHeight - avg / PingCapMs * ChartHeight));
        }
        points.Freeze();
        PingPoints = points;

        var outages = timeline.Outages();
        OutageCountText = outages.Count.ToString();

        OutageBars.Clear();
        Outages.Clear();
        foreach (var o in outages.OrderByDescending(o => o.Start))
        {
            double x = Math.Clamp((o.Start - start).TotalSeconds / NetworkTimeline.Window.TotalSeconds * ChartWidth, 0, ChartWidth);
            double width = Math.Max(2, o.Duration.TotalSeconds / NetworkTimeline.Window.TotalSeconds * ChartWidth);
            OutageBars.Add(new OutageBar(x, width));
            Outages.Add(new OutageRow(o.Start.ToString("dd/MM HH:mm:ss"), FormatDuration(o.Duration)));
        }
        OnPropertyChanged(nameof(HasOutages));
    }

    [RelayCommand]
    private void ExportMeasures()
    {
        var path = _dialogs.AskSavePath("Exporter les mesures réseau (24 h)",
            $"aether-reseau-{DateTime.Now:yyyyMMdd-HHmm}.csv", "Fichier CSV|*.csv");
        if (path is null) return;

        try
        {
            _measures.ExportCsv(path);
            Status = $"Mesures exportées : {path}";
        }
        catch (Exception ex) { Status = $"Export impossible : {ex.Message}"; }
    }

    internal static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds < 5 ? "moins de 5 s"
        : d.TotalMinutes < 1 ? $"{d.Seconds} s"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s"
        : $"{(int)d.TotalHours} h {d.Minutes:00} min";
}
