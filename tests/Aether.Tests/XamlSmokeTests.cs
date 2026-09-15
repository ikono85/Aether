using System.Runtime.ExceptionServices;
using System.Windows;
using Aether.Models;
using Aether.Views;

namespace Aether.Tests;

/// <summary>
/// Les erreurs XAML (ressource introuvable, style invalide) ne sont détectées qu'à l'exécution :
/// ce test instancie chaque vue avec le thème réel, sur un thread STA, pour les attraper sans
/// lancer l'application (qui exige les droits administrateur).
/// </summary>
public class XamlSmokeTests
{
    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        error?.Throw();
    }

    [Fact]
    public void Every_view_loads_with_the_real_theme()
    {
        RunSta(() =>
        {
            var app = Application.Current ?? new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Aether;component/Themes/Theme.xaml", UriKind.Absolute)
            });

            _ = new DashboardView();
            _ = new NetworkView();
            _ = new OptimizationView();
            _ = new WindowsServicesOptimizationView();
            _ = new PerformanceView();
            _ = new SettingsView();
            _ = new HistoryView();
            _ = typeof(OnboardingWindow).GetMethod("Ask")!;   // fenêtres modales : chargées ci-dessous
            _ = Activator.CreateInstance(typeof(OnboardingWindow),
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
                new object[] { new Aether.Services.Dialogs.OnboardingChoices(true, true, true, false) }, null);
            _ = Activator.CreateInstance(typeof(StartupChoiceDialog), nonPublic: true);
            _ = new MainWindow();
            _ = ServiceConfirmationDialog.ForService(new WindowsServiceInfo { DisplayName = "Test" }, null);

            Aether.Themes.VisualEffects.Apply(false);
            Assert.Null(app.Resources["GlowBarEffect"]);
            Aether.Themes.VisualEffects.Apply(true);
            Assert.NotNull(app.Resources["GlowBarEffect"]);
        });
    }

    [Fact]
    public void Dependency_graph_is_valid()
    {
        // ValidateOnBuild vérifie que chaque service est résolvable, sans rien instancier.
        using var provider = CompositionRoot.Build();
        Assert.NotNull(provider);
    }
}
