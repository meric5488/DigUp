using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Fouineur.App.Infrastructure;
using Fouineur.App.Services;
using Fouineur.Core.Configuration;
using Fouineur.Core.Index;
using Fouineur.Core.Inventory;
using Fouineur.Core.Model;
using Fouineur.Core.Sync;

namespace Fouineur.App.ViewModels;

public sealed class CategoryViewModel : ObservableObject
{
    public ItemKind? Kind { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    public Brush? Accent { get; init; }
    private int _count;
    public int Count { get => _count; set { if (Set(ref _count, value)) OnPropertyChanged(nameof(CountText)); } }
    public string CountText => Count > 0 ? Count.ToString("N0", CultureInfo.CurrentCulture) : "";
}

public sealed class SourceStatusViewModel : ObservableObject
{
    public required SourceConfig Config { get; init; }
    public string Name => Config.Name;
    public string Glyph => Config.Type switch
    {
        SourceType.Microsoft365 => "",
        SourceType.Zimbra => "",
        SourceType.Glpi => "\uE90F",
        _ => Config.Paths.Any(p => p.StartsWith(@"\\")) ? "" : "",
    };
    public string Detail => !Config.Enabled ? "désactivée"
        : Config.LastError is { Length: > 0 } e ? e
        : Config.LastSync is { } d ? "synchronisée " + MainViewModel.Relative(d) : "jamais synchronisée";
    public bool HasError => !string.IsNullOrEmpty(Config.LastError);
    private bool _isFilter;
    public bool IsFilter { get => _isFilter; set => Set(ref _isFilter, value); }
    public void Refresh() { OnPropertyChanged(nameof(Detail)); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(Name)); }
}

public sealed class ResultViewModel : ObservableObject
{
    public ResultViewModel(SearchHit hit)
    {
        Hit = hit;
        var (g, c, label) = KindVisuals.ForExtension(hit.Item.Extension);
        TypeGlyph = hit.Item.Kind is ItemKind.Mail or ItemKind.Teams or ItemKind.Glpi ? KindVisuals.Glyph(hit.Item.Kind) : g;
        var b = new SolidColorBrush(hit.Item.Kind is ItemKind.Mail or ItemKind.Teams or ItemKind.Glpi ? KindVisuals.Color(hit.Item.Kind) : c);
        b.Freeze();
        TypeBrush = b;
        TypeLabel = hit.Item.Kind switch
        {
            ItemKind.Mail => "Message",
            ItemKind.Teams => hit.Item.Id.StartsWith("chatday:") ? "Conversation Teams" : "Fil de canal Teams",
            ItemKind.Glpi => hit.Item.GetExtra("glpiType") ?? "GLPI",
            _ => label,
        };
    }

    public SearchHit Hit { get; }
    public IndexedItem Item => Hit.Item;
    public string Title => Item.Title;
    public string Snippet => Hit.Snippet;
    public string Location => Item.Location;
    public string Author => Item.Author;
    public string DateText => MainViewModel.Relative(Item.Date);
    public string DateFull => Item.Date.ToLocalTime().ToString("dddd d MMMM yyyy HH:mm", CultureInfo.CurrentCulture);
    public string TypeGlyph { get; }
    public Brush TypeBrush { get; }
    public string TypeLabel { get; }
    public string KindGlyph => KindVisuals.Glyph(Item.Kind);
    public Brush KindBrush => KindVisuals.Brush(Item.Kind);
    public string KindLabel => ItemKindNames.Label(Item.Kind);
    public bool ByMeaningOnly => Hit.MatchedMeaning && !Hit.MatchedText;
    public string Meta => string.Join("  ·  ", new[] { Author, Location }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string TileMeta => $"{KindLabel} · {DateText}";
    public string Size => FileSizeConverter.Format(Item.Size);

    /// <summary>Numéro de la source dans la réponse de Claude ([1], [2]…), sinon 0.</summary>
    public int Number { get; init; }
    public string NumberText => Number > 0 ? Number.ToString(CultureInfo.InvariantCulture) : "";

    private ImageSource? _thumb;
    public ImageSource? Thumbnail { get => _thumb; set { if (Set(ref _thumb, value)) OnPropertyChanged(nameof(HasThumbnail)); } }
    public bool HasThumbnail => _thumb is not null;
    internal bool ThumbRequested;
}

public enum PreviewMode { None, Loading, Handler, Text, Message }

public sealed class MainViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _searchCts, _previewCts;

    public MainViewModel(AppServices services)
    {
        _s = services;
        Categories = new ObservableCollection<CategoryViewModel>(
            new[] { new CategoryViewModel { Kind = null, Label = "Tout", Glyph = "" } }
            .Concat(ItemKindNames.All.Select(k => new CategoryViewModel { Kind = k, Label = ItemKindNames.Label(k), Glyph = KindVisuals.Glyph(k), Accent = KindVisuals.Brush(k) })));
        _selectedCategory = Categories[0];
        // L'inventaire n'existe que dans la catégorie GLPI : on y revient si l'application s'était fermée dans ce mode.
        if (_s.Settings.Claude.Mode == "inventaire")
            _selectedCategory = Categories.FirstOrDefault(c => c.Kind == ItemKind.Glpi) ?? Categories[0];
        DateFilters = new[]
        {
            new KeyValuePair<DateFilter, string>(DateFilter.Tout, "Toutes les dates"),
            new KeyValuePair<DateFilter, string>(DateFilter.SeptJours, "7 derniers jours"),
            new KeyValuePair<DateFilter, string>(DateFilter.TrenteJours, "30 derniers jours"),
            new KeyValuePair<DateFilter, string>(DateFilter.UnAn, "Dernière année"),
        };
        _isTileView = _s.Settings.ViewMode == "Vignettes";
        _showPreview = _s.Settings.ShowPreview;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (IsInventoryMode) _ = RunInventoryAsync(useClaude: false); else _ = RunSearchAsync();
        };

        OpenCommand = new AsyncCommand(p => OpenAsync(p as ResultViewModel ?? SelectedResult, false), p => (p as ResultViewModel ?? SelectedResult) is not null);
        OpenContainerCommand = new AsyncCommand(p => OpenAsync(p as ResultViewModel ?? SelectedResult, true), p => (p as ResultViewModel ?? SelectedResult) is not null);
        OpenWebmailCommand = new RelayCommand(p => OpenWebmail(p as ResultViewModel ?? SelectedResult), p => (p as ResultViewModel ?? SelectedResult)?.Item.Kind == ItemKind.Mail);
        CopyLinkCommand = new RelayCommand(p => CopyLink(p as ResultViewModel ?? SelectedResult), p => (p as ResultViewModel ?? SelectedResult) is not null);
        SyncCommand = new RelayCommand(() => _ = _s.Sync.SyncAllAsync(SourceFilter?.Config.Id), () => !IsSyncing);
        CancelSyncCommand = new RelayCommand(() => _s.Sync.Cancel(), () => IsSyncing);
        SettingsCommand = new RelayCommand(() => OpenSettings(false));
        ListViewCommand = new RelayCommand(() => IsTileView = false);
        TileViewCommand = new RelayCommand(() => IsTileView = true);
        ClearCommand = new RelayCommand(() => Query = "");
        TogglePreviewCommand = new RelayCommand(() => ShowPreview = !ShowPreview);
        IndexModeCommand = new RelayCommand(() => Mode = "index");
        ClaudeModeCommand = new RelayCommand(() => Mode = "claude");
        InventoryModeCommand = new RelayCommand(() => Mode = "inventaire");
        RunInventoryCommand = new AsyncCommand(() => RunInventoryAsync(useClaude: true), () => !IsInventoryBusy);
        ExportInventoryCommand = new RelayCommand(ExportInventory, () => InventoryRows.Count > 0);
        CopyInventoryCommand = new RelayCommand(() => { try { Clipboard.SetText(InventoryService.ToTabs(InventoryRows)); InventorySummary += " · copié"; } catch (Exception) { } }, () => InventoryRows.Count > 0);
        AskClaudeCommand = new AsyncCommand(AskClaudeAsync, () => HasQuery && !IsClaudeBusy);
        CancelClaudeCommand = new RelayCommand(() => _claudeCts?.Cancel(), () => IsClaudeBusy);
        CopyAnswerCommand = new RelayCommand(() => { try { Clipboard.SetText(ClaudeAnswer); } catch (Exception) { } }, () => ClaudeAnswer.Length > 0);
        _mode = _s.Settings.Claude.Mode is "claude" or "inventaire" ? _s.Settings.Claude.Mode : "index";
        SourceFilterCommand = new RelayCommand(p =>
        {
            var s = p as SourceStatusViewModel;
            foreach (var x in Sources) x.IsFilter = x == s && SourceFilter != s;
            SourceFilter = Sources.FirstOrDefault(x => x.IsFilter);
        });

        _s.Sync.StatusChanged += st => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            SyncText = st.Message;
            SyncFraction = st.Fraction;
            IsSyncing = st.Phase != SyncPhase.Idle;
        });
        _s.Sync.IndexChanged += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var s in Sources) s.Refresh();
            OnPropertyChanged(nameof(IndexSummary));
            if (!_userBusy && IsIndexMode) _ = RunSearchAsync(keepSelection: true);
        });
        RefreshSources();
        _ = RunSearchAsync(force: true);
        if (IsInventoryMode) _ = RunInventoryAsync(useClaude: false);
    }

    // ─── Recherche ───

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? "")) return;
            OnPropertyChanged(nameof(HasQuery));
            if (IsClaudeMode) return; // Claude : on attend Entrée
            if (IsInventoryMode && !InventoryQuery.HasSyntax(_query) && _query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2) return; // phrase : Entrée
            _debounce.Stop(); _debounce.Start();
        }
    }
    public bool HasQuery => _query.Length > 0;

    public ObservableCollection<CategoryViewModel> Categories { get; }
    private CategoryViewModel _selectedCategory;
    public CategoryViewModel SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is null || !Set(ref _selectedCategory, value)) return;
            OnPropertyChanged(nameof(IsGlpiCategory));
            // Quitter la catégorie GLPI ramène à la recherche dans l'index.
            if (IsInventoryMode && !IsGlpiCategory) Mode = "index";
            else _ = RunSearchAsync();
        }
    }

    /// <summary>Le bouton « Inventaire » n'apparaît que dans la catégorie GLPI.</summary>
    public bool IsGlpiCategory => _selectedCategory.Kind == ItemKind.Glpi;

    public KeyValuePair<DateFilter, string>[] DateFilters { get; }
    private DateFilter _date = DateFilter.Tout;
    public DateFilter Date { get => _date; set { if (Set(ref _date, value)) _ = RunSearchAsync(); } }

    private bool _sortByDate;
    public bool SortByDate { get => _sortByDate; set { if (Set(ref _sortByDate, value)) _ = RunSearchAsync(keepSelection: true); } }

    public ObservableCollection<SourceStatusViewModel> Sources { get; } = new();
    private SourceStatusViewModel? _sourceFilter;
    public SourceStatusViewModel? SourceFilter { get => _sourceFilter; set { if (Set(ref _sourceFilter, value)) _ = RunSearchAsync(); } }

    private ObservableCollection<ResultViewModel> _results = new();
    /// <summary>Remplacée d'un bloc à chaque recherche (beaucoup plus rapide que vider puis ajouter un par un).</summary>
    public ObservableCollection<ResultViewModel> Results { get => _results; private set => Set(ref _results, value); }
    private ResultViewModel? _selected;
    public ResultViewModel? SelectedResult
    {
        get => _selected;
        set { if (Set(ref _selected, value)) _ = LoadPreviewAsync(value); }
    }

    private string _summary = "";
    public string ResultSummary { get => _summary; set => Set(ref _summary, value); }
    private bool _searching;
    public bool IsSearching { get => _searching; set => Set(ref _searching, value); }
    public bool IsEmpty => Results.Count == 0 && !IsSearching;
    public string EmptyText => _s.Settings.Sources.Count == 0
        ? "Ajoutez une première source (fichiers, Microsoft 365, Zimbra) dans Sources."
        : HasQuery ? "Aucun résultat. Essayez d'autres mots, ou « Tout » et « Toutes les dates »." : "L'index est vide pour l'instant : la synchronisation est peut-être en cours.";

    private bool _userBusy;

    public async Task RunSearchAsync(bool keepSelection = false, bool force = false)
    {
        // En mode Claude, la liste montre les sources de la réponse : pas de recherche automatique.
        if (IsClaudeMode && !force && (HasClaudePanel || HasQuery)) return;
        if (IsInventoryMode && !force) return;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        try
        {
            var req = new SearchRequest
            {
                Text = Query, Kind = SelectedCategory.Kind, Date = Date, SourceId = SourceFilter?.Config.Id, Limit = 200,
            };
            var resp = await _s.Search.SearchAsync(req, cts.Token);
            if (cts.IsCancellationRequested) return;

            var keepId = keepSelection ? SelectedResult?.Item.Id : null;
            var hits = SortByDate ? resp.Hits.OrderByDescending(h => h.Item.Date).ToList() : resp.Hits.ToList();
            Results = new ObservableCollection<ResultViewModel>(hits.Select(h => new ResultViewModel(h)));

            int total = 0;
            foreach (var c in Categories)
            {
                c.Count = c.Kind is null ? resp.Total : resp.CountsByKind.GetValueOrDefault(c.Kind.Value);
                total = resp.Total;
            }
            var shown = Results.Count;
            ResultSummary = HasQuery
                ? $"{shown:N0} résultat{(shown > 1 ? "s" : "")}{(resp.SemanticUsed ? " (texte et sens)" : "")} · {resp.Elapsed.TotalMilliseconds:0} ms"
                : $"{shown:N0} élément{(shown > 1 ? "s" : "")} récent{(shown > 1 ? "s" : "")}";
            SelectedResult = keepId is null ? Results.FirstOrDefault() : Results.FirstOrDefault(r => r.Item.Id == keepId) ?? Results.FirstOrDefault();
            if (IsTileView) _ = LoadThumbnailsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Logger.Error("Recherche", e); ResultSummary = "Erreur de recherche : " + e.Message; }
        finally
        {
            if (cts == _searchCts) IsSearching = false;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    // ─── Recherche par Claude ───

    private string _mode = "index";
    /// <summary>« index » : texte et sens à chaque frappe ; « claude » : question à Claude (Entrée) ; « inventaire » : tableau du parc GLPI.</summary>
    public string Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            _s.Settings.Claude.Mode = value;
            foreach (var n in new[] { nameof(IsClaudeMode), nameof(IsIndexMode), nameof(IsInventoryMode), nameof(IsNotInventoryMode), nameof(SearchPlaceholder), nameof(HasClaudePanel) })
                OnPropertyChanged(n);
            if (value != "claude") _claudeCts?.Cancel();
            if (value == "index") _ = RunSearchAsync(force: true);
            if (value == "inventaire") _ = RunInventoryAsync(useClaude: false);
        }
    }
    public bool IsClaudeMode { get => _mode == "claude"; set => Mode = value ? "claude" : "index"; }
    public bool IsIndexMode => _mode == "index";
    public bool IsInventoryMode => _mode == "inventaire";
    public bool IsNotInventoryMode => !IsInventoryMode;

    public string SearchPlaceholder => _mode switch
    {
        "claude" => "Posez une question à Claude, puis appuyez sur Entrée…",
        "inventaire" => "Inventaire : « firefox », « logiciel:office version<2019 os:\"windows 10\" », ou une question + Entrée…",
        _ => "Rechercher dans les mails, Teams, SharePoint, OneDrive et fichiers…",
    };

    // ─── Inventaire (tableau du parc GLPI, calculé sur ce poste) ───

    public ICommand InventoryModeCommand { get; }
    public ICommand RunInventoryCommand { get; }
    public ICommand ExportInventoryCommand { get; }
    public ICommand CopyInventoryCommand { get; }

    private ObservableCollection<InventoryRow> _inventoryRows = new();
    public ObservableCollection<InventoryRow> InventoryRows { get => _inventoryRows; private set => Set(ref _inventoryRows, value); }
    private string _inventorySummary = "";
    public string InventorySummary { get => _inventorySummary; set => Set(ref _inventorySummary, value); }
    private string _inventoryInterpretation = "";
    public string InventoryInterpretation { get => _inventoryInterpretation; set => Set(ref _inventoryInterpretation, value); }
    private bool _inventoryBusy;
    public bool IsInventoryBusy { get => _inventoryBusy; set { if (Set(ref _inventoryBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
    private InventoryRow? _selectedInventoryRow;
    public InventoryRow? SelectedInventoryRow
    {
        get => _selectedInventoryRow;
        set
        {
            if (!Set(ref _selectedInventoryRow, value) || value is null) return;
            // L'aperçu et « Ouvrir » réutilisent la fiche indexée.
            if (_s.Store.Get(value.ItemId) is { } item) SelectedResult = new ResultViewModel(new SearchHit { Item = item });
        }
    }
    private CancellationTokenSource? _inventoryCts;

    /// <summary>
    /// Syntaxe directe (ou un simple nom de logiciel) : calcul immédiat. Phrase en langage courant + Entrée :
    /// Claude traduit la question en critères (seule la question lui est envoyée), puis calcul local.
    /// </summary>
    public async Task RunInventoryAsync(bool useClaude)
    {
        _inventoryCts?.Cancel();
        var cts = _inventoryCts = new CancellationTokenSource();
        var text = Query.Trim();
        IsInventoryBusy = true;
        try
        {
            InventoryFilter filter;
            bool sentence = !InventoryQuery.HasSyntax(text) && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 2;
            if (useClaude && sentence && _s.Claude.HasKey)
            {
                InventoryInterpretation = "Claude interprète la question…";
                filter = await _s.InventoryPlanner.PlanAsync(text, cts.Token);
                if (cts.IsCancellationRequested) return;
                InventoryInterpretation = "Interprété par Claude : " + filter.Describe();
            }
            else
            {
                if (sentence && !useClaude) return; // on attend Entrée pour une phrase
                filter = InventoryQuery.Parse(text);
                InventoryInterpretation = (sentence && !_s.Claude.HasKey ? "Sans clé Claude, la phrase est lue comme un nom de logiciel. " : "")
                                          + "Critères : " + filter.Describe();
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => InventoryService.Run(_s.Store, filter), cts.Token);
            if (cts.IsCancellationRequested) return;
            InventoryRows = new ObservableCollection<InventoryRow>(result.Rows);
            InventorySummary = result.ScannedCount == 0
                ? "Aucune fiche d'inventaire indexée : ajoutez une source GLPI, cochez « Inventaire » puis synchronisez."
                : $"{result.Rows.Count:N0} ligne{(result.Rows.Count > 1 ? "s" : "")} · {result.AssetCount:N0} poste{(result.AssetCount > 1 ? "s" : "")} sur {result.ScannedCount:N0} fiches · {sw.ElapsedMilliseconds} ms"
                  + (filter.Software is not null && !result.HasSoftwareData ? " · aucune liste de logiciels indexée (resynchronisez GLPI)" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error("Inventaire", e);
            InventoryInterpretation = "Erreur : " + e.Message;
        }
        finally { if (cts == _inventoryCts) IsInventoryBusy = false; }
    }

    private void ExportInventory()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exporter l'inventaire", Filter = "Fichier CSV (Excel)|*.csv",
            FileName = $"inventaire-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, InventoryService.ToCsv(InventoryRows), new System.Text.UTF8Encoding(true));
            InventorySummary += " · exporté";
            AppServices.Launch(dlg.FileName);
        }
        catch (Exception e) { MessageBox.Show("Export impossible : " + e.Message, "Fouineur", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private CancellationTokenSource? _claudeCts;
    private string _claudeAnswer = "";
    public string ClaudeAnswer { get => _claudeAnswer; set { if (Set(ref _claudeAnswer, value)) OnPropertyChanged(nameof(HasClaudePanel)); } }
    private string _claudeStatus = "";
    public string ClaudeStatus { get => _claudeStatus; set { if (Set(ref _claudeStatus, value)) OnPropertyChanged(nameof(HasClaudePanel)); } }
    private string _claudePlan = "";
    public string ClaudePlan { get => _claudePlan; set => Set(ref _claudePlan, value); }
    private bool _claudeBusy;
    public bool IsClaudeBusy
    {
        get => _claudeBusy;
        set { if (Set(ref _claudeBusy, value)) { OnPropertyChanged(nameof(HasClaudePanel)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool HasClaudePanel => IsClaudeMode && (IsClaudeBusy || ClaudeAnswer.Length > 0 || ClaudeStatus.Length > 0);

    public ICommand IndexModeCommand { get; }
    public ICommand ClaudeModeCommand { get; }
    public ICommand AskClaudeCommand { get; }
    public ICommand CancelClaudeCommand { get; }
    public ICommand CopyAnswerCommand { get; }

    private async Task AskClaudeAsync()
    {
        var question = Query.Trim();
        if (question.Length == 0) return;
        _claudeCts?.Cancel();
        var cts = _claudeCts = new CancellationTokenSource();
        var ct = cts.Token;
        ClaudeAnswer = ""; ClaudePlan = "";
        if (!_s.ClaudeSearch.IsConfigured)
        {
            ClaudeStatus = "Clé d'API Anthropic manquante : ajoutez-la dans Sources › Claude.";
            return;
        }
        IsClaudeBusy = true;
        _userBusy = true;
        try
        {
            ClaudeStatus = "Claude analyse la question…";
            var plan = await _s.ClaudeSearch.PlanAsync(question, ct);
            ClaudePlan = "Recherche : " + plan.Describe();

            ClaudeStatus = "Recherche dans l'index…";
            var ctx = await _s.ClaudeSearch.RetrieveAsync(question, plan, SelectedCategory.Kind, ct);
            Results = new ObservableCollection<ResultViewModel>(ctx.Sources.Select((h, i) => new ResultViewModel(h) { Number = i + 1 }));
            foreach (var c in Categories)
                c.Count = c.Kind is null ? ctx.Sources.Count : ctx.Sources.Count(h => h.Item.Kind == c.Kind);
            ResultSummary = ctx.Sources.Count == 0 ? "Aucun document trouvé" : $"{ctx.Sources.Count} document(s) consulté(s) par Claude";
            SelectedResult = Results.FirstOrDefault();
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(EmptyText));

            ClaudeStatus = ctx.Sources.Count == 0 ? "" : "Claude rédige la réponse…";
            var sb = new System.Text.StringBuilder();
            await foreach (var piece in _s.ClaudeSearch.AnswerAsync(ctx, ct))
            {
                sb.Append(piece);
                ClaudeAnswer = sb.ToString();
            }
            var sent = ctx.CharsSent >= 1000 ? $"{ctx.CharsSent / 1000.0:0.#} k caractères" : $"{ctx.CharsSent} caractères";
            ClaudeStatus = ctx.Sources.Count == 0 ? "" :
                $"{_s.Settings.Claude.AnswerModel} · {ctx.Sources.Count} extrait(s) envoyé(s) à Anthropic ({sent})"
                + (ctx.Excluded > 0 ? $" · {ctx.Excluded} résultat(s) de sources non autorisées écarté(s)" : "");
        }
        catch (OperationCanceledException) { ClaudeStatus = "Interrompu."; }
        catch (Core.Ai.ClaudeException e) { ClaudeStatus = "Erreur Claude : " + e.Message; Logger.Warn(e.Message); }
        catch (System.Net.Http.HttpRequestException e) { ClaudeStatus = "Connexion à Anthropic impossible : " + e.Message; Logger.Warn(e.Message); }
        finally
        {
            if (cts == _claudeCts) IsClaudeBusy = false;
            _userBusy = false;
        }
    }

    /// <summary>Sélectionne la source [n] citée dans la réponse.</summary>
    public void SelectSource(int number)
    {
        var r = Results.FirstOrDefault(x => x.Number == number);
        if (r is not null) SelectedResult = r;
    }

    // ─── Affichage ───

    private bool _isTileView;
    public bool IsTileView
    {
        get => _isTileView;
        set
        {
            if (!Set(ref _isTileView, value)) return;
            _s.Settings.ViewMode = value ? "Vignettes" : "Liste";
            if (value) _ = LoadThumbnailsAsync();
        }
    }

    private bool _showPreview;
    public bool ShowPreview
    {
        get => _showPreview;
        set
        {
            if (!Set(ref _showPreview, value)) return;
            _s.Settings.ShowPreview = value;
            if (value) _ = LoadPreviewAsync(SelectedResult);
            else PreviewMode = PreviewMode.None;
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        var list = Results.Take(200).Where(r => !r.ThumbRequested).ToList();
        using var gate = new SemaphoreSlim(4);
        var tasks = list.Select(async r =>
        {
            r.ThumbRequested = true;
            await gate.WaitAsync();
            try
            {
                ImageSource? img = null;
                if (r.Item.Kind == ItemKind.Fichier) img = await _s.Thumbnails.GetAsync(r.Item.OpenUri);
                else if (r.Item.Kind is ItemKind.SharePoint or ItemKind.OneDrive && _s.Sync.GetConnector(r.Item.SourceId) is { } con)
                    img = ShellThumbnails.FromBytes(await con.GetThumbnailAsync(r.Item, default));
                if (img is not null) r.Thumbnail = img;
            }
            catch (Exception) { /* pas de miniature */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    // ─── Aperçu ───

    private PreviewMode _previewMode;
    public PreviewMode PreviewMode { get => _previewMode; set => Set(ref _previewMode, value); }
    private IndexedItem? _previewItem;
    public IndexedItem? PreviewItem { get => _previewItem; set => Set(ref _previewItem, value); }
    private string? _previewFile;
    /// <summary>Fichier à confier au gestionnaire d'aperçu Windows (la vue l'observe).</summary>
    public string? PreviewFile { get => _previewFile; set => Set(ref _previewFile, value); }
    private string _previewNote = "";
    public string PreviewNote { get => _previewNote; set => Set(ref _previewNote, value); }

    public IReadOnlyList<string> QueryTerms => QueryParser.Words(QueryParser.Parse(Query).SemanticText).Where(w => w.Length > 1).ToList();

    public string PreviewTo => PreviewItem?.GetExtra("to") ?? "";
    public string PreviewCc => PreviewItem?.GetExtra("cc") ?? "";
    public IReadOnlyList<string> PreviewAttachments => (PreviewItem?.GetExtra("attachments") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
    public string PreviewParticipants => PreviewItem?.GetExtra("participants") ?? "";

    private async Task LoadPreviewAsync(ResultViewModel? r)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        PreviewFile = null;
        PreviewNote = "";
        if (r is null || !ShowPreview) { PreviewItem = null; PreviewMode = PreviewMode.None; return; }
        var item = await Task.Run(() => _s.Store.Get(r.Item.Id)) ?? r.Item;
        if (cts.IsCancellationRequested) return;
        PreviewItem = item;
        OnPropertyChanged(nameof(PreviewTo)); OnPropertyChanged(nameof(PreviewCc));
        OnPropertyChanged(nameof(PreviewAttachments)); OnPropertyChanged(nameof(PreviewParticipants));
        OnPropertyChanged(nameof(QueryTerms));

        if (item.Kind is ItemKind.Mail or ItemKind.Teams or ItemKind.Glpi) { PreviewMode = PreviewMode.Message; return; }

        if (!Controls.PreviewHost.HasHandler(item.Extension))
        {
            PreviewMode = PreviewMode.Text;
            PreviewNote = "Aperçu Windows indisponible pour ce type : texte indexé.";
            return;
        }
        PreviewMode = PreviewMode.Loading;
        try
        {
            await Task.Delay(120, cts.Token); // évite de télécharger en faisant défiler la liste
            var con = _s.Sync.GetConnector(item.SourceId);
            var path = con is null ? null : await con.GetPreviewFileAsync(item, cts.Token);
            if (cts.IsCancellationRequested) return;
            if (path is null || !File.Exists(path))
            {
                PreviewMode = PreviewMode.Text;
                PreviewNote = item.Kind == ItemKind.Fichier ? "Fichier inaccessible (partage hors ligne ?) : texte indexé." : "Aperçu indisponible : texte indexé.";
                return;
            }
            PreviewFile = path;
            PreviewMode = PreviewMode.Handler;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Warn($"Aperçu de {item.Title} : {e.Message}");
            PreviewMode = PreviewMode.Text;
            PreviewNote = "Aperçu indisponible : texte indexé.";
        }
    }

    /// <summary>Appelé par la vue si le gestionnaire Windows refuse le fichier.</summary>
    public void PreviewHandlerFailed()
    {
        PreviewMode = PreviewMode.Text;
        PreviewNote = "Le gestionnaire d'aperçu Windows n'a pas pu afficher ce fichier : texte indexé.";
    }

    // ─── Actions ───

    public ICommand OpenCommand { get; }
    public ICommand OpenContainerCommand { get; }
    public ICommand OpenWebmailCommand { get; }
    public ICommand CopyLinkCommand { get; }
    public ICommand SyncCommand { get; }
    public ICommand CancelSyncCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ListViewCommand { get; }
    public ICommand TileViewCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand TogglePreviewCommand { get; }
    public ICommand SourceFilterCommand { get; }

    private async Task OpenAsync(ResultViewModel? r, bool container)
    {
        if (r is null) return;
        _userBusy = true;
        try { await _s.OpenAsync(r.Item, container); }
        finally { _userBusy = false; }
    }

    private void OpenWebmail(ResultViewModel? r)
    {
        if (r is null || _s.Sync.GetConnector(r.Item.SourceId) is not Core.Connectors.Zimbra.ZimbraConnector z) return;
        AppServices.Launch(z.WebmailUrl(r.Item));
    }

    private void CopyLink(ResultViewModel? r)
    {
        if (r is null) return;
        var text = r.Item.Kind switch
        {
            ItemKind.Mail => $"{r.Item.Title} — {r.Item.Author} — {r.Item.Date.ToLocalTime():g}",
            _ => string.IsNullOrEmpty(r.Item.OpenUri) ? r.Item.Title : r.Item.OpenUri,
        };
        try { Clipboard.SetText(text); } catch (Exception) { }
    }

    public void OpenSettings(bool firstRun)
    {
        var vm = new SettingsViewModel(_s, firstRun);
        var w = new Views.SettingsWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        w.ShowDialog();
        vm.Commit();
        RefreshSources();
        OnPropertyChanged(nameof(EmbedderStatus));
        _ = RunSearchAsync(keepSelection: true);
    }

    private void RefreshSources()
    {
        Sources.Clear();
        foreach (var s in _s.Settings.Sources) Sources.Add(new SourceStatusViewModel { Config = s });
        if (SourceFilter is not null && !_s.Settings.Sources.Contains(SourceFilter.Config)) SourceFilter = null;
        OnPropertyChanged(nameof(IndexSummary));
    }

    // ─── Barre d'état ───

    private string _syncText = "Prêt";
    public string SyncText { get => _syncText; set => Set(ref _syncText, value); }
    private double? _syncFraction;
    public double? SyncFraction { get => _syncFraction; set { if (Set(ref _syncFraction, value)) { OnPropertyChanged(nameof(SyncPercent)); OnPropertyChanged(nameof(SyncIndeterminate)); } } }
    public double SyncPercent => (_syncFraction ?? 0) * 100;
    public bool SyncIndeterminate => _syncFraction is null;
    private bool _isSyncing;
    public bool IsSyncing { get => _isSyncing; set { if (Set(ref _isSyncing, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string EmbedderStatus => _s.EmbedderStatus;
    public string IndexSummary => $"{_s.Store.Count():N0} éléments indexés";

    public static string Relative(DateTimeOffset d)
    {
        var local = d.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == today.AddDays(-1)) return "hier " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date > today.AddDays(-7)) return local.ToString("dddd HH:mm", CultureInfo.CurrentCulture);
        if (local.Year == today.Year) return local.ToString("d MMM", CultureInfo.CurrentCulture);
        return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
