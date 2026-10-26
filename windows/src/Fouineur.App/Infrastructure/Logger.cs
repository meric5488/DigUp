using System.IO;

namespace Fouineur.App.Infrastructure;

/// <summary>Journal texte quotidien dans %LOCALAPPDATA%\Fouineur\logs.</summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static string? _dir;
    private static readonly LinkedList<string> Recent = new();

    public static event Action<string>? Logged;

    public static void Init(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        foreach (var f in new DirectoryInfo(dir).GetFiles("fouineur-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
            try { f.Delete(); } catch (IOException) { }
    }

    public static string? CurrentFile => _dir is null ? null : Path.Combine(_dir, $"fouineur-{DateTime.Now:yyyyMMdd}.log");

    public static IReadOnlyList<string> RecentLines { get { lock (Gate) return Recent.ToList(); } }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("ATTN", message);
    public static void Error(string message, Exception? e = null) => Write("ERR ", e is null ? message : $"{message} : {e}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            Recent.AddLast(line);
            while (Recent.Count > 500) Recent.RemoveFirst();
            if (CurrentFile is { } f)
                try { File.AppendAllText(f, line + Environment.NewLine); } catch (IOException) { }
        }
        Logged?.Invoke(line);
    }
}
