using System.Windows;
using Aether.Models;
using Aether.Services.Dialogs;

namespace Aether.Views;

/// <summary>Implémentation WPF des dialogues : MessageBox, fenêtres d'AETHER, boîte d'enregistrement.</summary>
public sealed class WpfDialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow is { IsVisible: true } w ? w : null;

    public bool Confirm(string title, string message)
    {
        var result = Owner is { } owner
            ? MessageBox.Show(owner, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        return result == MessageBoxResult.OK;
    }

    public ServiceDialogResult ConfirmServiceDisable(WindowsServiceInfo service)
    {
        var dialog = ServiceConfirmationDialog.ForService(service, Owner);
        bool confirmed = dialog.ShowDialog() == true;
        return new ServiceDialogResult(confirmed, confirmed && dialog.DontAskAgain);
    }

    public bool ConfirmProfile(string profileName, string summary, IReadOnlyList<string> consequences, ImpactLevel worstLevel) =>
        ServiceConfirmationDialog.ForProfile(profileName, summary, consequences, worstLevel, Owner).ShowDialog() == true;

    public IReadOnlyList<string>? ChooseStartupEntries(IReadOnlyList<string> entries) =>
        StartupChoiceDialog.Ask(entries, Owner);

    public string? AskSavePath(string title, string defaultFileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = defaultFileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true
        };
        bool? ok = Owner is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return ok == true ? dialog.FileName : null;
    }

    public OnboardingChoices? ShowOnboarding(OnboardingChoices defaults) => OnboardingWindow.Ask(defaults, Owner);
}
