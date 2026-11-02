using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DepupCode.Models;
using DepupCode.Services;

namespace DepupCode.Views;

public sealed record ParamRow(string Key, string Value);

/// <summary>Main-area pipeline monitor shown while the Deploy sidebar panel is active.</summary>
public partial class DeployMonitorView : UserControl
{
    private static readonly (string Tag, string Title)[] Stages =
    {
        ("01 Checkout", "Source Checkout"), ("02 Deps", "Dependencies"), ("03 Checks", "Compatibility & Lint"),
        ("04 Bundle", "Build & Bundle"), ("05 Deploy", "Edge Deployment")
    };

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _url = "";
    private int _percent;
    private bool _running;
    private bool _failed;

    public DeployMonitorView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateElapsed();
        UpdateStages();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => { if (_running) _timer.Start(); };
    }

    private static Brush B(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    private void Monitor_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < 980;
        MetricGrid.Columns = narrow ? 2 : 4;
        Grid.SetColumn(ArtifactsCard, narrow ? 0 : 1);
        Grid.SetRow(ArtifactsCard, narrow ? 1 : 0);
        if (narrow && DetailGrid.RowDefinitions.Count == 0)
        {
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            DetailGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        DetailGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    public void ShowAnalysis(RepoAnalysis analysis, string platform)
    {
        GitChip.Text = $"git:{analysis.Branch}@{analysis.Repository}";
        PlatformText.Text = platform;
        PlatformSub.Text = analysis.SupportedPlatforms.Count == 0 ? "Tidak ada platform cocok" : "Didukung: " + string.Join(", ", analysis.SupportedPlatforms);
        ParamList.ItemsSource = new List<ParamRow>
        {
            new("Repository", $"{analysis.Owner}/{analysis.Repository}"),
            new("Branch", analysis.Branch),
            new("Framework", analysis.Framework),
            new("Project Type", analysis.Kind.ToString()),
            new("Root Directory", string.IsNullOrWhiteSpace(analysis.RootDirectory) ? "/" : analysis.RootDirectory),
            new("Output Target", string.IsNullOrWhiteSpace(analysis.StaticDirectory) ? "Server / default" : analysis.StaticDirectory),
            new("Files Scanned", analysis.Files.Count.ToString())
        };
        ArtifactList.ItemsSource = analysis.Fixes;
        ArtifactEmpty.Text = "Tidak ada file konfigurasi tambahan yang diperlukan.";
        ArtifactEmpty.Visibility = analysis.Fixes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StageMessage.Text = analysis.Summary;
    }

    public void SetPlatform(string platform)
    {
        PlatformText.Text = platform;
    }

    public void Begin(int build, string platform)
    {
        BuildNumber.Text = " #" + build;
        PlatformText.Text = platform;
        _url = "";
        UrlText.Text = "—";
        UrlSub.Text = "Menunggu hasil deploy";
        OpenUrlButton.IsEnabled = false;
        CloudControlGate.Refresh(this);
        _running = true;
        _failed = false;
        _percent = 0;
        MiniProgress.Value = 0;
        PercentText.Text = "0%";
        _clock.Restart();
        _timer.Start();
        UpdateElapsed();
        SetState();
        UpdateStages();
    }

    public void SetProgress(int percent, string message)
    {
        _percent = Math.Clamp(percent, 0, 100);
        MiniProgress.Value = _percent;
        PercentText.Text = _percent + "%";
        StageMessage.Text = message;
        UpdateStages();
    }

    public void StillBuilding(string url, string message)
    {
        _running = true;
        _failed = false;
        _percent = 90;
        _url = url;
        MiniProgress.Value = _percent;
        PercentText.Text = _percent + "%";
        UrlText.Text = string.IsNullOrWhiteSpace(url) ? "—" : url;
        UrlSub.Text = "Build masih berjalan di provider";
        OpenUrlButton.IsEnabled = Uri.TryCreate(url, UriKind.Absolute, out _);
        CloudControlGate.Refresh(this);
        StageMessage.Text = message;
        _clock.Restart();
        _timer.Start();
        SetState();
        UpdateStages();
    }

    public void Complete(bool success, string url, string message)
    {
        _running = false;
        _failed = !success;
        _clock.Stop();
        _timer.Stop();
        UpdateElapsed();
        StageMessage.Text = message;
        if (success)
        {
            _percent = 100;
            MiniProgress.Value = 100;
            PercentText.Text = "100%";
            _url = url;
            UrlText.Text = string.IsNullOrWhiteSpace(url) ? "—" : url;
            UrlSub.Text = string.IsNullOrWhiteSpace(url) ? "URL belum tersedia" : "✓ Terkirim ke provider";
            OpenUrlButton.IsEnabled = Uri.TryCreate(url, UriKind.Absolute, out _);
            CloudControlGate.Refresh(this);
        }
        SetState();
        UpdateStages();
    }

    /// <summary>Shows a saved deployment from the history list.</summary>
    public void ShowRecord(ProjectRecord record, int build)
    {
        _running = false;
        _failed = false;
        _clock.Reset();
        _timer.Stop();
        BuildNumber.Text = " #" + build;
        GitChip.Text = record.Name;
        PlatformText.Text = record.Platform;
        PlatformSub.Text = record.RepositoryUrl;
        _url = record.SiteUrl;
        UrlText.Text = string.IsNullOrWhiteSpace(record.SiteUrl) ? "—" : record.SiteUrl;
        UrlSub.Text = record.Status;
        OpenUrlButton.IsEnabled = Uri.TryCreate(record.SiteUrl, UriKind.Absolute, out _);
        CloudControlGate.Refresh(this);
        ElapsedText.Text = record.UpdatedAt.ToLocalTime().ToString("dd MMM HH:mm");
        ElapsedSub.Text = "Terakhir diperbarui";
        ElapsedSub.Foreground = B("#9490B8");
        _percent = record.Status.Equals("Deploying", StringComparison.OrdinalIgnoreCase) ? 80 : 100;
        MiniProgress.Value = _percent;
        PercentText.Text = _percent + "%";
        ParamList.ItemsSource = new List<ParamRow>
        {
            new("Project", record.Name), new("Platform", record.Platform), new("Status", record.Status),
            new("Repository", string.IsNullOrWhiteSpace(record.RepositoryUrl) ? "-" : record.RepositoryUrl),
            new("Provider Project", string.IsNullOrWhiteSpace(record.ProviderProjectId) ? "-" : record.ProviderProjectId)
        };
        ArtifactList.ItemsSource = null;
        ArtifactEmpty.Text = "Artefak hanya tampil untuk deploy yang sedang dijalankan.";
        ArtifactEmpty.Visibility = Visibility.Visible;
        StageMessage.Text = $"{record.Name} · {record.Platform} · {record.Status}";
        SetState();
        UpdateStages();
    }

    private void SetState()
    {
        StateDot.Fill = B(_failed ? "#F87171" : _running ? "#FBBF24" : _percent >= 100 ? "#10B981" : "#6C6794");
        ElapsedSub.Text = _failed ? "Gagal" : _running ? "Running…" : _percent >= 100 ? "Selesai" : "Idle";
        ElapsedSub.Foreground = B(_failed ? "#F87171" : _running ? "#FBBF24" : "#34D399");
    }

    private void UpdateElapsed()
    {
        var t = _clock.Elapsed;
        ElapsedText.Text = $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
    }

    private void UpdateStages()
    {
        StageGrid.Children.Clear();
        var mono = new FontFamily("JetBrains Mono, Cascadia Code, Consolas");
        var activeIndex = -1;
        for (var i = 0; i < Stages.Length; i++)
        {
            var done = _percent >= (i + 1) * 20;
            var active = !done && _percent > i * 20 && !_failed;
            var failed = _failed && !done && _percent > i * 20;
            if (active) activeIndex = i;
            var card = new Border
            {
                CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(active ? 2 : 1), Padding = new Thickness(12), Margin = new Thickness(4, 0, 4, 0),
                Background = B(done ? "#0F2A22" : failed ? "#2A1018" : active ? "#1C1736" : "#0E0C1A"),
                BorderBrush = B(done ? "#1E5A45" : failed ? "#7F1D1D" : active ? "#8B5CF6" : "#2A224F"),
                Opacity = done || active || failed ? 1 : 0.6
            };
            var panel = new StackPanel();
            var top = new DockPanel();
            var mark = new TextBlock { Text = done ? "✓" : failed ? "✕" : active ? "⟳" : "○", Foreground = B(done ? "#34D399" : failed ? "#F87171" : "#A78BFA"), FontWeight = FontWeights.Bold, FontSize = 14 };
            DockPanel.SetDock(mark, Dock.Right);
            top.Children.Add(mark);
            top.Children.Add(new Border { Background = B("#1C1736"), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = Stages[i].Tag, FontFamily = mono, FontSize = 10, Foreground = B(done ? "#34D399" : "#A78BFA") } });
            panel.Children.Add(top);
            panel.Children.Add(new TextBlock { Text = Stages[i].Title, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = done ? "Selesai" : failed ? "Gagal" : active ? "Sedang berjalan" : "Menunggu", FontFamily = mono, FontSize = 10, Foreground = B("#9490B8") });
            card.Child = panel;
            StageGrid.Children.Add(card);
        }
        StagePillText.Text = _failed ? "Failed" : _percent >= 100 ? "Completed" : activeIndex >= 0 ? $"Stage {activeIndex + 1} of {Stages.Length} Active" : "Idle";
    }

    private void OpenUrl_Click(object sender, RoutedEventArgs e) => OpenUrl();
    private void UrlText_MouseDown(object sender, MouseButtonEventArgs e) => OpenUrl();

    private void OpenUrl()
    {
        if (Uri.TryCreate(_url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
