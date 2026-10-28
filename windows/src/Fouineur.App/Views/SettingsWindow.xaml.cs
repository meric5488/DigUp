using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fouineur.App.ViewModels;

namespace Fouineur.App.Views;

public partial class SettingsWindow : Window
{
    /// <summary>
    /// Contenu factice affiché (en points) quand un secret est déjà enregistré : le vrai secret, chiffré DPAPI,
    /// n'est jamais relu dans l'interface. Tant que le champ contient ce texte, rien n'est modifié.
    /// </summary>
    private const string Stored = "⁣⁣⁣⁣⁣⁣⁣⁣⁣⁣⁣⁣";

    private SettingsViewModel? _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _vm = DataContext as SettingsViewModel;
            if (_vm is not null) _vm.PropertyChanged += Vm_PropertyChanged;
            RefreshSecretFields();
            if (_vm is { SelectedSource: { } s } && s.Roots.Count == 0 && (s.IsLocal || s.IsM365 && s.IsSignedIn || s.IsZimbra && s.HasPassword || s.IsGlpi && s.HasGlpiToken))
                _ = s.LoadRootsAsync();
        };
        Closed += (_, _) => { if (_vm is not null) _vm.PropertyChanged -= Vm_PropertyChanged; };
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSource)) RefreshSecretFields();
    }

    private SourceEditorViewModel? Source => _vm?.SelectedSource ?? (DataContext as SettingsViewModel)?.SelectedSource;

    /// <summary>Champs de secrets : remplis de points si un secret est enregistré, vides sinon.</summary>
    private void RefreshSecretFields()
    {
        var s = Source;
        Show(ZimbraPassword, s is { IsZimbra: true, HasPassword: true });
        Show(GlpiUserToken, s is { IsGlpi: true, HasGlpiToken: true });
        Show(GlpiAppToken, s is { IsGlpi: true, HasGlpiAppToken: true });
        Show(ClaudeKey, (DataContext as SettingsViewModel)?.HasClaudeKey == true);
    }

    private static void Show(PasswordBox box, bool stored) => box.Password = stored ? Stored : "";

    /// <summary>Nouvelle saisie dans le champ (vide ou inchangé = rien à enregistrer).</summary>
    private static string? Typed(PasswordBox box) =>
        box.Password.Length == 0 || box.Password == Stored ? null : box.Password.Replace("⁣", "");

    private void SelectAllOnFocus(object sender, RoutedEventArgs e)
    {
        // Taper dans un champ « enregistré » remplace le secret au lieu de s'ajouter aux points.
        if (sender is PasswordBox b && b.Password == Stored) b.SelectAll();
    }

    // ─── Zimbra ───

    private void PushZimbraPassword()
    {
        if (Typed(ZimbraPassword) is not { } pwd) return;
        Source?.SetPassword(pwd);
        Show(ZimbraPassword, true);
    }

    private void ZimbraPassword_LostFocus(object sender, RoutedEventArgs e) => PushZimbraPassword();

    private void TestZimbra_Click(object sender, RoutedEventArgs e)
    {
        var s = Source;
        if (s is null) return;
        PushZimbraPassword();
        // Les champs liés en LostFocus doivent être poussés avant la connexion.
        (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (s.TestZimbraCommand.CanExecute(null)) s.TestZimbraCommand.Execute(null);
    }

    // ─── GLPI ───

    private void PushGlpiTokens()
    {
        string? user = Typed(GlpiUserToken), app = Typed(GlpiAppToken);
        if (user is null && app is null) return;
        Source?.SetGlpiTokens(user, app);
        if (user is not null) Show(GlpiUserToken, true);
        if (app is not null) Show(GlpiAppToken, true);
    }

    private void GlpiTokens_LostFocus(object sender, RoutedEventArgs e) => PushGlpiTokens();

    private void ClearGlpiApp_Click(object sender, RoutedEventArgs e)
    {
        Source?.ClearGlpiAppToken();
        Show(GlpiAppToken, false);
    }

    private void TestGlpi_Click(object sender, RoutedEventArgs e)
    {
        var s = Source;
        if (s is null) return;
        PushGlpiTokens();
        (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (s.TestGlpiCommand.CanExecute(null)) s.TestGlpiCommand.Execute(null);
    }

    // ─── Claude ───

    private void PushClaudeKey()
    {
        if (Typed(ClaudeKey) is not { } key || DataContext is not SettingsViewModel vm) return;
        vm.SetClaudeKey(key);
        Show(ClaudeKey, true);
    }

    private void ClaudeKey_LostFocus(object sender, RoutedEventArgs e) => PushClaudeKey();

    private void TestClaude_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        PushClaudeKey();
        (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (vm.TestClaudeCommand.CanExecute(null)) vm.TestClaudeCommand.Execute(null);
    }

    private void RemoveClaudeKey_Click(object sender, RoutedEventArgs e) => Show(ClaudeKey, false);

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // Le bouton est « IsCancel » : la boîte de dialogue se ferme d'elle-même.
        PushClaudeKey();
        PushZimbraPassword();
        PushGlpiTokens();
    }
}
