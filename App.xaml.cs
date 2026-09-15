using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Aether.Services.Infrastructure;
using Aether.ViewModels;

namespace Aether;

public partial class App : Application
{
    /// <summary>
    /// Instance unique, toutes sessions confondues : deux AETHER chargeraient deux fois le pilote
    /// capteurs et écriraient le même journal de restauration (%ProgramData%) en parallèle.
    /// La ligne de commande respecte la même règle.
    /// </summary>
    private const string MutexName = @"Global\Aether.SingleInstance";
    private const string ActivateEventName = @"Global\Aether.Activate";

    private ServiceProvider? _services;
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activateWait;
    private DateTime _lastErrorDialog = DateTime.MinValue;
    private bool _interactiveSession;

    /// <summary>Fin anormale de la session précédente, proposée à l'utilisateur une fois la fenêtre affichée.</summary>
    public static string? PreviousSessionIssue { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterExceptionHandlers();

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool firstInstance);

        var command = CommandLine.TryParse(e.Args);
        if (command is not null)
        {
            RunCommandLine(command, firstInstance);
            return;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        if (!firstInstance)
        {
            Log.Info("AETHER est déjà lancé : la fenêtre existante est rappelée.");
            try { _activate.Set(); } catch { }
            ReleaseMutex(owned: false);
            Shutdown();
            return;
        }

        Log.Info($"Démarrage d'AETHER (PID {Environment.ProcessId}, élevé : {Elevation.IsElevated}).");
        _interactiveSession = true;
        PreviousSessionIssue = Diagnostics.BeginSession();

        _activateWait = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToFront()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        try
        {
            _services = CompositionRoot.Build();
            var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Échec du démarrage.", ex);
            MessageBox.Show(
                $"AETHER n'a pas pu démarrer : {ex.GetBaseException().Message}{Environment.NewLine}{Environment.NewLine}" +
                $"Détails dans le journal : {Log.CurrentFile}",
                "AETHER — erreur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Mode sans interface (désinstallation, tests d'intégration). Voir <see cref="CommandLine"/>.</summary>
    private void RunCommandLine(CliCommand command, bool firstInstance)
    {
        int code;
        if (!firstInstance)
        {
            Log.Warn($"{command.Verb} refusé : AETHER est ouvert. Fermez-le (icône de notification → Quitter) puis réessayez.");
            code = CommandLine.AlreadyRunning;
            ReleaseMutex(owned: false);
        }
        else
        {
            try
            {
                using var services = CompositionRoot.Build();
                code = Task.Run(() => CommandLine.RunAsync(command, services)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Log.Error($"Ligne de commande {command.Verb} interrompue.", ex);
                code = CommandLine.Failure;
            }
        }

        Shutdown(code);
    }

    private void ReleaseMutex(bool owned)
    {
        if (_mutex == null) return;
        if (owned) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
        _mutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateWait?.Unregister(null);
        _activate?.Dispose();
        _services?.Dispose();
        ReleaseMutex(owned: true);
        if (_interactiveSession) Diagnostics.EndSession();
        Log.Info($"Arrêt d'AETHER (code {e.ApplicationExitCode}).");
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var exception = args.ExceptionObject as Exception;
            Log.Error("Exception fatale non gérée.", exception);
            Diagnostics.WriteCrash(exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Exception d'une tâche non observée.", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>
    /// Erreur sur le thread d'interface : journalisée, signalée, et l'application continue au lieu
    /// de disparaître sans explication. Une seule fenêtre toutes les 30 s : une erreur répétée
    /// par un timer ne doit pas empiler des dizaines de boîtes de dialogue.
    /// </summary>
    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Error("Exception non gérée sur le thread d'interface.", args.Exception);
        args.Handled = true;

        if (DateTime.UtcNow - _lastErrorDialog < TimeSpan.FromSeconds(30)) return;
        _lastErrorDialog = DateTime.UtcNow;

        MessageBox.Show(
            $"Une erreur inattendue s'est produite : {args.Exception.GetBaseException().Message}{Environment.NewLine}{Environment.NewLine}" +
            $"AETHER continue de fonctionner, mais un redémarrage de l'application est conseillé.{Environment.NewLine}" +
            "Paramètres → Diagnostic → « Copier le diagnostic » pour signaler le problème.",
            "AETHER — erreur", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
