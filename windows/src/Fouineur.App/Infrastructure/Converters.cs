using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Fouineur.Core.Model;

namespace Fouineur.App.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, null => false, string s => s.Length > 0, int i => i != 0, _ => true };
        return b ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

/// <summary>Compare une valeur à un paramètre (pour des RadioButton liés à une énumération ou une chaîne).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? (t.IsEnum ? Enum.Parse(t, p.ToString()!) : p) : Binding.DoNothing;
}

public static class KindVisuals
{
    public static string Glyph(ItemKind k) => k switch
    {
        ItemKind.Mail => "",
        ItemKind.Teams => "",
        ItemKind.SharePoint => "",
        ItemKind.OneDrive => "",
        ItemKind.Fichier => "",
        ItemKind.Glpi => "\uE90F",
        _ => "",
    };

    public static Color Color(ItemKind k) => k switch
    {
        ItemKind.Mail => System.Windows.Media.Color.FromRgb(0x0F, 0x6C, 0xBD),
        ItemKind.Teams => System.Windows.Media.Color.FromRgb(0x5B, 0x5F, 0xC7),
        ItemKind.SharePoint => System.Windows.Media.Color.FromRgb(0x03, 0x83, 0x87),
        ItemKind.OneDrive => System.Windows.Media.Color.FromRgb(0x00, 0x78, 0xD4),
        ItemKind.Fichier => System.Windows.Media.Color.FromRgb(0xC1, 0x7A, 0x00),
        ItemKind.Glpi => System.Windows.Media.Color.FromRgb(0xD9, 0x5F, 0x2B),
        _ => Colors.Gray,
    };

    public static Brush Brush(ItemKind k)
    {
        var b = new SolidColorBrush(Color(k));
        b.Freeze();
        return b;
    }

    /// <summary>Glyphe et couleur par type de fichier (vignettes sans miniature).</summary>
    public static (string Glyph, Color Color, string Label) ForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".doc" or ".docx" or ".docm" or ".dotx" or ".odt" or ".rtf" => ("", System.Windows.Media.Color.FromRgb(0x18, 0x5A, 0xBD), "Document Word"),
        ".xls" or ".xlsx" or ".xlsm" or ".ods" or ".csv" => ("", System.Windows.Media.Color.FromRgb(0x10, 0x7C, 0x41), "Classeur Excel"),
        ".ppt" or ".pptx" or ".pptm" or ".odp" or ".ppsx" => ("", System.Windows.Media.Color.FromRgb(0xC4, 0x3E, 0x1C), "Présentation"),
        ".pdf" => ("", System.Windows.Media.Color.FromRgb(0xB3, 0x0B, 0x00), "PDF"),
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".heic" => ("", System.Windows.Media.Color.FromRgb(0x88, 0x17, 0x98), "Image"),
        ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => ("", System.Windows.Media.Color.FromRgb(0x77, 0x4C, 0xAA), "Vidéo"),
        ".mp3" or ".wav" or ".flac" or ".m4a" => ("", System.Windows.Media.Color.FromRgb(0x6B, 0x4C, 0xAA), "Audio"),
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => ("", System.Windows.Media.Color.FromRgb(0x7A, 0x5C, 0x2E), "Archive"),
        ".eml" or ".msg" => ("", System.Windows.Media.Color.FromRgb(0x0F, 0x6C, 0xBD), "Message"),
        ".glpi" => ("\uE90F", System.Windows.Media.Color.FromRgb(0xD9, 0x5F, 0x2B), "GLPI"),
        ".teams" => ("", System.Windows.Media.Color.FromRgb(0x5B, 0x5F, 0xC7), "Conversation Teams"),
        ".txt" or ".md" or ".log" => ("", System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60), "Texte"),
        ".html" or ".htm" or ".xml" or ".json" or ".cs" or ".py" or ".js" or ".ps1" or ".sql" => ("", System.Windows.Media.Color.FromRgb(0x2E, 0x6B, 0x8A), "Code / texte"),
        ".vsdx" or ".vsd" => ("", System.Windows.Media.Color.FromRgb(0x38, 0x55, 0xA8), "Visio"),
        "" => ("", System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60), "Fichier"),
        _ => ("", System.Windows.Media.Color.FromRgb(0x60, 0x60, 0x60), $"Fichier {ext.TrimStart('.').ToUpperInvariant()}"),
    };
}

/// <summary>
/// TextBlock.(Highlight.Text) : affiche un extrait en mettant en valeur les passages encadrés par \u0002 … \u0003.
/// </summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? v) => d.SetValue(TextProperty, v);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var s = e.NewValue as string ?? "";
        int i = 0;
        while (i < s.Length)
        {
            int a = s.IndexOf(SearchHit.HighlightStart, i);
            if (a < 0) { tb.Inlines.Add(new Run(s[i..])); break; }
            if (a > i) tb.Inlines.Add(new Run(s[i..a]));
            int b = s.IndexOf(SearchHit.HighlightEnd, a + 1);
            if (b < 0) b = s.Length;
            var run = new Run(s[(a + 1)..b]) { FontWeight = FontWeights.SemiBold };
            run.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            tb.Inlines.Add(run);
            i = b + 1;
        }
    }
}

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is long n ? Format(n) : "";
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();

    public static string Format(long n) => n switch
    {
        <= 0 => "",
        < 1024 => $"{n} o",
        < 1024 * 1024 => $"{n / 1024.0:0.#} Ko",
        < 1024L * 1024 * 1024 => $"{n / 1024.0 / 1024:0.#} Mo",
        _ => $"{n / 1024.0 / 1024 / 1024:0.##} Go",
    };
}
