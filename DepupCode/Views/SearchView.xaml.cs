using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace DepupCode.Views;

public sealed record LineHit(string FullPath, int Line, string Preview, IReadOnlyList<(int Start, int Length)> Ranges);

public sealed class FileGroup
{
    public FileGroup(string fullPath, string relativePath, List<LineHit> hits)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Hits = hits;
    }

    public string FullPath { get; }
    public string RelativePath { get; }
    public List<LineHit> Hits { get; }
    public int Count => Hits.Sum(h => h.Ranges.Count);
    public string FileName => Path.GetFileName(RelativePath);
    public string Directory
    {
        get
        {
            var dir = Path.GetDirectoryName(RelativePath);
            return string.IsNullOrEmpty(dir) ? "" : "  " + dir.Replace('\\', '/');
        }
    }
}

/// <summary>Renders a search hit with the matched text highlighted.</summary>
public static class HitText
{
    public static readonly DependencyProperty HitProperty =
        DependencyProperty.RegisterAttached("Hit", typeof(LineHit), typeof(HitText), new PropertyMetadata(null, OnHitChanged));

    public static LineHit? GetHit(DependencyObject d) => (LineHit?)d.GetValue(HitProperty);
    public static void SetHit(DependencyObject d, LineHit? value) => d.SetValue(HitProperty, value);

    private static void OnHitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        if (e.NewValue is not LineHit hit) return;
        var text = hit.Preview;
        var pos = 0;
        foreach (var (start, length) in hit.Ranges.OrderBy(r => r.Start))
        {
            if (start < pos || start + length > text.Length) continue;
            if (start > pos) block.Inlines.Add(new Run(text[pos..start]));
            block.Inlines.Add(new Run(text.Substring(start, length))
            {
                Background = new SolidColorBrush(Color.FromArgb(150, 245, 158, 11)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold
            });
            pos = start + length;
        }
        if (pos < text.Length) block.Inlines.Add(new Run(text[pos..]));
    }
}

/// <summary>Highlights search matches inside the code editor.</summary>
public sealed class SearchHighlighter : DocumentColorizingTransformer
{
    public Regex? Pattern { get; set; }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (Pattern is null || line.Length == 0) return;
        var text = CurrentContext.Document.GetText(line);
        foreach (Match m in Pattern.Matches(text))
        {
            if (m.Length == 0) continue;
            ChangeLinePart(line.Offset + m.Index, line.Offset + m.Index + m.Length, element =>
            {
                element.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(Color.FromArgb(150, 245, 158, 11)));
                element.TextRunProperties.SetForegroundBrush(Brushes.White);
            });
        }
    }
}

public partial class SearchView : UserControl
{
    private string? _root;
    private readonly Action<string, int> _open;
    private readonly Action<Regex?> _highlight;

    public SearchView(Action<string, int> open, Action<Regex?> highlight)
    {
        InitializeComponent();
        _open = open;
        _highlight = highlight;
    }

    public void SetRoot(string? root)
    {
        if (_root != root)
        {
            _root = root;
            ResultsTree.ItemsSource = null;
            StatusText.Text = string.IsNullOrWhiteSpace(root) ? "Buka folder project untuk mencari." : "Masukkan kata lalu tekan Enter.";
        }
        FolderText.Text = string.IsNullOrWhiteSpace(root) ? "" : Path.GetFileName(root.TrimEnd('\\', '/'));
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void ToggleFilters(object sender, MouseButtonEventArgs e)
    {
        var show = FilterPanel.Visibility != Visibility.Visible;
        FilterPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FilterArrow.Text = show ? "⌃" : "⌄";
    }

    private async void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (QueryBox.Text.Trim().Length > 0) await SearchAsync();
    }

    private async void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    private Regex? BuildRegex(string query)
    {
        var pattern = RegexToggle.IsChecked == true ? query : Regex.Escape(query);
        if (WordToggle.IsChecked == true) pattern = @"\b(?:" + pattern + @")\b";
        var options = CaseToggle.IsChecked == true ? RegexOptions.None : RegexOptions.IgnoreCase;
        return new Regex(pattern, options, TimeSpan.FromSeconds(1));
    }

    private static List<Regex> BuildGlobs(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g =>
            {
                var p = g.Replace('\\', '/');
                var rx = "^" + Regex.Escape(p).Replace(@"\*\*", "§§").Replace(@"\*", "[^/]*").Replace("§§", ".*").Replace(@"\?", ".") + "$";
                // A bare pattern such as "*.ts" or "dist" matches at any depth.
                if (!p.Contains('/')) rx = "(^|/)" + rx.TrimStart('^');
                return new Regex(rx, RegexOptions.IgnoreCase);
            }).ToList();

    private async Task SearchAsync()
    {
        var query = QueryBox.Text;
        if (query.Trim().Length == 0)
        {
            ResultsTree.ItemsSource = null;
            _highlight(null);
            StatusText.Text = "Masukkan kata lalu tekan Enter.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_root) || !Directory.Exists(_root))
        {
            StatusText.Text = "Buka folder project terlebih dahulu.";
            return;
        }

        Regex regex;
        try { regex = BuildRegex(query)!; }
        catch (ArgumentException ex) { StatusText.Text = "Regex tidak valid: " + ex.Message; return; }

        var include = BuildGlobs(IncludeBox.Text);
        var exclude = BuildGlobs(ExcludeBox.Text);
        var root = _root;
        StatusText.Text = "Mencari…";
        ResultsTree.ItemsSource = null;

        var (groups, truncated) = await Task.Run(() =>
        {
            var found = new List<FileGroup>();
            var total = 0;
            var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is ".git" or "node_modules" or "bin" or "obj" or ".next" or ".vs"))
                .Take(4000);
            foreach (var file in files)
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (include.Count > 0 && !include.Any(g => g.IsMatch(rel))) continue;
                if (exclude.Any(g => g.IsMatch(rel))) continue;
                try
                {
                    if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                    var hits = new List<LineHit>();
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(file))
                    {
                        lineNumber++;
                        if (line.Length > 2000) continue;
                        var matches = regex.Matches(line);
                        if (matches.Count == 0) continue;
                        var lead = line.Length - line.TrimStart().Length;
                        var preview = line.Trim();
                        if (preview.Length > 220) preview = preview[..220];
                        var ranges = matches.Where(m => m.Length > 0 && m.Index - lead >= 0 && m.Index - lead + m.Length <= preview.Length)
                            .Select(m => (m.Index - lead, m.Length)).ToList();
                        if (ranges.Count == 0) continue;
                        hits.Add(new LineHit(file, lineNumber, preview, ranges));
                        total += ranges.Count;
                        if (total >= 1000) break;
                    }
                    if (hits.Count > 0) found.Add(new FileGroup(file, rel, hits));
                }
                catch { }
                if (total >= 1000) return (found, true);
            }
            return (found, false);
        });

        ResultsTree.ItemsSource = groups;
        _highlight(regex);
        var count = groups.Sum(g => g.Count);
        StatusText.Text = count == 0
            ? "Tidak ada hasil."
            : $"{count} results in {groups.Count} files" + (truncated ? " (limit)" : "");
    }

    private void Result_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is LineHit hit) _open(hit.FullPath, hit.Line);
    }
}
