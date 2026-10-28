using System.Text.Json;
using System.Text.Json.Serialization;
using Fouineur.Core.Selection;

namespace Fouineur.Core.Configuration;

public enum SourceType { LocalFiles, Microsoft365, Zimbra, Glpi }

public sealed class SourceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public SourceType Type { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public SelectionRules Rules { get; set; } = new();

    // Fichiers locaux et partages réseau
    public List<string> Paths { get; set; } = new();
    public List<string> ExcludePatterns { get; set; } = new() { ".git", "node_modules", "$RECYCLE.BIN", "System Volume Information", "~$*", "*.tmp", "Thumbs.db", "desktop.ini" };
    public int MaxFileSizeMb { get; set; } = 50;

    // Zimbra
    public string ServerUrl { get; set; } = "";
    public string UserName { get; set; } = "";
    /// <summary>Comptes délégués (boîte entière) lus avec les identifiants de l'utilisateur.</summary>
    public List<string> ExtraAccounts { get; set; } = new();
    /// <summary>Gabarit du lien webmail : {base}, {id}, {account}, {folder}.</summary>
    public string WebmailTemplate { get; set; } = "{base}/modern/email/Inbox/message/{id}";

    // Microsoft 365
    public string ClientId { get; set; } = "";
    public string Tenant { get; set; } = "organizations";
    public string AccountHint { get; set; } = "";
    public int ChatHistoryDays { get; set; } = 365;
    public int ChannelHistoryDays { get; set; } = 730;

    /// <summary>Autoriser l'envoi d'extraits de cette source à Claude (recherche assistée).</summary>
    public bool AllowClaude { get; set; } = true;

    // GLPI (ServerUrl = adresse de GLPI)
    /// <summary>Tickets, problèmes et changements modifiés depuis N jours ; 0 = tout l'historique.</summary>
    public int GlpiHistoryDays { get; set; }

    public DateTimeOffset? LastSync { get; set; }
    public string? LastError { get; set; }
}

public sealed class EmbeddingSettings
{
    /// <summary>« onnx », « ollama » ou « none ».</summary>
    public string Provider { get; set; } = "onnx";
    /// <summary>Dossier contenant model.onnx (+ model.onnx_data) et tokenizer.model.</summary>
    public string OnnxModelDir { get; set; } = "";
    public bool UseDirectML { get; set; } = true;
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "embeddinggemma";
    public int Dimensions { get; set; } = 256;
    public float MinSimilarity { get; set; } = 0.35f;
}

public sealed class ClaudeSettings
{
    /// <summary>Modèle qui analyse la question (rapide, peu coûteux).</summary>
    public string AnalysisModel { get; set; } = "claude-haiku-5-5";
    /// <summary>Modèle qui rédige la réponse.</summary>
    public string AnswerModel { get; set; } = "claude-sonnet-5-5";
    /// <summary>Nombre maximal de documents dont un extrait est envoyé.</summary>
    public int MaxDocuments { get; set; } = 12;
    /// <summary>Taille d'un extrait envoyé, en caractères.</summary>
    public int PassageChars { get; set; } = 1500;
    /// <summary>Dernier mode de recherche choisi : « index » ou « claude ».</summary>
    public string Mode { get; set; } = "index";
}

public sealed class AppSettings
{
    public List<SourceConfig> Sources { get; set; } = new();
    public EmbeddingSettings Embedding { get; set; } = new();
    public ClaudeSettings Claude { get; set; } = new();
    /// <summary>Synchronisation périodique, en minutes ; 0 = uniquement à la demande (bouton Synchroniser).</summary>
    public int SyncIntervalMinutes { get; set; } = 30;
    /// <summary>Synchroniser dès l'ouverture de l'application (désactivé par défaut).</summary>
    public bool SyncAtStartup { get; set; }
    public bool OpenOfficeInDesktopApp { get; set; } = true;
    public string ViewMode { get; set; } = "Liste";
    public bool ShowPreview { get; set; } = true;
    public double PreviewWidth { get; set; } = 460;
    /// <summary>Client Entra ID par défaut (inscription « Fouinneur »), modifiable par source.</summary>
    public string DefaultM365ClientId { get; set; } = "";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        }
        catch (JsonException) { File.Copy(path, path + ".corrompu", true); }
        return new AppSettings();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, true);
    }
}

/// <summary>Emplacements de l'application (par défaut %LOCALAPPDATA%\Fouineur).</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fouineur");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Logs);
    }
    public string Index => Path.Combine(Root, "index.sqlite");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Secrets => Path.Combine(Root, "secrets");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string Models => Path.Combine(Root, "models");
    public string TokenCache => Path.Combine(Root, "msal.cache");
}

/// <summary>Stockage chiffré des secrets (DPAPI sous Windows).</summary>
public interface ISecretStore
{
    string? Get(string key);
    void Set(string key, string? value);
}

/// <summary>Secrets en mémoire (tests).</summary>
public sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _d = new();
    public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
    public void Set(string key, string? value) { if (value is null) _d.Remove(key); else _d[key] = value; }
}
