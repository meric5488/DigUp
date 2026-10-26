using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Fouineur.App.Infrastructure;
using Fouineur.App.Services;
using Fouineur.Core.Configuration;
using Fouineur.Core.Connectors;
using Fouineur.Core.Connectors.Local;
using Fouineur.Core.Connectors.M365;
using Fouineur.Core.Connectors.Zimbra;
using Fouineur.Core.Selection;

namespace Fouineur.App.ViewModels;

/// <summary>Nœud du sélecteur (case à cocher à trois états, chargement à la demande).</summary>
public sealed class NodeViewModel : ObservableObject
{
    private readonly SourceEditorViewModel _owner;
    private bool _loaded;
    public SourceNode Node { get; }
    public ObservableCollection<NodeViewModel> Children { get; } = new();
    public NodeViewModel? Parent { get; }

    public NodeViewModel(SourceEditorViewModel owner, SourceNode node, NodeViewModel? parent)
    {
        _owner = owner; Node = node; Parent = parent;
        if (node.HasChildren) Children.Add(Placeholder(owner));
    }

    private NodeViewModel(SourceEditorViewModel owner) { _owner = owner; Node = new SourceNode { Id = "…", Name = "Chargement…", HasChildren = false }; IsPlaceholder = true; }
    private static NodeViewModel Placeholder(SourceEditorViewModel o) => new(o);
    public bool IsPlaceholder { get; }

    public string Name => Node.Name;
    public string? Detail => Node.Detail;
    public bool Selectable => Node.Selectable && !IsPlaceholder;
    public string Glyph => Node.Icon switch
    {
        NodeIcon.Drive => "",
        NodeIcon.Site => "",
        NodeIcon.Team => "",
        NodeIcon.Channel => "",
        NodeIcon.Chat => "",
        NodeIcon.Mailbox => "",
        NodeIcon.MailFolder => "",
        NodeIcon.SharedFolder => "",
        NodeIcon.Server => "",
        NodeIcon.Root => "",
        _ => "",
    };

    public bool? IsChecked
    {
        get => IsPlaceholder ? false : _owner.Rules.GetState(Node.Ancestors, Node.Id) switch
        {
            CheckState.Checked => true,
            CheckState.Unchecked => false,
            _ => null,
        };
        set
        {
            if (IsPlaceholder) return;
            _owner.Rules.Set(Node.Ancestors, Node.Id, value == true, Node.Name);
            _owner.OnRulesChanged();
        }
    }

    public void RefreshCheck()
    {
        OnPropertyChanged(nameof(IsChecked));
        foreach (var c in Children) c.RefreshCheck();
    }

    private bool _expanded;
    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value) && value && !_loaded) _ = LoadAsync(); }
    }

    private bool _loading;
    public bool IsLoading { get => _loading; set => Set(ref _loading, value); }

    public async Task LoadAsync()
    {
        if (IsPlaceholder) return;
        _loaded = true;
        IsLoading = true;
        try
        {
            var kids = await _owner.Connector.GetChildrenAsync(Node, default);
            Children.Clear();
            foreach (var k in kids) Children.Add(new NodeViewModel(_owner, k, this));
        }
        catch (Exception e)
        {
            Children.Clear();
            Children.Add(new NodeViewModel(_owner, new SourceNode { Id = "!", Name = "Erreur : " + e.Message, HasChildren = false, Selectable = false }, this));
            _loaded = false;
            Logger.Warn($"Chargement de {Node.Name} : {e.Message}");
        }
        finally { IsLoading = false; }
    }
}

public sealed record RuleSummary(string Text, bool Include);

/// <summary>Éditeur d'une source (connexion + sélection de ce qu'il faut indexer).</summary>
public sealed class SourceEditorViewModel : ObservableObject
{
    private readonly AppServices _s;
    public SourceConfig Config { get; }
    public string InitialFingerprint { get; }
    public bool ConnectionChanged { get; private set; }

    public SourceEditorViewModel(AppServices s, SourceConfig cfg)
    {
        _s = s; Config = cfg;
        InitialFingerprint = cfg.Rules.Fingerprint();
        Paths = new ObservableCollection<string>(cfg.Paths);
        ExtraAccounts = new ObservableCollection<string>(cfg.ExtraAccounts);
        RefreshTreeCommand = new AsyncCommand(LoadRootsAsync);
        AddFolderCommand = new RelayCommand(AddFolder);
        AddPathCommand = new RelayCommand(() => AddPath(NewPath), () => !string.IsNullOrWhiteSpace(NewPath));
        RemovePathCommand = new RelayCommand(p => RemovePath(p as string));
        SignInCommand = new AsyncCommand(SignInAsync);
        SignOutCommand = new AsyncCommand(SignOutAsync);
        SearchSitesCommand = new AsyncCommand(SearchSitesAsync, () => !string.IsNullOrWhiteSpace(SiteQuery));
        TestZimbraCommand = new AsyncCommand(TestZimbraAsync);
        AddAccountCommand = new RelayCommand(AddAccount, () => NewAccount.Contains('@'));
        RemoveAccountCommand = new RelayCommand(p => RemoveAccount(p as string));
        SyncNowCommand = new RelayCommand(() => { _ = _s.Sync.SyncAllAsync(Config.Id); Status = "Synchronisation lancée."; });
        if (cfg.Type == SourceType.Microsoft365) _accountName = _s.TokenProvider(cfg).AccountName ?? cfg.AccountHint;
    }

    public SelectionRules Rules => Config.Rules;
    public ISourceConnector Connector => _s.Sync.GetConnector(Config);

    public bool IsLocal => Config.Type == SourceType.LocalFiles;
    public bool IsM365 => Config.Type == SourceType.Microsoft365;
    public bool IsZimbra => Config.Type == SourceType.Zimbra;
    public string TypeLabel => Config.Type switch
    {
        SourceType.Microsoft365 => "Microsoft 365 — OneDrive, SharePoint, Teams",
        SourceType.Zimbra => "Messagerie Zimbra",
        _ => "Dossiers locaux et partages réseau",
    };
    public string Glyph => Config.Type switch { SourceType.Microsoft365 => "", SourceType.Zimbra => "", _ => "" };

    public string Name { get => Config.Name; set { Config.Name = value; OnPropertyChanged(); } }
    public bool Enabled { get => Config.Enabled; set { Config.Enabled = value; OnPropertyChanged(); } }
    public string LastSyncText => Config.LastSync is { } d ? "Dernière synchronisation : " + MainViewModel.Relative(d) : "Jamais synchronisée";
    public string? LastError => Config.LastError;

    private string _status = "";
    public string Status { get => _status; set => Set(ref _status, value); }

    private void ConnectionEdited()
    {
        ConnectionChanged = true;
        _s.Sync.ResetConnector(Config.Id);
        Roots.Clear();
        TreeHint = "Paramètres modifiés : cliquez sur « Actualiser » pour recharger la liste.";
    }

    // ─── Arborescence ───

    public ObservableCollection<NodeViewModel> Roots { get; } = new();
    private bool _treeLoading;
    public bool IsTreeLoading { get => _treeLoading; set => Set(ref _treeLoading, value); }
    private string _treeHint = "";
    public string TreeHint { get => _treeHint; set => Set(ref _treeHint, value); }
    public ICommand RefreshTreeCommand { get; }

    public async Task LoadRootsAsync()
    {
        IsTreeLoading = true;
        TreeHint = "";
        try
        {
            Roots.Clear();
            var roots = await Connector.GetChildrenAsync(null, default);
            foreach (var r in roots) Roots.Add(new NodeViewModel(this, r, null));
            if (Roots.Count == 0) TreeHint = IsLocal ? "Ajoutez un dossier ou un partage ci-dessus." : "Rien à afficher.";
            else if (Roots.Count == 1) Roots[0].IsExpanded = true;
        }
        catch (Exception e)
        {
            TreeHint = "Impossible de charger la liste : " + e.Message;
            Logger.Warn($"{Config.Name} : {e}");
        }
        finally { IsTreeLoading = false; }
        OnRulesChanged();
    }

    public void OnRulesChanged()
    {
        foreach (var r in Roots) r.RefreshCheck();
        foreach (var r in SiteResults) r.RefreshCheck();
        OnPropertyChanged(nameof(RuleSummaries));
    }

    public IReadOnlyList<RuleSummary> RuleSummaries => Rules.Rules
        .OrderBy(r => r.Value.Ancestors.Count).ThenBy(r => r.Value.Label)
        .Select(r => new RuleSummary((r.Value.Include ? "Inclus : " : "Exclu : ") + (string.IsNullOrEmpty(r.Value.Label) ? r.Key : r.Value.Label), r.Value.Include))
        .ToList();

    // ─── Fichiers locaux ───

    public ObservableCollection<string> Paths { get; }
    private string _newPath = "";
    public string NewPath { get => _newPath; set => Set(ref _newPath, value); }
    public ICommand AddFolderCommand { get; }
    public ICommand AddPathCommand { get; }
    public ICommand RemovePathCommand { get; }

    public string ExcludePatterns
    {
        get => string.Join(Environment.NewLine, Config.ExcludePatterns);
        set => Config.ExcludePatterns = value.Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
    public int MaxFileSizeMb { get => Config.MaxFileSizeMb; set => Config.MaxFileSizeMb = Math.Clamp(value, 1, 2048); }

    private void AddFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier à indexer", Multiselect = true };
        if (dlg.ShowDialog() == true) foreach (var f in dlg.FolderNames) AddPath(f);
    }

    private void AddPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return;
        if (!Directory.Exists(path))
        {
            if (MessageBox.Show($"« {path} » est inaccessible pour l'instant. L'ajouter quand même ?", "Fouineur", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        ((LocalFilesConnector)Connector).AddRoot(path);
        Paths.Clear(); foreach (var p in Config.Paths) Paths.Add(p);
        NewPath = "";
        _ = LoadRootsAsync();
    }

    private void RemovePath(string? path)
    {
        if (path is null) return;
        ((LocalFilesConnector)Connector).RemoveRoot(path);
        Paths.Remove(path);
        _ = LoadRootsAsync();
    }

    // ─── Microsoft 365 ───

    public string ClientId { get => Config.ClientId; set { if (Config.ClientId == value) return; Config.ClientId = value.Trim(); OnPropertyChanged(); ConnectionEdited(); } }
    public string Tenant { get => Config.Tenant; set { if (Config.Tenant == value) return; Config.Tenant = value.Trim(); OnPropertyChanged(); ConnectionEdited(); } }
    public int ChatHistoryDays { get => Config.ChatHistoryDays; set => Config.ChatHistoryDays = Math.Clamp(value, 1, 3650); }
    public int ChannelHistoryDays { get => Config.ChannelHistoryDays; set => Config.ChannelHistoryDays = Math.Clamp(value, 1, 3650); }
    private string? _accountName;
    public string? AccountName { get => _accountName; set { if (Set(ref _accountName, value)) OnPropertyChanged(nameof(IsSignedIn)); } }
    public bool IsSignedIn => !string.IsNullOrEmpty(AccountName);
    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }

    private async Task SignInAsync()
    {
        Status = "Connexion…";
        try
        {
            AccountName = await _s.TokenProvider(Config).SignInAsync(default);
            Config.AccountHint = AccountName ?? "";
            Status = $"Connecté : {AccountName}";
            await LoadRootsAsync();
        }
        catch (Exception e)
        {
            Status = "Échec de la connexion : " + e.Message;
            Logger.Error("Connexion Microsoft 365", e);
        }
    }

    private async Task SignOutAsync()
    {
        await _s.TokenProvider(Config).SignOutAsync();
        AccountName = null;
        Config.AccountHint = "";
        Roots.Clear();
        Status = "Déconnecté.";
    }

    private string _siteQuery = "";
    public string SiteQuery { get => _siteQuery; set => Set(ref _siteQuery, value); }
    public ObservableCollection<NodeViewModel> SiteResults { get; } = new();
    public ICommand SearchSitesCommand { get; }

    private async Task SearchSitesAsync()
    {
        SiteResults.Clear();
        if (Connector is not M365Connector m) return;
        try
        {
            foreach (var n in await m.SearchSitesAsync(SiteQuery.Trim(), default)) SiteResults.Add(new NodeViewModel(this, n, null));
            if (SiteResults.Count == 0) Status = "Aucun site trouvé.";
        }
        catch (Exception e) { Status = "Recherche de sites : " + e.Message; }
    }

    // ─── Zimbra ───

    public string ServerUrl { get => Config.ServerUrl; set { if (Config.ServerUrl == value) return; Config.ServerUrl = value.Trim().TrimEnd('/'); OnPropertyChanged(); ConnectionEdited(); } }
    public string UserName { get => Config.UserName; set { if (Config.UserName == value) return; Config.UserName = value.Trim(); OnPropertyChanged(); ConnectionEdited(); } }
    public string WebmailTemplate { get => Config.WebmailTemplate; set => Config.WebmailTemplate = value; }
    public ObservableCollection<string> ExtraAccounts { get; }
    private string _newAccount = "";
    public string NewAccount { get => _newAccount; set => Set(ref _newAccount, value); }
    public ICommand AddAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand TestZimbraCommand { get; }
    public bool HasPassword => IsZimbra && _s.Secrets.Get($"zimbra:{Config.Id}:password") is not null;

    /// <summary>Appelé par la vue (PasswordBox n'est pas liable).</summary>
    public void SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return;
        _s.Secrets.Set($"zimbra:{Config.Id}:password", password);
        _s.Secrets.Set($"zimbra:{Config.Id}:token", null);
        ConnectionEdited();
        OnPropertyChanged(nameof(HasPassword));
    }

    private void AddAccount()
    {
        var a = NewAccount.Trim();
        if (!ExtraAccounts.Contains(a, StringComparer.OrdinalIgnoreCase))
        {
            ExtraAccounts.Add(a);
            Config.ExtraAccounts = ExtraAccounts.ToList();
            ApplyZimbraDefaults();
            ConnectionEdited();
        }
        NewAccount = "";
    }

    private void RemoveAccount(string? a)
    {
        if (a is null) return;
        ExtraAccounts.Remove(a);
        Config.ExtraAccounts = ExtraAccounts.ToList();
        Rules.Set(Array.Empty<string>(), ZimbraConnector.AccountNode(a), false);
        ConnectionEdited();
    }

    /// <summary>Boîte jamais configurée : tout sauf corbeille et indésirables.</summary>
    private void ApplyZimbraDefaults()
    {
        if (Connector is not ZimbraConnector z) return;
        foreach (var a in z.Accounts)
        {
            var node = ZimbraConnector.AccountNode(a);
            bool touched = Rules.Rules.ContainsKey(node) || Rules.Rules.Values.Any(r => r.Ancestors.Contains(node));
            if (!touched) z.ApplyDefaultSelection(a);
        }
        OnRulesChanged();
    }

    private async Task TestZimbraAsync()
    {
        Status = "Connexion au serveur…";
        try
        {
            if (Connector is not ZimbraConnector z) return;
            await z.Client.EnsureAuthAsync(default, force: true);
            ApplyZimbraDefaults();
            Status = "Connexion réussie.";
            await LoadRootsAsync();
        }
        catch (Exception e)
        {
            Status = "Échec : " + e.Message;
            Logger.Warn($"Zimbra : {e}");
        }
    }

    public ICommand SyncNowCommand { get; }
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly string _embeddingSnapshot;

    public SettingsViewModel(AppServices s, bool firstRun)
    {
        _s = s;
        FirstRun = firstRun;
        foreach (var c in s.Settings.Sources) Sources.Add(new SourceEditorViewModel(s, c));
        _selected = Sources.FirstOrDefault();
        _embeddingSnapshot = System.Text.Json.JsonSerializer.Serialize(s.Settings.Embedding);

        AddM365Command = new RelayCommand(() => Add(new SourceConfig
        {
            Type = SourceType.Microsoft365, Name = "Microsoft 365", ClientId = s.Settings.DefaultM365ClientId,
        }));
        AddZimbraCommand = new RelayCommand(() => Add(new SourceConfig { Type = SourceType.Zimbra, Name = "Zimbra" }));
        AddLocalCommand = new RelayCommand(() => Add(new SourceConfig { Type = SourceType.LocalFiles, Name = "Fichiers" }));
        RemoveSourceCommand = new RelayCommand(RemoveSelected, () => SelectedSource is not null);
        BrowseModelCommand = new RelayCommand(BrowseModel);
        OpenFolderCommand = new RelayCommand(p => { if (p is string dir) { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } });
        ResetVectorsCommand = new RelayCommand(() =>
        {
            if (MessageBox.Show("Recalculer la recherche par le sens pour tout l'index ? (le texte intégral n'est pas touché)", "Fouineur", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                _s.Store.ResetEmbeddings();
        });
    }

    public bool FirstRun { get; }
    public ObservableCollection<SourceEditorViewModel> Sources { get; } = new();
    private SourceEditorViewModel? _selected;
    public SourceEditorViewModel? SelectedSource
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value) || value is null) return;
            if (value.Roots.Count == 0 && !value.IsTreeLoading && (value.IsLocal || value.IsM365 && value.IsSignedIn || value.IsZimbra && value.HasPassword))
                _ = value.LoadRootsAsync();
        }
    }

    public ICommand AddM365Command { get; }
    public ICommand AddZimbraCommand { get; }
    public ICommand AddLocalCommand { get; }
    public ICommand RemoveSourceCommand { get; }
    public ICommand BrowseModelCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand ResetVectorsCommand { get; }

    private void Add(SourceConfig cfg)
    {
        _s.Settings.Sources.Add(cfg);
        var vm = new SourceEditorViewModel(_s, cfg);
        Sources.Add(vm);
        SelectedSource = vm;
    }

    private void RemoveSelected()
    {
        var vm = SelectedSource;
        if (vm is null) return;
        if (MessageBox.Show($"Supprimer la source « {vm.Name} » et retirer ses éléments de l'index ?", "Fouineur", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _s.Settings.Sources.Remove(vm.Config);
        _s.Sync.RemoveSource(vm.Config);
        _s.Secrets.Set($"zimbra:{vm.Config.Id}:password", null);
        _s.Secrets.Set($"zimbra:{vm.Config.Id}:token", null);
        Sources.Remove(vm);
        SelectedSource = Sources.FirstOrDefault();
    }

    // ─── Général ───

    public AppSettings Settings => _s.Settings;
    public int SyncIntervalMinutes { get => _s.Settings.SyncIntervalMinutes; set => _s.Settings.SyncIntervalMinutes = Math.Clamp(value, 5, 24 * 60); }
    public bool OpenOfficeInDesktopApp { get => _s.Settings.OpenOfficeInDesktopApp; set => _s.Settings.OpenOfficeInDesktopApp = value; }
    public string DefaultClientId { get => _s.Settings.DefaultM365ClientId; set => _s.Settings.DefaultM365ClientId = value.Trim(); }

    public string Provider
    {
        get => _s.Settings.Embedding.Provider;
        set { _s.Settings.Embedding.Provider = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsOnnx)); OnPropertyChanged(nameof(IsOllama)); }
    }
    public bool IsOnnx => Provider == "onnx";
    public bool IsOllama => Provider == "ollama";
    public string OnnxModelDir
    {
        get => string.IsNullOrWhiteSpace(_s.Settings.Embedding.OnnxModelDir) ? DefaultModelDir : _s.Settings.Embedding.OnnxModelDir;
        set { _s.Settings.Embedding.OnnxModelDir = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModelPresent)); }
    }
    public string DefaultModelDir => Path.Combine(_s.Paths.Models, "embeddinggemma");
    public bool ModelPresent => Directory.Exists(OnnxModelDir) && Directory.EnumerateFiles(OnnxModelDir, "*.onnx", SearchOption.AllDirectories).Any();
    public bool UseDirectML { get => _s.Settings.Embedding.UseDirectML; set => _s.Settings.Embedding.UseDirectML = value; }
    public string OllamaUrl { get => _s.Settings.Embedding.OllamaUrl; set => _s.Settings.Embedding.OllamaUrl = value; }
    public string OllamaModel { get => _s.Settings.Embedding.OllamaModel; set => _s.Settings.Embedding.OllamaModel = value; }
    public int Dimensions { get => _s.Settings.Embedding.Dimensions; set => _s.Settings.Embedding.Dimensions = value is 128 or 256 or 512 or 768 ? value : 256; }
    public int[] DimensionChoices { get; } = { 128, 256, 512, 768 };
    public string EmbedderStatus => _s.EmbedderStatus;
    public string IndexPath => _s.Paths.Index;
    public string RootPath => _s.Paths.Root;
    public string LogsPath => _s.Paths.Logs;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    private void BrowseModel()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Dossier du modèle EmbeddingGemma (ONNX)" };
        if (dlg.ShowDialog() == true) OnnxModelDir = dlg.FolderName;
    }

    /// <summary>Applique les changements à la fermeture : élagage immédiat, connecteurs, modèle, synchro.</summary>
    public void Commit()
    {
        var toSync = new List<string>();
        foreach (var vm in Sources)
        {
            if (vm.Config.Rules.Fingerprint() != vm.InitialFingerprint)
            {
                int n = _s.Sync.PruneSource(vm.Config);
                if (n > 0) Logger.Info($"[{vm.Name}] {n} éléments retirés de l'index (désélectionnés).");
                toSync.Add(vm.Config.Id);
            }
            else if (vm.ConnectionChanged) toSync.Add(vm.Config.Id);
            if (vm.Config.LastSync is null) toSync.Add(vm.Config.Id);
        }
        _s.Settings.Save(_s.Paths.Settings);
        if (System.Text.Json.JsonSerializer.Serialize(_s.Settings.Embedding) != _embeddingSnapshot) _s.ReloadEmbedder();
        _s.Sync.StartTimer();
        if (toSync.Count > 0)
            _ = Task.Run(async () =>
            {
                foreach (var id in toSync.Distinct()) await _s.Sync.SyncAllAsync(id);
            });
    }
}
