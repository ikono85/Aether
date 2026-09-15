using Microsoft.Extensions.DependencyInjection;
using Aether.Services;
using Aether.Services.Dialogs;
using Aether.Services.History;
using Aether.Services.Infrastructure;
using Aether.Services.Optimization;
using Aether.Services.WindowsServices;
using Aether.ViewModels;
using Aether.Views;

namespace Aether;

/// <summary>
/// Assemblage de l'application : chaque service et ViewModel est déclaré une fois ici, au lieu
/// d'être instancié à la main dans les constructeurs. À construire sur le thread d'interface
/// (les services capturent son Dispatcher).
/// </summary>
public static class CompositionRoot
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        // Services système : une seule instance pour toute l'application.
        services.AddSingleton<NetworkService>();
        services.AddSingleton(sp => new HardwareService(sp.GetRequiredService<NetworkService>()));
        services.AddSingleton<ConnectionService>();
        services.AddSingleton<SpeedTestService>();
        services.AddSingleton<WindowsServiceManager>();
        services.AddSingleton<OptimizationEngine>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton(_ => new ServiceChangeLog(WindowsServiceCatalog.All.Select(s => s.ServiceName)));
        services.AddSingleton<ServiceRestorer>();
        services.AddSingleton<ChangeHistoryService>();
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<MeasurementHistory>();
        services.AddSingleton<UpdateService>();

        // ViewModels.
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<OptimizationViewModel>();
        services.AddSingleton<WindowsServicesOptimizationViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<MainViewModel>();

        // Lazy<T> : résolution différée, pour les onglets chargés à la première visite.
        services.AddTransient(typeof(Lazy<>), typeof(LazyService<>));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private sealed class LazyService<T>(IServiceProvider provider)
        : Lazy<T>(() => provider.GetRequiredService<T>()) where T : notnull;
}
