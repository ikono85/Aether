using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Aether.Views;

/// <summary>Choix, programme par programme, de ce que Startup Manager désactive.</summary>
public partial class StartupChoiceDialog : Window
{
    public sealed class Choice : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public string Name { get; init; } = "";

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public ObservableCollection<Choice> Choices { get; } = new();

    private StartupChoiceDialog()
    {
        InitializeComponent();
        DataContext = this;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    /// <summary>Programmes cochés, ou null si l'utilisateur annule.</summary>
    public static IReadOnlyList<string>? Ask(IReadOnlyList<string> entries, Window? owner)
    {
        var dialog = new StartupChoiceDialog { Owner = owner };
        foreach (var e in entries) dialog.Choices.Add(new Choice { Name = e });

        return dialog.ShowDialog() == true
            ? dialog.Choices.Where(c => c.IsSelected).Select(c => c.Name).ToList()
            : null;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var c in Choices) c.IsSelected = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var c in Choices) c.IsSelected = false; }
    private void OnConfirm(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
