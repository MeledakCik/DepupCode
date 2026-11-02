using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DepupCode.Models;
using DepupCode.Services;

namespace DepupCode.Views;

public sealed class HistoryItem
{
    public HistoryItem(ProjectRecord record, int build)
    {
        Record = record;
        Build = build;
    }

    public ProjectRecord Record { get; }
    public int Build { get; }
    public string Name => Record.Name;
    public string Platform => Record.Platform;
    public string Status => Record.IsBuilding ? "Building" : Record.Status;
    public Brush StatusBrush
    {
        get
        {
            var hex = Record.IsBuilding ? "#FBBF24" : Record.IsLocalOnly ? "#9490B8" : "#34D399";
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
            brush.Freeze();
            return brush;
        }
    }
    public string Age => Record.RelativeUpdatedText;
}

/// <summary>Run &amp; Deploy sidebar panel. The live pipeline is rendered by <see cref="Monitor"/> in the main area.</summary>
public partial class DeployView : UserControl
{
    private readonly SettingsService _settings;
    private readonly LocalDataStore _store;
    private readonly DeployService _deploy = new();
    private readonly PlatformService _platforms;
    private readonly DepupCloudService _cloud = DepupCloudService.Shared;
    private readonly Action<string> _navigate;
    private readonly Action<string> _log;
    private RepoAnalysis? _analysis;
    private string? _rootFolder;
    private bool _suppressHistory;

    public DeployMonitorView Monitor { get; } = new();

    public DeployView(SettingsService settings, LocalDataStore store, Action<string> navigate, Action<string> log)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _navigate = navigate;
        _log = log;
        _platforms = new PlatformService(settings, null, store);
        DepupCloudRouteCheckBox.IsChecked = !string.IsNullOrWhiteSpace(settings.GetDepupSessionCookie());
        foreach (var radio in new[] { DepupCloudRadio, RailwayRadio, VercelRadio, CloudflareRadio })
            radio.Checked += (_, _) => Monitor.SetPlatform(SelectedPlatform());
        Monitor.SetPlatform(SelectedPlatform());
        DeployProgress.ValueChanged += (_, _) => Monitor.SetProgress((int)DeployProgress.Value, ProgressMessage.Text);
        RefreshHistory();
    }

    public void SetRoot(string? folder)
    {
        _rootFolder = folder;
        if (!string.IsNullOrWhiteSpace(RepositoryUrlBox.Text)) return;
        var origin = GitService.ReadOriginUrl(folder);
        if (origin is not null) RepositoryUrlBox.Text = origin;
        else if (!string.IsNullOrWhiteSpace(folder))
            ProjectNameBox.Text = Path.GetFileName(folder.TrimEnd('\\', '/'));
    }

    private string SelectedPlatform() =>
        new[] { DepupCloudRadio, RailwayRadio, VercelRadio, CloudflareRadio }
            .FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "Depup Edge CDN";

    public void RefreshHistory()
    {
        var projects = _store.LoadProjects().OrderBy(p => p.UpdatedAt).ToList();
        var items = projects.Select((p, i) => new HistoryItem(p, i + 1)).Reverse().ToList();
        _suppressHistory = true;
        HistoryList.ItemsSource = items;
        _suppressHistory = false;
        HistoryTotal.Text = "TOTAL: " + items.Count;
        HistoryEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void History_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressHistory) return;
        if (HistoryList.SelectedItem is HistoryItem item) Monitor.ShowRecord(item.Record, item.Build);
    }

    private void Env_Click(object sender, RoutedEventArgs e) => _navigate("environment");

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        AnalysisCard.Visibility = Visibility.Collapsed;
        DeployButton.IsEnabled = false;
        ApplyLocalFixesButton.Visibility = Visibility.Collapsed;
        ProgressMessage.Text = "Membaca struktur project dan memeriksa kompatibilitas…";
        try
        {
            var url = RepositoryUrlBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(url) && url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var githubToken = _settings.GetToken("github");
                if (string.IsNullOrWhiteSpace(githubToken) && DepupCloudRouteCheckBox.IsChecked == true)
                {
                    // Repo private tanpa token lokal: coba token GitHub yang tersimpan di akun Depup Cloud.
                    try { githubToken = await _cloud.GetProviderTokenAsync("github"); }
                    catch { githubToken = ""; }
                }
                _analysis = await _deploy.AnalyzeGitHubAsync(url, githubToken);
            }
            else if (!string.IsNullOrWhiteSpace(url) && Directory.Exists(url))
            {
                _analysis = _deploy.AnalyzeDirectory(url);
                _rootFolder = url;
            }
            else if (!string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder))
            {
                _analysis = _deploy.AnalyzeDirectory(_rootFolder);
            }
            else
            {
                throw new InvalidOperationException("Masukkan URL repository GitHub atau buka folder workspace sebelum memeriksa project.");
            }

            if (string.IsNullOrWhiteSpace(ProjectNameBox.Text)) ProjectNameBox.Text = _analysis.Repository;
            AnalysisSummary.Text = _analysis.Summary;
            AnalysisFiles.Text = $"Platform: {string.Join(", ", _analysis.SupportedPlatforms)}  •  Branch: {_analysis.Branch}";
            FixesList.ItemsSource = _analysis.Fixes;
            NoFixesText.Visibility = _analysis.Fixes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ApplyLocalFixesButton.Visibility = _analysis.Fixes.Count > 0 && !string.IsNullOrWhiteSpace(_rootFolder) && Directory.Exists(_rootFolder)
                ? Visibility.Visible
                : Visibility.Collapsed;
            AnalysisCard.Visibility = Visibility.Visible;
            DeployButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
            ProgressMessage.Text = _analysis.Fixes.Count == 0
                ? "Project siap dideploy ke Depup Cloud / provider."
                : $"{_analysis.Fixes.Count} pelengkap otomatis tersedia.";
            Monitor.ShowAnalysis(_analysis, SelectedPlatform());
            _log("Compatibility check: " + _analysis.Summary);
        }
        catch (Exception ex)
        {
            ProgressMessage.Text = "Pemeriksaan gagal: " + ex.Message;
            _log("Compatibility check gagal: " + ex.Message);
        }
        finally { CheckButton.IsEnabled = true; }
    }

    private void ApplyLocalFixes_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null || string.IsNullOrWhiteSpace(_rootFolder) || !Directory.Exists(_rootFolder)) return;
        try
        {
            _deploy.ApplyFixesLocally(_analysis, _rootFolder);
            ProgressMessage.Text = $"{_analysis.Fixes.Count} file pelengkap berhasil ditulis ke {_rootFolder}.";
            ApplyLocalFixesButton.Visibility = Visibility.Collapsed;
            _log("Pelengkap file diterapkan ke folder lokal: " + _rootFolder);
        }
        catch (Exception ex)
        {
            ProgressMessage.Text = "Gagal menulis file pelengkap: " + ex.Message;
        }
    }

    private async void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (_analysis is null) return;
        var platformName = SelectedPlatform();
        var platform = platformName switch
        {
            "Depup Edge CDN" => DeployPlatform.DepupCloud,
            "Cloudflare Pages" => DeployPlatform.CloudflarePages,
            "Railway" => DeployPlatform.Railway,
            _ => DeployPlatform.Vercel
        };
        if (!_analysis.SupportedPlatforms.Contains(platformName, StringComparer.OrdinalIgnoreCase) &&
            platform != DeployPlatform.DepupCloud)
        {
            ProgressMessage.Text = $"{_analysis.Framework} tidak cocok untuk {platformName}. Pilih: {string.Join(", ", _analysis.SupportedPlatforms)}.";
            return;
        }
        var projectName = string.IsNullOrWhiteSpace(ProjectNameBox.Text) ? _analysis.Repository : ProjectNameBox.Text.Trim();
        DeployButton.IsEnabled = false;
        DeployProgress.Value = 0;
        ResultLink.Text = "";
        Monitor.ShowAnalysis(_analysis, platformName);
        Monitor.Begin(_store.LoadProjects().Count + 1, platformName);
        _log($"Deploy {projectName} → {platformName} dimulai");
        var reporter = new Progress<(int Percent, string Message)>(p =>
        {
            ProgressMessage.Text = p.Message;
            DeployProgress.Value = p.Percent;
            _log($"[{p.Percent}%] {p.Message}");
        });
        try
        {
            // Rute akun Depup Cloud hanya untuk provider eksternal dan butuh repository GitHub;
            // selain itu tetap memakai alur langsung Argon (token lokal / Depup Edge).
            var useCloudRoute = DepupCloudRouteCheckBox.IsChecked == true &&
                                platform != DeployPlatform.DepupCloud &&
                                !string.IsNullOrWhiteSpace(_analysis.RepositoryUrl);
            DeploymentResult result;
            if (useCloudRoute)
            {
                var user = await _cloud.GetSessionUserAsync();
                if (user is null)
                    throw new InvalidOperationException("Masuk ke Depup Cloud lewat Settings sebelum memakai rute cloud, atau matikan opsi rute cloud.");
                result = await _cloud.DeployAsync(_analysis, platformName, projectName,
                    _store.LoadEnvironment(_analysis.RepositoryUrl), reporter);
            }
            else result = await _platforms.DeployAsync(_analysis, platform, projectName, reporter);
            var normalizedStatus = result.Status.Trim().ToUpperInvariant();
            var failed = normalizedStatus is "ERROR" or "FAILED" or "CANCELED" or "CANCELLED";
            var succeeded = normalizedStatus is "READY" or "DEPLOYED" or "ACTIVE" or "AKTIF" or "SUCCESS" or "SUCCEEDED";
            var stillBuilding = !failed && !succeeded;
            ResultLink.Text = result.Url;
            ProgressMessage.Text = result.Message;
            _store.UpsertProject(new ProjectRecord
            {
                Name = projectName,
                LocalPath = _rootFolder ?? "",
                RepositoryUrl = _analysis.RepositoryUrl,
                Branch = _analysis.Branch,
                Framework = _analysis.Framework,
                Platform = platformName == "Railway" ? "Railway (Singapore)" : platformName,
                PlatformSub = platform == DeployPlatform.DepupCloud
                    ? result.Status.Equals("Aktif", StringComparison.OrdinalIgnoreCase) ? "Link Instan" : "Via akun Depup Cloud"
                    : useCloudRoute ? "Via akun Depup Cloud" : "Provider langsung",
                SiteUrl = result.Url,
                ProviderProjectId = result.ProviderProjectId,
                ProviderServiceId = result.ProviderServiceId,
                ProviderEnvironmentId = result.ProviderEnvironmentId,
                Status = failed ? "Deploy gagal" : stillBuilding ? "Building" : result.Status,
                BuildProgress = stillBuilding ? 90 : failed ? 0 : 100,
                IconKind = "bolt",
                UpdatedAt = DateTime.UtcNow
            });
            if (failed) Monitor.Complete(false, result.Url, result.Message);
            else if (stillBuilding) Monitor.StillBuilding(result.Url, result.Message);
            else Monitor.Complete(true, result.Url, result.Message);
            _log(failed ? "Deploy melaporkan kegagalan: " + result.Status : "Deploy selesai: " + result.Url);
            RefreshHistory();
        }
        catch (Exception ex)
        {
            ProgressMessage.Text = "Deploy gagal: " + ex.Message;
            Monitor.Complete(false, "", "Deploy gagal: " + ex.Message);
            _log("Deploy gagal: " + ex.Message);
        }
        finally
        {
            DeployButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
        }
    }

    private void OpenResult(object sender, MouseButtonEventArgs e)
    {
        if (Uri.TryCreate(ResultLink.Text, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
