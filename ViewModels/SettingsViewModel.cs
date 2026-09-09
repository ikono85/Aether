using CommunityToolkit.Mvvm.ComponentModel;

namespace Aether.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private bool _liveTelemetry = true;
    [ObservableProperty] private bool _particleEffects = true;
    [ObservableProperty] private bool _aiInsights = true;
    [ObservableProperty] private int _refreshRate = 1;
    public string Version => "AETHER OS · v1.0.0 · build 2026.08";
}
