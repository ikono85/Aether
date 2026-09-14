using System.ComponentModel;
using Aether.Models;
using Aether.ViewModels;

namespace Aether.Services;

/// <summary>
/// Décide QUAND prévenir. Les mesures existent déjà (capteurs, maillons réseau, verdict) :
/// ce service n'en ajoute aucune, il transforme des états en notifications.
///
/// Toute la difficulté est de ne pas harceler : chaque alerte n'est émise qu'au passage
/// d'un seuil (pas à chaque relevé au-dessus), exige que la condition dure plusieurs
/// relevés pour écarter les pics isolés, et ne se réarme qu'après un retour franc à la
/// normale (hystérésis) ou un délai minimal.
/// </summary>
public sealed class AlertMonitor : IDisposable
{
    private readonly HardwareService _hw;
    private readonly NetworkService _net;
    private readonly SettingsViewModel _settings;
    private readonly TrayService _tray;

    /// <summary>Écart sous le seuil pour réarmer une alerte thermique : évite l'oscillation 84/85/84.</summary>
    private const double ThermalHysteresis = 5;

    /// <summary>Délai minimal entre deux alertes identiques, même si l'hystérésis a été franchie.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    /// <summary>Relevés réseau consécutifs requis (un relevé ≈ 5 s).</summary>
    private const int OutageCycles = 2;
    private const int VerdictCycles = 3;

    private readonly ThermalWatch _cpu = new("CPU");
    private readonly ThermalWatch _gpu = new("GPU");

    private int _downStreak, _upStreak;
    private bool _connectionDown;
    private DateTime _downSince;

    private int _badVerdictStreak, _goodVerdictStreak;
    private bool _verdictAlerted;
    private int _verdictSeverity;

    public AlertMonitor(HardwareService hw, NetworkService net, SettingsViewModel settings, TrayService tray)
    {
        _hw = hw;
        _net = net;
        _settings = settings;
        _tray = tray;

        _hw.Updated += OnHardware;
        _net.Updated += OnNetwork;
        _settings.PropertyChanged += OnSettingChanged;
        _tray.AlertsToggled += OnTrayToggled;
    }

    private bool Enabled => _settings.AlertsEnabled;

    // ------------------------------------------------------------------ Température

    private void OnHardware()
    {
        double threshold = _settings.AlertTempC;
        bool on = Enabled && _settings.AlertTemperature;

        Check(_cpu, _hw.Cpu.Temperature, threshold, on);
        Check(_gpu, _hw.Gpu.Temperature, threshold, on);

        UpdateTray();
    }

    private void Check(ThermalWatch w, double temp, double threshold, bool alertsOn)
    {
        if (double.IsNaN(temp)) return;

        w.Over = temp >= threshold;

        // Réarmement : il faut redescendre franchement, pas seulement repasser d'un degré.
        if (w.Alerted && temp <= threshold - ThermalHysteresis) w.Alerted = false;

        if (!w.Over || w.Alerted || !alertsOn) return;
        if (DateTime.UtcNow - w.LastAlert < Cooldown) return;

        w.Alerted = true;
        w.LastAlert = DateTime.UtcNow;
        _tray.Notify($"{w.Name} à {temp:0} °C",
            $"Le seuil d'alerte de {threshold:0} °C est dépassé. Vérifiez la ventilation et ce qui " +
            "sollicite la machine (onglet Performance).");
    }

    private sealed class ThermalWatch(string name)
    {
        public string Name { get; } = name;
        public bool Over { get; set; }
        public bool Alerted { get; set; }
        public DateTime LastAlert { get; set; } = DateTime.MinValue;
    }

    // ------------------------------------------------------------------ Réseau

    private void OnNetwork()
    {
        CheckOutage();
        CheckVerdict();
        UpdateTray();
    }

    /// <summary>
    /// Coupure = aucun des maillons de référence qui répondaient d'habitude ne répond plus.
    /// On ne regarde que les maillons ayant déjà répondu : un routeur qui filtre l'ICMP
    /// depuis toujours n'est pas « tombé ».
    /// </summary>
    private void CheckOutage()
    {
        var references = new[] { _net.Gateway, _net.Cdn, _net.Cloud }.Where(n => n.EverAnswered).ToList();
        if (references.Count == 0) return;

        bool down = references.All(n => !n.Reachable);

        if (down) { _downStreak++; _upStreak = 0; }
        else { _upStreak++; _downStreak = 0; }

        if (!_connectionDown && _downStreak >= OutageCycles)
        {
            _connectionDown = true;
            _downSince = DateTime.Now;

            if (Enabled && _settings.AlertConnection)
            {
                // La gateway distingue « box injoignable » de « box OK mais Internet coupé ».
                bool localDown = _net.Gateway.EverAnswered && !_net.Gateway.Reachable;
                _tray.Notify("Connexion perdue",
                    localDown
                        ? "La box ne répond plus : câble, Wi-Fi ou box redémarrée."
                        : "La box répond mais Internet est injoignable : incident côté opérateur probable.");
            }
        }
        else if (_connectionDown && _upStreak >= 1)
        {
            _connectionDown = false;
            var duration = DateTime.Now - _downSince;

            if (Enabled && _settings.AlertConnection)
                _tray.Notify("Connexion rétablie",
                    $"Coupure d'environ {FormatDuration(duration)}, débutée à {_downSince:HH:mm:ss}.",
                    warning: false);
        }
    }

    /// <summary>
    /// Verdict rouge persistant. Pendant une coupure, c'est l'alerte de coupure qui parle :
    /// le verdict dirait la même chose en moins précis.
    /// </summary>
    private void CheckVerdict()
    {
        var verdict = NetworkDiagnosis.Evaluate(_net);
        _verdictSeverity = verdict.Severity;

        if (verdict.Severity >= 2) { _badVerdictStreak++; _goodVerdictStreak = 0; }
        else { _goodVerdictStreak++; _badVerdictStreak = 0; }

        if (_verdictAlerted && _goodVerdictStreak >= VerdictCycles) _verdictAlerted = false;

        if (_verdictAlerted || _connectionDown || _badVerdictStreak < VerdictCycles) return;
        if (!Enabled || !_settings.AlertNetwork) return;

        _verdictAlerted = true;
        _tray.Notify(Capitalize(verdict.Title), verdict.Detail);
    }

    // ------------------------------------------------------------------ Icône

    private void UpdateTray()
    {
        int severity = _connectionDown || _cpu.Over || _gpu.Over ? 2 : _verdictSeverity;
        _tray.SetSeverity(severity);

        var parts = new List<string> { "AETHER" };
        if (!double.IsNaN(_hw.Cpu.Temperature)) parts.Add($"CPU {_hw.Cpu.Temperature:0}°C");
        if (!double.IsNaN(_hw.Gpu.Temperature)) parts.Add($"GPU {_hw.Gpu.Temperature:0}°C");
        parts.Add(_connectionDown ? "hors ligne" : _net.PingMs >= 0 ? $"{_net.PingMs:0} ms" : "réseau …");
        if (!Enabled) parts.Add("alertes coupées");
        _tray.SetTooltip(string.Join(" · ", parts));
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.AlertsEnabled))
        {
            _tray.SetAlertsChecked(_settings.AlertsEnabled);
            UpdateTray();
        }
    }

    private void OnTrayToggled(bool enabled) => _settings.AlertsEnabled = enabled;

    private static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds:00} s" : $"{d.Seconds} s";

    /// <summary>« LIEN LOCAL LENT » → « Lien local lent » : un titre de notification ne crie pas.</summary>
    private static string Capitalize(string title) =>
        title.Length == 0 ? title : char.ToUpper(title[0]) + title[1..].ToLowerInvariant();

    public void Dispose()
    {
        _hw.Updated -= OnHardware;
        _net.Updated -= OnNetwork;
        _settings.PropertyChanged -= OnSettingChanged;
        _tray.AlertsToggled -= OnTrayToggled;
    }
}
