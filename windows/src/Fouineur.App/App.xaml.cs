using System.Windows;
using System.Windows.Threading;
using Fouineur.App.Infrastructure;
using Fouineur.App.Services;
using Fouineur.App.ViewModels;
using Fouineur.App.Views;

namespace Fouineur.App;

public partial class App : Application
{
    private static Mutex? _single;
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new Mutex(true, @"Local\Fouineur.Instance", out bool first);
        if (!first)
        {
            MessageBox.Show("Fouineur est déjà ouvert.", "Fouineur", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Thème Fluent de Windows 11 (clair/sombre selon le système), appliqué après le chargement des ressources de App.xaml.
        ThemeMode = ThemeMode.System;

        DispatcherUnhandledException += (_, a) => { ShowError(a.Exception); a.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Logger.Error("Erreur non gérée", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Logger.Error("Tâche en erreur", a.Exception); a.SetObserved(); };

        var splash = new SplashWindow();
        splash.Show();
        splash.Report("Ouverture de l'index…");

        try
        {
            Services = new AppServices();
        }
        catch (DllNotFoundException ex)
        {
            splash.Hide();
            MessageBox.Show($"Le moteur SQLite est introuvable : {ex.Message}", "Fouineur", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        splash.Report($"{Services.Store.Count():N0} éléments indexés — préparation de l'interface…");
        var vm = new MainViewModel(Services);
        var win = new MainWindow { DataContext = vm };
        MainWindow = win;
        win.ContentRendered += (_, _) => splash.CloseWhenReady();
        win.Show();

        if (Services.Settings.Sources.Count == 0)
            win.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => vm.OpenSettings(firstRun: true));
        Services.Sync.StartTimer();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services?.Settings.Save(Services.Paths.Settings);
            Services?.Dispose();
        }
        catch (Exception ex) { Logger.Error("Fermeture", ex); }
        _single?.Dispose();
        base.OnExit(e);
    }

    public static void ShowError(Exception e)
    {
        Logger.Error("Erreur", e);
        var msg = e is AggregateException ae ? ae.Flatten().InnerException?.Message ?? e.Message : e.Message;
        Current?.Dispatcher.Invoke(() =>
        {
            if (Current.MainWindow is { IsLoaded: true } owner)
                MessageBox.Show(owner, msg, "Fouineur", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                MessageBox.Show(msg, "Fouineur", MessageBoxButton.OK, MessageBoxImage.Warning);
        });
    }
}
