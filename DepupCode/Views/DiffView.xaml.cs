using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DepupCode.Services;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace DepupCode.Views;

/// <summary>Side-by-side HEAD vs working tree comparison.</summary>
public partial class DiffView : UserControl
{
    private readonly Action _close;
    private bool _syncing;

    public DiffView(string? folder, GitChange change, Action close)
    {
        InitializeComponent();
        _close = close;
        FileTitle.Text = change.FileName;
        FileTag.Text = change.Tag;
        PathText.Text = change.RelativePath;
        ModeBadge.Text = change.Status == "U" ? "New file" : change.Status == "D" ? "Deleted" : "Working Tree";

        var oldText = (change.Status is "U" or "A" ? null : GitService.ReadHeadText(folder, change.RelativePath)) ?? "";
        var newText = "";
        try { if (change.Status != "D" && File.Exists(change.FullPath)) newText = File.ReadAllText(change.FullPath); }
        catch (Exception ex) { newText = "// Tidak dapat membaca file: " + ex.Message; }

        var model = new SideBySideDiffBuilder(new Differ()).BuildDiffModel(oldText, newText);
        var left = model.OldText.Lines;
        var right = model.NewText.Lines;

        LeftEditor.Text = string.Join("\n", left.Select(l => l.Text ?? ""));
        RightEditor.Text = string.Join("\n", right.Select(l => l.Text ?? ""));
        LeftEditor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(left.Select(l => l.Type).ToList(), isLeft: true));
        RightEditor.TextArea.TextView.BackgroundRenderers.Add(new DiffBackground(right.Select(l => l.Type).ToList(), isLeft: false));

        var extension = Path.GetExtension(change.RelativePath).ToLowerInvariant();
        var highlight = extension is ".ts" or ".tsx" ? ".js" : extension;
        var definition = HighlightingManager.Instance.GetDefinitionByExtension(highlight);
        LeftEditor.SyntaxHighlighting = definition;
        RightEditor.SyntaxHighlighting = definition;

        var removed = left.Count(l => l.Type is ChangeType.Deleted or ChangeType.Modified);
        var added = right.Count(l => l.Type is ChangeType.Inserted or ChangeType.Modified);
        SummaryText.Text = $"+{added}  −{removed}";
        LeftSub.Text = change.Status is "U" or "A" ? "(belum ada di HEAD)" : change.RelativePath;

        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(RightEditor, LeftEditor);
    }

    private void Sync(ICSharpCode.AvalonEdit.TextEditor from, ICSharpCode.AvalonEdit.TextEditor to)
    {
        if (_syncing) return;
        _syncing = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        to.ScrollToHorizontalOffset(from.HorizontalOffset);
        _syncing = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => _close();

    private sealed class DiffBackground : IBackgroundRenderer
    {
        private readonly List<ChangeType> _types;
        private readonly Brush _changed;
        private readonly Brush _padding = Frozen(Color.FromArgb(40, 148, 144, 184));

        public DiffBackground(List<ChangeType> types, bool isLeft)
        {
            _types = types;
            _changed = Frozen(isLeft ? Color.FromArgb(110, 127, 29, 29) : Color.FromArgb(105, 6, 95, 70));
        }

        private static Brush Frozen(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (!textView.VisualLinesValid) return;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index < 0 || index >= _types.Count) continue;
                var brush = _types[index] switch
                {
                    ChangeType.Deleted or ChangeType.Inserted or ChangeType.Modified => _changed,
                    ChangeType.Imaginary => _padding,
                    _ => null
                };
                if (brush is null) continue;
                var y = visual.VisualTop - textView.VerticalOffset;
                drawingContext.DrawRectangle(brush, null, new Rect(0, y, textView.ActualWidth, visual.Height));
            }
        }
    }
}
