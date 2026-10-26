using System.Windows;

namespace Fouineur.App.Views;

public partial class InputDialog : Window
{
    private readonly bool _secret;

    private InputDialog(string title, string message, bool secret)
    {
        InitializeComponent();
        Title = title;
        Message.Text = message;
        _secret = secret;
        if (secret) { Input.Visibility = Visibility.Collapsed; Secret.Visibility = Visibility.Visible; }
        Loaded += (_, _) => { if (secret) Secret.Focus(); else Input.Focus(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Demande une valeur ; null si l'utilisateur annule.</summary>
    public static string? Ask(string title, string message, bool isSecret)
    {
        var d = new InputDialog(title, message, isSecret);
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        if (owner is not null && owner != d && owner.IsLoaded) d.Owner = owner;
        else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return d.ShowDialog() == true ? (isSecret ? d.Secret.Password : d.Input.Text) : null;
    }
}
