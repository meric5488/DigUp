using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Fouineur.App.ViewModels;
using Fouineur.Core.Model;

namespace Fouineur.App.Views;

public partial class MainWindow : Window
{
    private MainViewModel Vm => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is MainViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
                ApplyViewMode();
                ApplyPreviewVisibility();
            }
        };
        Loaded += (_, _) =>
        {
            var w = App.Services.Settings.PreviewWidth;
            if (w > 200) PreviewColumn.Width = new GridLength(w);
            SearchBox.Focus();
        };
        Closing += (_, _) =>
        {
            if (Vm.ShowPreview && PreviewColumn.ActualWidth > 200) App.Services.Settings.PreviewWidth = PreviewColumn.ActualWidth;
            Host.Unload();
        };
        PreviewKeyDown += Window_PreviewKeyDown;

        if (Infrastructure.Branding.Logo is { } logo)
        {
            CompanyLogo.Source = logo;
            CompanyLogoBadge.Visibility = Visibility.Visible;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsTileView): ApplyViewMode(); break;
            case nameof(MainViewModel.ShowPreview): ApplyPreviewVisibility(); break;
            case nameof(MainViewModel.PreviewMode):
            case nameof(MainViewModel.PreviewFile):
            case nameof(MainViewModel.PreviewItem):
                UpdatePreview(); break;
        }
    }

    private void ApplyViewMode()
    {
        bool tiles = Vm.IsTileView;
        ResultsList.ItemTemplate = (DataTemplate)FindResource(tiles ? "TileTemplate" : "RowTemplate");
        ResultsList.ItemsPanel = (ItemsPanelTemplate)FindResource(tiles ? "TilePanel" : "ListPanel");
        // WrapPanel ne virtualise pas : on garde un défilement fluide en mode liste seulement.
        VirtualizingPanel.SetIsVirtualizing(ResultsList, !tiles);
    }

    private GridLength _savedPreviewWidth = new(460);

    private void ApplyPreviewVisibility()
    {
        if (Vm.ShowPreview)
        {
            PreviewPane.Visibility = Visibility.Visible;
            Splitter.Visibility = Visibility.Visible;
            PreviewColumn.Width = _savedPreviewWidth.Value > 200 ? _savedPreviewWidth : new GridLength(460);
        }
        else
        {
            if (PreviewColumn.ActualWidth > 200) _savedPreviewWidth = new GridLength(PreviewColumn.ActualWidth);
            Host.Unload();
            PreviewPane.Visibility = Visibility.Collapsed;
            Splitter.Visibility = Visibility.Collapsed;
            PreviewColumn.Width = new GridLength(0);
        }
    }

    // ─── Aperçu ───

    private string? _shownFile;

    private void UpdatePreview()
    {
        var mode = Vm.PreviewMode;
        LoadingPanel.Visibility = mode == PreviewMode.Loading ? Visibility.Visible : Visibility.Collapsed;
        NoPreview.Visibility = mode == PreviewMode.None ? Visibility.Visible : Visibility.Collapsed;
        WebmailButton.Visibility = Vm.PreviewItem?.Kind == ItemKind.Mail ? Visibility.Visible : Visibility.Collapsed;

        if (mode == PreviewMode.Handler && Vm.PreviewFile is { } file)
        {
            TextPanel.Visibility = Visibility.Collapsed;
            Host.Visibility = Visibility.Visible;
            if (_shownFile != file)
            {
                _shownFile = file;
                if (!Host.Show(file)) { _shownFile = null; Host.Visibility = Visibility.Collapsed; Vm.PreviewHandlerFailed(); }
            }
            return;
        }

        if (_shownFile is not null) { Host.Unload(); _shownFile = null; }
        Host.Visibility = Visibility.Collapsed;

        if (mode is PreviewMode.Text or PreviewMode.Message && Vm.PreviewItem is { } item)
        {
            TextPanel.Visibility = Visibility.Visible;
            TextView.Document = BuildDocument(item, mode == PreviewMode.Message, Vm.QueryTerms);
        }
        else TextPanel.Visibility = Visibility.Collapsed;
    }

    private FlowDocument BuildDocument(IndexedItem item, bool message, IReadOnlyList<string> terms)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13.5,
            PagePadding = new Thickness(18, 12, 18, 18),
            TextAlignment = TextAlignment.Left,
        };
        doc.SetResourceReference(FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        doc.SetResourceReference(FlowDocument.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");

        string body = item.Body;
        if (message && item.Kind == ItemKind.Mail)
        {
            // L'en-tête (De/À/Cc) est déjà en tête du corps indexé : on l'affiche sous forme de tableau.
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 10) };
            table.Columns.Add(new TableColumn { Width = new GridLength(70) });
            table.Columns.Add(new TableColumn());
            var group = new TableRowGroup();
            void Row(string label, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                var r = new TableRow();
                var l = new Paragraph(new Run(label)) { Margin = new Thickness(0, 1, 0, 1) };
                l.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
                r.Cells.Add(new TableCell(l));
                r.Cells.Add(new TableCell(new Paragraph(new Run(value)) { Margin = new Thickness(0, 1, 0, 1) }));
                group.Rows.Add(r);
            }
            Row("De", item.Author);
            Row("À", item.GetExtra("to"));
            Row("Cc", item.GetExtra("cc"));
            Row("Date", item.Date.ToLocalTime().ToString("dddd d MMMM yyyy HH:mm", CultureInfo.CurrentCulture));
            var att = item.GetExtra("attachments");
            if (!string.IsNullOrEmpty(att)) Row("Pièces", string.Join(", ", att.Split('|')));
            table.RowGroups.Add(group);
            doc.Blocks.Add(table);
            int cut = body.IndexOf("\n\n", StringComparison.Ordinal);
            if (body.StartsWith("De :", StringComparison.Ordinal) && cut > 0) body = body[(cut + 2)..];
        }
        else if (message && item.Kind == ItemKind.Teams && item.GetExtra("participants") is { Length: > 0 } who)
        {
            var p = new Paragraph(new Run("Participants : " + who)) { Margin = new Thickness(0, 0, 0, 10) };
            p.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            doc.Blocks.Add(p);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            var p = new Paragraph(new Run(item.Kind == ItemKind.Fichier || item.Kind is ItemKind.SharePoint or ItemKind.OneDrive
                ? "Pas de texte indexé pour ce fichier (format non lu ou fichier trop volumineux)."
                : "(message vide)"));
            p.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
            doc.Blocks.Add(p);
            return doc;
        }

        if (body.Length > 150_000) body = body[..150_000] + "\n\n[…] (aperçu tronqué)";
        var teamsHeader = new Regex(@"^.+ — \d{2}/\d{2}/\d{4} \d{2}:\d{2}$");
        foreach (var block in body.Split("\n\n"))
        {
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
            var lines = block.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) para.Inlines.Add(new LineBreak());
                var line = lines[i];
                if (item.Kind == ItemKind.Teams && teamsHeader.IsMatch(line))
                {
                    var r = new Run(line) { FontWeight = FontWeights.SemiBold };
                    r.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
                    para.Inlines.Add(r);
                }
                else AddHighlighted(para.Inlines, line, terms);
            }
            doc.Blocks.Add(para);
        }
        return doc;
    }

    private static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;

    private static void AddHighlighted(InlineCollection inlines, string text, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) { inlines.Add(new Run(text)); return; }
        int pos = 0;
        while (pos < text.Length)
        {
            int best = -1, bestLen = 0;
            foreach (var t in terms)
            {
                int i = Compare.IndexOf(text.AsSpan(pos), t.AsSpan(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out int len);
                if (i >= 0) i += pos;
                if (i >= 0 && (best < 0 || i < best)) { best = i; bestLen = len; }
            }
            if (best < 0) { inlines.Add(new Run(text[pos..])); break; }
            // étendre jusqu'à la fin du mot (recherche par préfixe)
            int end = best + bestLen;
            while (end < text.Length && char.IsLetterOrDigit(text[end])) end++;
            if (best > pos) inlines.Add(new Run(text[pos..best]));
            var hit = new Run(text[best..end]) { FontWeight = FontWeights.SemiBold };
            hit.SetResourceReference(TextElement.BackgroundProperty, "SystemFillColorCautionBackgroundBrush");
            inlines.Add(hit);
            pos = end;
        }
    }

    // ─── Clavier et souris ───

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Vm.Results.Count > 0:
                ResultsList.SelectedIndex = Math.Max(0, ResultsList.SelectedIndex);
                (ResultsList.ItemContainerGenerator.ContainerFromIndex(ResultsList.SelectedIndex) as ListViewItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter when Vm.SelectedResult is not null:
                Execute(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Vm.OpenContainerCommand : Vm.OpenCommand);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.Query = "";
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((e.Key == Key.F || e.Key == Key.L) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ResultsList, d) is ListViewItem)
            Execute(Vm.OpenCommand);
    }

    private void ResultsList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Execute(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Vm.OpenContainerCommand : Vm.OpenCommand);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Execute(Vm.CopyLinkCommand);
            e.Handled = true;
        }
    }

    private static void Execute(ICommand c)
    {
        if (c.CanExecute(null)) c.Execute(null);
    }
}
