using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace Fouineur.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute { }

public static class Assert
{
    public static void True(bool c, string? msg = null) { if (!c) throw new Exception("Attendu vrai" + (msg is null ? "" : " : " + msg)); }
    public static void False(bool c, string? msg = null) { if (c) throw new Exception("Attendu faux" + (msg is null ? "" : " : " + msg)); }
    public static void Equal<T>(T expected, T actual, string? msg = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Attendu « {expected} », obtenu « {actual} »" + (msg is null ? "" : " : " + msg));
    }
    public static void Contains(string needle, string hay, string? msg = null)
    {
        if (!hay.Contains(needle)) throw new Exception($"« {Short(hay)} » ne contient pas « {needle} »" + (msg is null ? "" : " : " + msg));
    }
    public static void NotContains(string needle, string hay)
    {
        if (hay.Contains(needle)) throw new Exception($"« {Short(hay)} » contient « {needle} »");
    }
    public static void NotNull(object? o, string? msg = null) { if (o is null) throw new Exception("Valeur nulle" + (msg is null ? "" : " : " + msg)); }
    public static void Null(object? o, string? msg = null) { if (o is not null) throw new Exception($"Attendu null, obtenu {o}" + (msg is null ? "" : " : " + msg)); }
    public static async Task Throws<TE>(Func<Task> f) where TE : Exception
    {
        try { await f(); } catch (TE) { return; }
        throw new Exception($"Exception {typeof(TE).Name} attendue");
    }
    private static string Short(string s) => s.Length > 300 ? s[..300] + "…" : s;
}

public static class Runner
{
    public static async Task<int> Main(string[] args)
    {
        var filter = args.FirstOrDefault();
        var tests = typeof(Runner).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.GetCustomAttribute<TestAttribute>() is not null).Select(m => (t, m)))
            .Where(x => filter is null || (x.t.Name + "." + x.m.Name).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.t.Name).ThenBy(x => x.m.Name).ToList();
        int ok = 0, ko = 0;
        foreach (var (t, m) in tests)
        {
            var name = $"{t.Name}.{m.Name}";
            try
            {
                var r = m.Invoke(null, null);
                if (r is Task task) await task;
                ok++;
                Console.WriteLine($"  ok   {name}");
            }
            catch (Exception e)
            {
                ko++;
                var inner = e is TargetInvocationException tie ? tie.InnerException! : e;
                Console.WriteLine($"  ÉCHEC {name}\n       {inner.GetType().Name}: {inner.Message}\n{Indent(inner.StackTrace)}");
            }
        }
        Console.WriteLine($"\n{ok} réussis, {ko} échoués, {tests.Count} au total.");
        return ko == 0 ? 0 : 1;
    }

    private static string Indent(string? s) => string.Join("\n", (s ?? "").Split('\n').Take(6).Select(l => "       " + l.Trim()));
}

/// <summary>Faux serveur HTTP : une fonction reçoit (méthode, URL, corps) et renvoie la réponse.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public readonly List<(string Method, string Url, string Body)> Calls = new();
    /// <summary>Noms des en-têtes de la dernière requête reçue.</summary>
    public HashSet<string> LastHeaders { get; private set; } = new();
    private readonly Func<string, string, string, (HttpStatusCode, string, string?)> _route;

    public FakeHttp(Func<string, string, string, (HttpStatusCode Status, string Body, string? ContentType)> route) => _route = route;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
        lock (Calls)
        {
            Calls.Add((request.Method.Method, url, body));
            LastHeaders = new HashSet<string>(request.Headers.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        }
        var (status, respBody, ctype) = _route(request.Method.Method, url, body);
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(respBody, Encoding.UTF8, ctype ?? "application/json"),
        };
        return resp;
    }

    public static (HttpStatusCode, string, string?) Json(JsonNode n) => (HttpStatusCode.OK, n.ToJsonString(), "application/json");
    public static (HttpStatusCode, string, string?) Json(string s) => (HttpStatusCode.OK, s, "application/json");
    public static (HttpStatusCode, string, string?) Status(HttpStatusCode s, string body = "{}") => (s, body, "application/json");
    public static (HttpStatusCode, string, string?) Bytes(string text) => (HttpStatusCode.OK, text, "application/octet-stream");
}

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fouineur-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() { Directory.CreateDirectory(Path); }
    public string File(string rel, string content)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}
