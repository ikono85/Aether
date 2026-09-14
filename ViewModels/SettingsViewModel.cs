using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Services;

namespace Aether.ViewModels;

/// <summary>
/// Panneau de configuration. Chaque réglage agit réellement sur l'application : il est
/// appliqué immédiatement dans le setter correspondant, puis persisté dans
/// <see cref="AppSettings"/>. Aucun interrupteur décoratif.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly HardwareService _hw;
    private readonly AppSettings _store;

    /// <summary>Vrai pendant le chargement initial : évite d'écrire le fichier à chaque setter.</summary>
    private bool _loading;

    /// <summary>Prévient <see cref="MainViewModel"/> qu'un réglage influençant l'accent a changé.</summary>
    public event Action? AccentPolicyChanged;

    public SettingsViewModel(HardwareService hw)
    {
        _hw = hw;
        _store = AppSettings.Load();

        _loading = true;
        // L'état réel du démarrage automatique est la tâche planifiée, pas le fichier :
        // l'utilisateur peut l'avoir supprimée depuis le Planificateur de tâches.
        _launchAtStartup = StartupManager.IsEnabled();
        _startMinimized = _store.StartMinimized;
        _sampleIntervalMs = _store.SampleIntervalMs;
        _pauseWhenMinimized = _store.PauseWhenMinimized;
        _visualEffects = _store.VisualEffects;
        _dynamicAccent = _store.DynamicAccent;
        _uiScale = _store.UiScale;
        _thermalComfortC = _store.ThermalComfortC;
        _confirmRiskyActions = _store.ConfirmRiskyActions;
        _loading = false;

        // Réapplique au démarrage ce que le fichier contenait.
        _hw.Interval = TimeSpan.FromMilliseconds(_sampleIntervalMs);
        ApplyVisualEffects(_visualEffects);
    }

    // ---------------------------------------------------------------- Général

    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _startMinimized;

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_loading) return;
        bool actual = StartupManager.Set(value, out var message);
        StartupStatus = message;
        _store.StartWithWindows = actual;
        Persist();

        // Refus (droits insuffisants, schtasks en échec) : la case revient à l'état réel.
        if (actual != value)
        {
            _loading = true;
            LaunchAtStartup = actual;
            _loading = false;
        }
    }

    [ObservableProperty] private string _startupStatus = "";

    partial void OnStartMinimizedChanged(bool value) { _store.StartMinimized = value; Persist(); }

    // ---------------------------------------------------------------- Capteurs

    /// <summary>Période d'échantillonnage en millisecondes (250 ms à 5 s).</summary>
    [ObservableProperty] private int _sampleIntervalMs;

    partial void OnSampleIntervalMsChanged(int value)
    {
        _hw.Interval = TimeSpan.FromMilliseconds(value);
        _store.SampleIntervalMs = value;
        OnPropertyChanged(nameof(IntervalLabel));
        Persist();
    }

    public string IntervalLabel => SampleIntervalMs >= 1000
        ? $"Toutes les {SampleIntervalMs / 1000.0:0.#} s"
        : $"Toutes les {SampleIntervalMs} ms";

    [ObservableProperty] private bool _pauseWhenMinimized;

    partial void OnPauseWhenMinimizedChanged(bool value)
    {
        _store.PauseWhenMinimized = value;
        // Si on désactive la pause alors que la fenêtre est déjà réduite, on relance tout de suite.
        if (!value) _hw.Resume();
        Persist();
    }

    /// <summary>Diagnostic de la couche capteurs, tel que le rapporte LibreHardwareMonitor.</summary>
    public string SensorStatus => _hw.StatusMessage;

    public string ElevationStatus => StartupManager.IsElevated
        ? "Session élevée : températures et optimisations système disponibles."
        : "Session non élevée : températures CPU/carte mère et optimisations indisponibles.";

    public bool IsElevated => StartupManager.IsElevated;

    [RelayCommand]
    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(SensorStatus));
        OnPropertyChanged(nameof(ElevationStatus));
    }

    // ---------------------------------------------------------------- Apparence

    [ObservableProperty] private bool _visualEffects;

    partial void OnVisualEffectsChanged(bool value)
    {
        ApplyVisualEffects(value);
        _store.VisualEffects = value;
        Persist();
    }

    /// <summary>
    /// Les ombres des panneaux passent par la ressource dynamique « PanelShadow ».
    /// La remplacer par null supprime le flou sur toute l'interface d'un coup : c'est
    /// le poste de rendu le plus coûteux sur un GPU intégré.
    /// </summary>
    private static void ApplyVisualEffects(bool on)
    {
        if (Application.Current is not { } app) return;
        app.Resources["PanelShadow"] = on
            ? new DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black }
            : null;
    }

    [ObservableProperty] private bool _dynamicAccent;

    partial void OnDynamicAccentChanged(bool value)
    {
        _store.DynamicAccent = value;
        AccentPolicyChanged?.Invoke();
        Persist();
    }

    /// <summary>Échelle de l'interface en pourcent.</summary>
    [ObservableProperty] private int _uiScale;

    partial void OnUiScaleChanged(int value)
    {
        _store.UiScale = value;
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ScaleLabel));
        Persist();
    }

    /// <summary>Facteur lié au <c>ScaleTransform</c> de la fenêtre.</summary>
    public double Scale => Math.Clamp(UiScale, 80, 150) / 100.0;
    public string ScaleLabel => $"{UiScale} %";

    // ---------------------------------------------------------------- Seuils

    /// <summary>
    /// Température de confort : au-delà, le score de santé commence à se dégrader.
    /// Un refroidissement à air tient 65-70 °C sans être en difficulté, un watercooling
    /// descend plus bas — d'où le réglage.
    /// </summary>
    [ObservableProperty] private int _thermalComfortC;

    partial void OnThermalComfortCChanged(int value)
    {
        _store.ThermalComfortC = value;
        AccentPolicyChanged?.Invoke();
        Persist();
    }

    [ObservableProperty] private bool _confirmRiskyActions;

    partial void OnConfirmRiskyActionsChanged(bool value) { _store.ConfirmRiskyActions = value; Persist(); }

    // ---------------------------------------------------------------- Données

    public string DataPath => AppSettings.FilePath;

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { AppSettings.OpenDataFolder(); }
        catch (Exception ex) { StartupStatus = $"Dossier inaccessible : {ex.Message}"; }
    }

    [RelayCommand]
    private void ResetSettings()
    {
        AppSettings.Delete();
        var d = new AppSettings();

        _loading = true;
        StartMinimized = d.StartMinimized;
        SampleIntervalMs = d.SampleIntervalMs;
        PauseWhenMinimized = d.PauseWhenMinimized;
        VisualEffects = d.VisualEffects;
        DynamicAccent = d.DynamicAccent;
        UiScale = d.UiScale;
        ThermalComfortC = d.ThermalComfortC;
        ConfirmRiskyActions = d.ConfirmRiskyActions;
        _loading = false;

        // Application effective des valeurs par défaut : les setters ont été court-circuités.
        _hw.Interval = TimeSpan.FromMilliseconds(d.SampleIntervalMs);
        ApplyVisualEffects(d.VisualEffects);
        AccentPolicyChanged?.Invoke();
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ScaleLabel));
        OnPropertyChanged(nameof(IntervalLabel));

        d.StartWithWindows = LaunchAtStartup;
        d.Save();
        StartupStatus = "Réglages réinitialisés. Le démarrage automatique est inchangé.";
    }

    public string Version => "AETHER OS · v1.0.0 · build 2026.08";

    private void Persist()
    {
        if (!_loading) _store.Save();
    }
}
