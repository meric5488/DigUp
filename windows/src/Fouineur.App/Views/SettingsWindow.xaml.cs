using System.Windows;
using System.Windows.Input;
using Fouineur.App.ViewModels;

namespace Fouineur.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is SettingsViewModel { SelectedSource: { } s } && s.Roots.Count == 0 && (s.IsLocal || s.IsM365 && s.IsSignedIn || s.IsZimbra && s.HasPassword))
                _ = s.LoadRootsAsync();
        };
    }

    private SourceEditorViewModel? Source => (DataContext as SettingsViewModel)?.SelectedSource;

    private void ZimbraPassword_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ZimbraPassword.Password.Length > 0) Source?.SetPassword(ZimbraPassword.Password);
    }

    private void TestZimbra_Click(object sender, RoutedEventArgs e)
    {
        var s = Source;
        if (s is null) return;
        if (ZimbraPassword.Password.Length > 0)
        {
            s.SetPassword(ZimbraPassword.Password);
            ZimbraPassword.Clear();
        }
        // Les champs liés en LostFocus doivent être poussés avant la connexion.
        (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (s.TestZimbraCommand.CanExecute(null)) s.TestZimbraCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Le bouton est « IsCancel » : la boîte de dialogue se ferme d'elle-même.
        if (ZimbraPassword.Password.Length > 0) Source?.SetPassword(ZimbraPassword.Password);
    }
}
