using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DepupCode.Views;

public sealed class PaletteItem
{
    public PaletteItem(string title, string subtitle, string glyph, string color, string shortcut, Action action)
    {
        Title = title;
        Subtitle = subtitle;
        Glyph = glyph;
        Color = color;
        Shortcut = shortcut;
        Action = action;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Glyph { get; }
    public string Color { get; }
    public string Shortcut { get; }
    public Action Action { get; }
    public Brush GlyphBrush => Frozen(Color);

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public partial class CommandPaletteWindow : Window
{
    private readonly List<PaletteItem> _items;
    private readonly List<PaletteItem> _filtered = [];
    private bool _closing;

    public CommandPaletteWindow(string mode, string placeholder, IEnumerable<PaletteItem> items, string initial = "")
    {
        InitializeComponent();
        ModeLabel.Text = mode;
        _items = items.ToList();
        QueryBox.Text = initial;
        QueryBox.ToolTip = placeholder;
        ApplyFilter();
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            QueryBox.CaretIndex = QueryBox.Text.Length;
            if (Results.Items.Count > 0) Results.SelectedIndex = 0;
        };
    }

    public static void Show(Window owner, string mode, string placeholder, IEnumerable<PaletteItem> items, string initial = "")
    {
        var palette = new CommandPaletteWindow(mode, placeholder, items, initial) { Owner = owner };
        palette.ShowDialog();
    }

    private void ApplyFilter()
    {
        var query = QueryBox.Text.Trim();
        _filtered.Clear();
        if (query.Length == 0) _filtered.AddRange(_items);
        else
        {
            foreach (var item in _items)
            {
                var score = Score(item, query);
                if (score >= 0) _filtered.Add(item);
            }
            _filtered.Sort((a, b) => Score(b, query).CompareTo(Score(a, query)));
        }
        Results.ItemsSource = null;
        Results.ItemsSource = _filtered.Take(200).ToList();
        CountText.Text = $"{_filtered.Count} hasil";
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private static int Score(PaletteItem item, string query)
    {
        var title = item.Title;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1000 - title.Length;
        var index = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0) return 700 - index;
        if (item.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase)) return 300;
        // subsequence match
        var ti = 0;
        var score = 0;
        foreach (var c in title)
        {
            if (ti < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[ti]))
            {
                ti++;
                score += 5;
            }
        }
        return ti == query.Length ? score : -1;
    }

    private void Query_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            e.Handled = true;
            if (Results.Items.Count > 0) Results.SelectedIndex = Math.Min(Results.Items.Count - 1, Results.SelectedIndex + 1);
            Results.ScrollIntoView(Results.SelectedItem);
        }
        else if (e.Key == Key.Up)
        {
            e.Handled = true;
            if (Results.Items.Count > 0) Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 1);
            Results.ScrollIntoView(Results.SelectedItem);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OpenSelected();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _closing = true;
            Close();
        }
        else if (e.Key == Key.PageDown)
        {
            e.Handled = true;
            if (Results.Items.Count > 0) Results.SelectedIndex = Math.Min(Results.Items.Count - 1, Results.SelectedIndex + 8);
        }
        else if (e.Key == Key.PageUp)
        {
            e.Handled = true;
            if (Results.Items.Count > 0) Results.SelectedIndex = Math.Max(0, Results.SelectedIndex - 8);
        }
    }

    private void OpenSelected()
    {
        if (Results.SelectedItem is not PaletteItem item) return;
        _closing = true;
        Close();
        Dispatcher.BeginInvoke(item.Action);
    }

    private void Results_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (!_closing) Close();
    }
}
