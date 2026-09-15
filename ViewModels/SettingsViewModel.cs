using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aether.Services;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;

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

    private readonly NetworkService _net;
    private readonly IDialogService _dialogs;
    private readonly ChangeHistoryService _history;
    private readonly UpdateService _updates;

    public SettingsViewModel(HardwareService hw, NetworkService net, IDialogService dialogs,
                             ChangeHistoryService history, UpdateService updates)
    {
        _hw = hw;
        _net = net;
        _dialogs = dialogs;
        _history = history;
        _updates = updates;
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
        _closeToTray = _store.CloseToTray;
        _alertsEnabled = _store.AlertsEnabled;
        _alertTemperature = _store.AlertTemperature;
        _alertTempC = _store.AlertTempC;
        _alertConnection = _store.AlertConnection;
        _alertNetwork = _store.AlertNetwork;
        _createRestorePoint = _store.CreateRestorePoint;
        _checkForUpdates = _store.CheckForUpdates;
        _loading = false;

        // Réapplique au démarrage ce que le fichier contenait.
        _hw.Interval = TimeSpan.FromMilliseconds(_sampleIntervalMs);
        ApplyVisualEffects(_visualEffects);

        // « Afficher les animations dans Windows » peut changer pendant que l'application tourne.
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                OnPropertyChanged(nameof(AnimationsEnabled));
        };
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

    [RelayCommand]
    private void RefreshDiagnostics() => OnPropertyChanged(nameof(SensorStatus));

    // ---------------------------------------------------------------- Apparence

    [ObservableProperty] private bool _visualEffects;

    partial void OnVisualEffectsChanged(bool value)
    {
        ApplyVisualEffects(value);
        OnPropertyChanged(nameof(AnimationsEnabled));
        _store.VisualEffects = value;
        Persist();
    }

    /// <summary>
    /// Toutes les ombres et lueurs passent par des ressources dynamiques : les remplacer par
    /// null supprime le flou sur toute l'interface d'un coup — le poste de rendu le plus
    /// coûteux sur un GPU intégré.
    /// </summary>
    private static void ApplyVisualEffects(bool on) => Aether.Themes.VisualEffects.Apply(on);

    /// <summary>
    /// Animations continues (noyau, halo, flux réseau) : coupées avec les effets visuels, ou
    /// quand l'utilisateur a désactivé les animations dans les paramètres d'accessibilité de Windows.
    /// </summary>
    public bool AnimationsEnabled => VisualEffects && SystemParameters.ClientAreaAnimation;

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

    // ---------------------------------------------------------------- Zone de notification et alertes

    [ObservableProperty] private bool _closeToTray;
    partial void OnCloseToTrayChanged(bool value) { _store.CloseToTray = value; Persist(); }

    [ObservableProperty] private bool _alertsEnabled;
    partial void OnAlertsEnabledChanged(bool value) { _store.AlertsEnabled = value; Persist(); }

    [ObservableProperty] private bool _alertTemperature;
    partial void OnAlertTemperatureChanged(bool value) { _store.AlertTemperature = value; Persist(); }

    /// <summary>Seuil d'alerte CPU/GPU en °C.</summary>
    [ObservableProperty] private int _alertTempC;
    partial void OnAlertTempCChanged(int value) { _store.AlertTempC = value; Persist(); }

    [ObservableProperty] private bool _alertConnection;
    partial void OnAlertConnectionChanged(bool value) { _store.AlertConnection = value; Persist(); }

    [ObservableProperty] private bool _alertNetwork;
    partial void OnAlertNetworkChanged(bool value) { _store.AlertNetwork = value; Persist(); }

    // ---------------------------------------------------------------- Protection

    [ObservableProperty] private bool _createRestorePoint;
    partial void OnCreateRestorePointChanged(bool value) { _store.CreateRestorePoint = value; Persist(); }

    // ---------------------------------------------------------------- Accueil

    public bool OnboardingCompleted => _store.OnboardingCompleted;

    /// <summary>
    /// Premier lancement : explique ce qu'AETHER fait et envoie, et recueille les choix. « Plus tard »
    /// laisse l'écran réapparaître au prochain lancement.
    /// </summary>
    public void RunOnboardingIfNeeded()
    {
        if (_store.OnboardingCompleted) return;

        var choices = _dialogs.ShowOnboarding(
            new OnboardingChoices(CloseToTray, AlertsEnabled, CreateRestorePoint, CheckForUpdates));
        if (choices is null) return;

        CloseToTray = choices.CloseToTray;
        AlertsEnabled = choices.AlertsEnabled;
        CreateRestorePoint = choices.CreateRestorePoint;
        CheckForUpdates = choices.CheckForUpdates;

        _store.OnboardingCompleted = true;
        Persist();
    }

    // ---------------------------------------------------------------- Mises à jour

    [ObservableProperty] private bool _checkForUpdates;
    partial void OnCheckForUpdatesChanged(bool value) { _store.CheckForUpdates = value; Persist(); }

    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private string _updateUrl = "";

    public bool HasUpdate => UpdateUrl.Length > 0;
    partial void OnUpdateUrlChanged(string value) => OnPropertyChanged(nameof(HasUpdate));

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        UpdateStatus = "Vérification…";
        var (update, message) = await _updates.CheckAsync(CancellationToken.None);
        UpdateUrl = update?.Url ?? "";
        UpdateStatus = message;
    }

    /// <summary>Vérification automatique : seulement si activée, et au plus une fois par jour.</summary>
    public async Task<UpdateInfo?> CheckForUpdatesAutoAsync()
    {
        if (!CheckForUpdates || DateTime.UtcNow - _store.LastUpdateCheckUtc < TimeSpan.FromHours(20)) return null;

        var (update, message) = await _updates.CheckAsync(CancellationToken.None);
        _store.LastUpdateCheckUtc = DateTime.UtcNow;
        Persist();
        UpdateUrl = update?.Url ?? "";
        UpdateStatus = message;
        return update;
    }

    /// <summary>
    /// Ouvre la page de la version via l'Explorateur : lancé directement depuis AETHER (administrateur),
    /// le navigateur hériterait des droits administrateur.
    /// </summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (!UpdateUrl.StartsWith(UpdateService.AllowedPagePrefix, StringComparison.Ordinal)) return;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add(UpdateUrl);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex) { UpdateStatus = $"Ouverture impossible : {ex.Message}"; }
    }

    // ---------------------------------------------------------------- Diagnostic

    [ObservableProperty] private string _diagnosticStatus = "";

    public string BuildDiagnostic() => Diagnostics.BuildReport(_hw, _net);

    [RelayCommand]
    private void CopyDiagnostic()
    {
        try
        {
            Clipboard.SetText(BuildDiagnostic());
            DiagnosticStatus = "Rapport copié dans le presse-papiers (adresses publiques, nom du PC et de l'utilisateur masqués).";
        }
        catch (Exception ex) { DiagnosticStatus = $"Copie impossible : {ex.Message}"; }
    }

    [RelayCommand]
    private void ExportDiagnostic()
    {
        var path = _dialogs.AskSavePath("Exporter un rapport de diagnostic",
            $"aether-diagnostic-{DateTime.Now:yyyyMMdd-HHmm}.zip", "Archive ZIP|*.zip");
        if (path is null) return;

        try
        {
            Diagnostics.ExportZip(path, BuildDiagnostic(), _history.ExportJson());
            DiagnosticStatus = $"Rapport exporté : {path}. Rien n'est envoyé automatiquement.";
        }
        catch (Exception ex) { DiagnosticStatus = $"Export impossible : {ex.Message}"; }
    }

    /// <summary>La session précédente s'est mal terminée : propose le rapport, sans rien envoyer.</summary>
    public void OfferDiagnosticAfterCrash(string issue)
    {
        var nl = Environment.NewLine;
        if (_dialogs.Confirm("AETHER — fermeture inattendue",
                $"{issue}{nl}{nl}Copier le rapport de diagnostic dans le presse-papiers pour le joindre à un signalement ?" +
                $"{nl}Rien n'est envoyé automatiquement : vous choisissez à qui le transmettre."))
            CopyDiagnostic();

        Diagnostics.MarkCrashesSeen();
    }

    // ---------------------------------------------------------------- Données

    public string DataPath => AppSettings.FilePath;

    [RelayCommand]
    private void OpenDataFolder()
    {
        try { AppSettings.OpenDataFolder(); }
        catch (Exception ex) { StartupStatus = $"Dossier inaccessible : {ex.Message}"; }
    }

    public string LogPath => Aether.Services.Infrastructure.Log.Folder;

    /// <summary>Journaux : ce qu'il faut joindre à un signalement de problème.</summary>
    [RelayCommand]
    private void OpenLogsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(LogPath);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogPath) { UseShellExecute = true });
        }
        catch (Exception ex) { StartupStatus = $"Dossier des journaux inaccessible : {ex.Message}"; }
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
        CloseToTray = d.CloseToTray;
        AlertsEnabled = d.AlertsEnabled;
        AlertTemperature = d.AlertTemperature;
        AlertTempC = d.AlertTempC;
        AlertConnection = d.AlertConnection;
        AlertNetwork = d.AlertNetwork;
        CreateRestorePoint = d.CreateRestorePoint;
        CheckForUpdates = d.CheckForUpdates;
        _loading = false;

        // Application effective des valeurs par défaut : les setters ont été court-circuités.
        _hw.Interval = TimeSpan.FromMilliseconds(d.SampleIntervalMs);
        ApplyVisualEffects(d.VisualEffects);
        OnPropertyChanged(nameof(AnimationsEnabled));
        AccentPolicyChanged?.Invoke();
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(ScaleLabel));
        OnPropertyChanged(nameof(IntervalLabel));

        d.StartWithWindows = LaunchAtStartup;
        d.OnboardingCompleted = _store.OnboardingCompleted;   // l'accueil a déjà été vu
        d.Save();
        StartupStatus = "Réglages réinitialisés. Le démarrage automatique est inchangé.";
    }

    /// <summary>Version réelle de l'assemblage (définie dans Aether.csproj), et non un texte figé.</summary>
    public string Version
    {
        get
        {
            var informational = typeof(SettingsViewModel).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion;
            var version = informational?.Split('+')[0] ?? "?";
            return $"AETHER · v{version} · .NET {Environment.Version.ToString(2)}";
        }
    }

    private void Persist()
    {
        if (!_loading) _store.Save();
    }
}
