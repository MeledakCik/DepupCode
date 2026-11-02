using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DepupCode.Controls;

/// <summary>
/// Compact code overview for the editor. Lines are scaled to the available height and
/// the highlighted range follows the editor scroll position.
/// </summary>
public sealed class EditorMinimap : FrameworkElement
{
    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(15, 10, 28)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(167, 139, 250)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CommentBrushProperty = DependencyProperty.Register(
        nameof(CommentBrush), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(110, 150, 115)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StringBrushProperty = DependencyProperty.Register(
        nameof(StringBrush), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(220, 155, 120)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TypeBrushProperty = DependencyProperty.Register(
        nameof(TypeBrush), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(120, 185, 220)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewportBrushProperty = DependencyProperty.Register(
        nameof(ViewportBrush), typeof(Brush), typeof(EditorMinimap),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(139, 92, 246)), FrameworkPropertyMetadataOptions.AffectsRender));

    private string[] _lines = [""];
    private string _text = "";
    private double _scrollPosition;
    private double _viewportSize = 0.1;
    private int _currentLine = 1;

    public Brush Background { get => (Brush)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush CommentBrush { get => (Brush)GetValue(CommentBrushProperty); set => SetValue(CommentBrushProperty, value); }
    public Brush StringBrush { get => (Brush)GetValue(StringBrushProperty); set => SetValue(StringBrushProperty, value); }
    public Brush TypeBrush { get => (Brush)GetValue(TypeBrushProperty); set => SetValue(TypeBrushProperty, value); }
    public Brush ViewportBrush { get => (Brush)GetValue(ViewportBrushProperty); set => SetValue(ViewportBrushProperty, value); }

    public event Action<double>? SeekRequested;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value ?? "";
            _lines = _text.Replace("\r\n", "\n").Split('\n');
            InvalidateVisual();
        }
    }

    public int CurrentLine
    {
        get => _currentLine;
        set { if (_currentLine == value) return; _currentLine = Math.Max(1, value); InvalidateVisual(); }
    }

    public double ScrollPosition
    {
        get => _scrollPosition;
        set { _scrollPosition = Math.Clamp(value, 0, 1); InvalidateVisual(); }
    }

    public double ViewportSize
    {
        get => _viewportSize;
        set { _viewportSize = Math.Clamp(value, 0.015, 1); InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        drawingContext.DrawRectangle(Background, null, new Rect(0, 0, width, height));
        var lineCount = Math.Max(1, _lines.Length);
        var rowHeight = height / lineCount;
        var sampleCount = Math.Min(lineCount, Math.Max(1, (int)Math.Ceiling(height * 1.5)));
        var stride = Math.Max(1, (int)Math.Ceiling((double)lineCount / sampleCount));
        var barHeight = Math.Max(0.65, Math.Min(2.1, rowHeight * 0.74));

        for (var index = 0; index < lineCount; index += stride)
        {
            var line = _lines[index];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var trimmed = line.TrimStart();
            var indent = line.Length - trimmed.Length;
            var left = Math.Min(width - 2, 2 + indent * 0.38);
            var contentWidth = Math.Min(width - left - 2, Math.Max(3, trimmed.Length * 0.52));
            if (contentWidth <= 0) continue;

            var brush = trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal)
                ? CommentBrush
                : trimmed.Contains('\"') || trimmed.Contains('\'')
                    ? StringBrush
                    : trimmed.Contains("class ", StringComparison.Ordinal) || trimmed.Contains("function ", StringComparison.Ordinal) || trimmed.Contains("public ", StringComparison.Ordinal)
                        ? TypeBrush
                        : Foreground;
            var y = index * rowHeight;
            drawingContext.DrawRoundedRectangle(brush, null,
                new Rect(left, y, contentWidth, Math.Min(barHeight, height - y)), 0.6, 0.6);
        }

        var viewportHeight = Math.Max(8, height * _viewportSize);
        var viewportTop = Math.Clamp(height * _scrollPosition, 0, Math.Max(0, height - viewportHeight));
        var accent = ViewportBrush is SolidColorBrush solid ? solid.Color : Color.FromRgb(139, 92, 246);
        var fill = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B));
        var pen = new Pen(ViewportBrush, 1);
        drawingContext.DrawRoundedRectangle(fill, pen, new Rect(0.5, viewportTop, Math.Max(0, width - 1), viewportHeight), 2, 2);

        var caretY = Math.Clamp((_currentLine - 1) * rowHeight, 0, Math.Max(0, height - 1));
        drawingContext.DrawRectangle(ViewportBrush, null, new Rect(0, caretY, Math.Min(4, width), Math.Max(1, Math.Min(1.4, rowHeight))));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (ActualHeight <= 0 || _lines.Length == 0) return;
        var point = e.GetPosition(this);
        var ratio = Math.Clamp(point.Y / ActualHeight, 0, 1);
        SeekRequested?.Invoke(ratio);
        e.Handled = true;
    }
}
