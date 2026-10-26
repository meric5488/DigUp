using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Fouineur.App.Infrastructure;

namespace Fouineur.App.Views;

/// <summary>Écran d'accueil affiché pendant l'ouverture de l'index et le chargement du modèle.</summary>
public partial class SplashWindow : Window
{
    private readonly Stopwatch _shown = Stopwatch.StartNew();
    public static readonly TimeSpan MinimumDisplay = TimeSpan.FromMilliseconds(1500);

    public SplashWindow()
    {
        InitializeComponent();
        var logo = Branding.Logo;
        if (logo is null) CompanyLogo.Visibility = Visibility.Collapsed;
        else CompanyLogo.Source = logo;
        Version.Text = $"{Branding.CompanyName} · version {typeof(SplashWindow).Assembly.GetName().Version?.ToString(3)}";
    }

    /// <summary>Met à jour le message et force l'affichage (le travail de démarrage tourne sur le fil de l'interface).</summary>
    public void Report(string message)
    {
        Status.Text = message;
        Dispatcher.Invoke(DispatcherPriority.Render, () => { });
    }

    /// <summary>Ferme l'écran d'accueil en respectant une durée minimale d'affichage.</summary>
    public async void CloseWhenReady()
    {
        var remaining = MinimumDisplay - _shown.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        Close();
    }
}
