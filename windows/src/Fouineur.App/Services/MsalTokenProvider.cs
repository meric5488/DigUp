using System.IO;
using System.Windows;
using System.Windows.Interop;
using Fouineur.Core.Connectors.M365;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Fouineur.App.Services;

/// <summary>
/// Jetons Graph délégués via MSAL et le gestionnaire de comptes Windows (WAM) :
/// connexion avec le compte professionnel déjà ouvert sur le poste, MFA géré par Windows.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    private readonly string _clientId;
    private readonly string _tenant;
    private readonly string _cacheDir;
    private readonly string _cacheName;
    private IPublicClientApplication? _app;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAccount? _account;

    public MsalTokenProvider(string clientId, string tenant, string cacheDir, string cacheName)
    {
        _clientId = clientId.Trim();
        _tenant = string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim();
        _cacheDir = cacheDir; _cacheName = cacheName;
    }

    public string? AccountName => _account?.Username;

    /// <summary>Si faux, aucune fenêtre de connexion ne s'ouvre (synchro en arrière-plan) : une erreur est levée à la place.</summary>
    public bool AllowInteractive { get; set; }

    private async Task<IPublicClientApplication> App()
    {
        if (_app is not null) return _app;
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException("Identifiant d'application (client ID) Entra non renseigné dans la source Microsoft 365.");
        var builder = PublicClientApplicationBuilder.Create(_clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, _tenant)
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(MainWindowHandle)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Fouineur" });
        var app = builder.Build();
        Directory.CreateDirectory(_cacheDir);
        var props = new StorageCreationPropertiesBuilder(_cacheName, _cacheDir).Build();
        var helper = await MsalCacheHelper.CreateAsync(props);
        helper.RegisterCache(app.UserTokenCache);
        return _app = app;
    }

    private static IntPtr MainWindowHandle()
    {
        IntPtr h = IntPtr.Zero;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var w = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? System.Windows.Application.Current.MainWindow;
            if (w is not null) h = new WindowInteropHelper(w).Handle;
        });
        return h;
    }

    public async Task<string> GetTokenAsync(bool interactive, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var app = await App();
            var account = _account ?? (await app.GetAccountsAsync()).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
            if (!interactive)
            {
                try
                {
                    var r = await app.AcquireTokenSilent(GraphClient.Scopes, account).ExecuteAsync(ct);
                    _account = r.Account;
                    return r.AccessToken;
                }
                catch (MsalUiRequiredException) when (AllowInteractive) { /* on passe à l'interactif */ }
                catch (MsalUiRequiredException e)
                {
                    throw new InvalidOperationException("Connexion Microsoft 365 requise : ouvrez Sources › Microsoft 365 › Se connecter.", e);
                }
            }
            if (!AllowInteractive)
                throw new InvalidOperationException("Connexion Microsoft 365 expirée : ouvrez Sources › Microsoft 365 › Se connecter.");

            var task = System.Windows.Application.Current.Dispatcher.Invoke(() =>
                app.AcquireTokenInteractive(GraphClient.Scopes)
                   .WithAccount(account == PublicClientApplication.OperatingSystemAccount ? null : account)
                   .WithPrompt(Prompt.SelectAccount)
                   .ExecuteAsync(ct));
            var res = await task;
            _account = res.Account;
            return res.AccessToken;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Connexion explicite (bouton « Se connecter »).</summary>
    public async Task<string> SignInAsync(CancellationToken ct)
    {
        AllowInteractive = true;
        try
        {
            try { await GetTokenAsync(false, ct); }
            catch (MsalException) { await GetTokenAsync(true, ct); }
            return AccountName ?? "";
        }
        finally { AllowInteractive = false; }
    }

    public async Task SignOutAsync()
    {
        if (_app is null) return;
        foreach (var a in await _app.GetAccountsAsync()) await _app.RemoveAsync(a);
        _account = null;
    }
}
