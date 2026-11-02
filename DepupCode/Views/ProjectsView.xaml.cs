using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DepupCode.Models;
using DepupCode.Services;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace DepupCode.Views;

public partial class ProjectsView : UserControl
{
    private readonly LocalDataStore _store;
    private readonly SettingsService _settings;
    private readonly PlatformService _platforms;
    private readonly DeployService _deploy = new();
    private readonly ObservableCollection<ProjectRecord> _projects = [];
    private readonly Action<string>? _openWorkspaceFolder;
    private readonly Action<string>? _navigateTo;
    private readonly Action? _projectsUpdated;
    private ICollectionView? _projectsView;
    private string _activeFilterTag = "ALL";
    private string? _currentFolder;

    public ProjectsView() : this(new SettingsService(), new LocalDataStore()) { }

    public ProjectsView(SettingsService settings, LocalDataStore store,
        string? currentFolder = null,
        Action<string>? openWorkspaceFolder = null,
        Action<string>? navigateTo = null,
        Action? projectsUpdated = null)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
        _currentFolder = currentFolder;
        _openWorkspaceFolder = openWorkspaceFolder;
        _navigateTo = navigateTo;
        _projectsUpdated = projectsUpdated;
        _platforms = new PlatformService(settings, null, store);

        DefaultPathText.Text = _settings.Load().DefaultProjectsPath;
        RefreshTunnelHeader();
        DepupCloudService.Shared.TunnelStateChanged += OnTunnelStateChanged;
        Unloaded += (_, _) => DepupCloudService.Shared.TunnelStateChanged -= OnTunnelStateChanged;

        ReloadCatalog();
    }

    private void OnTunnelStateChanged(object? sender, DepupTunnelSession? session) =>
        Dispatcher.BeginInvoke(RefreshTunnelHeader);

    private void RefreshTunnelHeader()
    {
        var active = DepupCloudService.Shared.IsTunnelActive;
        TunnelHeaderStatusText.Text = active ? "Tunnel Aktif" : "Siap (Klik untuk Buka)";
        TunnelHeaderStatusText.Foreground = MakeBrush(active ? "#34D399" : "#A78BFA");
        TunnelHeaderDot.Fill = MakeBrush(active ? "#34D399" : "#10B981");
    }

    public void SetCurrentFolder(string? folder)
    {
        _currentFolder = folder;
        ReloadCatalog();
    }

    public void ReloadCatalog()
    {
        _projects.Clear();
        var loaded = _store.LoadProjects();
        var activeName = string.IsNullOrWhiteSpace(_currentFolder)
            ? ""
            : Path.GetFileName(_currentFolder.TrimEnd('\\', '/'));

        foreach (var project in loaded)
        {
            project.IsCurrentWorkspace =
                (!string.IsNullOrWhiteSpace(_currentFolder) &&
                 string.Equals(project.LocalPath, _currentFolder, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(project.Name, activeName, StringComparison.OrdinalIgnoreCase);
            _projects.Add(project);
        }

        _projectsView = CollectionViewSource.GetDefaultView(_projects);
        _projectsView.Filter = FilterProject;
        ProjectsListControl.ItemsSource = _projectsView;
        UpdateCountsAndFooter();
        CloudControlGate.Refresh(this);
    }

    private void UpdateCountsAndFooter()
    {
        var total = _projects.Count;
        var localCount = _projects.Count(p => p.IsLocalOnly);
        var activeCount = total - localCount;

        if (PillAll is not null) PillAll.Content = $"Semua ({total})";
        if (PillActive is not null) PillActive.Content = $"Aktif ({activeCount})";
        if (PillLocal is not null) PillLocal.Content = $"Hanya Lokal ({localCount})";

        var visible = _projectsView?.Cast<object>().Count() ?? total;
        if (StatusText is not null)
            StatusText.Text = _settings.Load().IsOfflineMode
                ? $"Mode Offline — menampilkan {visible} dari {total} project cache lokal. Sinkronisasi, deploy, dan upload dijeda."
                : $"Menampilkan {visible} dari {total} project tersimpan di perangkat ini.";

        if (BreadcrumbFilterTitle is not null)
        {
            BreadcrumbFilterTitle.Text = _activeFilterTag switch
            {
                "ACTIVE" => $"Project Aktif ({activeCount})",
                "RAILWAY" => "Railway Cloud",
                "DEPUP" => "Depup Edge CDN",
                "LOCAL" => $"Hanya Lokal ({localCount})",
                _ => $"Semua Project ({total})"
            };
        }
    }

    private void FilterPill_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        _activeFilterTag = tag;
        _projectsView?.Refresh();
        UpdateCountsAndFooter();
    }

    private void ProjectFilter_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(ProjectSearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _projectsView?.Refresh();
        UpdateCountsAndFooter();
    }

    private bool FilterProject(object item)
    {
        if (item is not ProjectRecord project) return false;
        var query = ProjectSearchBox?.Text.Trim();
        if (!string.IsNullOrEmpty(query) &&
            !project.Name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !project.Framework.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !project.Branch.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !project.Platform.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !project.Status.Contains(query, StringComparison.OrdinalIgnoreCase) &&
            !project.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            return false;

        return _activeFilterTag switch
        {
            "ACTIVE" => !project.IsLocalOnly,
            "RAILWAY" => project.Platform.Contains("Railway", StringComparison.OrdinalIgnoreCase),
            "DEPUP" => project.Platform.Contains("Depup", StringComparison.OrdinalIgnoreCase) ||
                       project.SiteUrl.Contains("depup.cloud", StringComparison.OrdinalIgnoreCase),
            "LOCAL" => project.IsLocalOnly,
            _ => true
        };
    }

    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        await SyncCatalogAsync();
    }

    public void ShowOfflineNotice() => StatusText.Text =
        "Mode Offline — menampilkan cache perangkat. Sinkronisasi, deploy, dan upload dinonaktifkan hingga online.";

    public async Task SyncCatalogAsync()
    {
        if (_settings.Load().IsOfflineMode)
        {
            ShowOfflineNotice();
            return;
        }

        SyncPlatformButton.IsEnabled = false;
        StatusText.Text = "Memuat project dan situs terbaru dari Depup Cloud serta provider yang terhubung…";
        try
        {
            var failures = new List<string>();
            var syncedCount = 0;
            var session = await DepupCloudService.Shared.CheckSessionAsync();
            if (session.IsConnected && !string.IsNullOrWhiteSpace(session.Email))
            {
                try
                {
                    foreach (var project in await DepupCloudService.Shared.ListSitesAsync())
                    {
                        MergeProject(project);
                        syncedCount++;
                    }
                }
                catch (Exception ex) { failures.Add("Depup sites: " + ex.Message); }

                try
                {
                    foreach (var project in await DepupCloudService.Shared.ListProjectsAsync())
                    {
                        MergeProject(project);
                        syncedCount++;
                    }
                }
                catch (Exception ex) { failures.Add("Riwayat akun: " + ex.Message); }
            }
            else
            {
                failures.Add("Masuk ke akun Depup Cloud untuk memuat data akun.");
            }

            foreach (var provider in new[] { "railway", "vercel", "cloudflare" })
            {
                try
                {
                    var remote = await _platforms.ListProjectsAsync(provider);
                    foreach (var project in remote)
                    {
                        MergeProject(project);
                        syncedCount++;
                    }
                }
                catch (Exception ex) { failures.Add(provider + ": " + ex.Message); }
            }

            _store.SaveProjects(_projects);
            _projectsView?.Refresh();
            UpdateCountsAndFooter();
            _projectsUpdated?.Invoke();
            StatusText.Text = failures.Count == 0
                ? $"Selesai: {syncedCount} data cloud diperbarui. Cache lokal siap dipakai offline."
                : $"Data tersimpan di cache ({syncedCount} item diperbarui). " + string.Join(" ", failures);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Katalog lokal tetap tersedia. Sinkronisasi gagal: " + ex.Message;
        }
        finally
        {
            SyncPlatformButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
        }
    }

    private void MergeProject(ProjectRecord project)
    {
        var existing = _projects.FirstOrDefault(item =>
            item.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase) &&
            (item.Platform.Equals(project.Platform, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrWhiteSpace(project.RepositoryUrl) && item.RepositoryUrl.Equals(project.RepositoryUrl, StringComparison.OrdinalIgnoreCase))));
        if (existing is null)
        {
            _projects.Insert(0, project);
            return;
        }

        existing.Platform = project.Platform;
        existing.PlatformSub = project.PlatformSub;
        if (!string.IsNullOrWhiteSpace(project.SiteUrl)) existing.SiteUrl = project.SiteUrl;
        if (!string.IsNullOrWhiteSpace(project.Status)) existing.Status = project.Status;
        if (!string.IsNullOrWhiteSpace(project.RepositoryUrl)) existing.RepositoryUrl = project.RepositoryUrl;
        if (!string.IsNullOrWhiteSpace(project.ProviderProjectId)) existing.ProviderProjectId = project.ProviderProjectId;
        if (!string.IsNullOrWhiteSpace(project.ProviderServiceId)) existing.ProviderServiceId = project.ProviderServiceId;
        if (!string.IsNullOrWhiteSpace(project.ProviderEnvironmentId)) existing.ProviderEnvironmentId = project.ProviderEnvironmentId;
        existing.UpdatedAt = project.UpdatedAt;
    }

    private void ConnectRepository_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDarkDialog("Hubungkan Repository / Pipeline Baru", 460, 340);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "Tambahkan repository ke katalog lokal",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = "Simpan alamat repo di perangkat. Deploy dapat dimulai setelah akun/provider terhubung.",
            FontSize = 11.5,
            Foreground = MakeBrush("#9490B8"),
            Margin = new Thickness(0, 4, 0, 14)
        });

        root.Children.Add(new TextBlock { Text = "Nama Project", FontSize = 11, Foreground = MakeBrush("#C4B5FD"), Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(nameBox);

        root.Children.Add(new TextBlock { Text = "GitHub Repository URL", FontSize = 11, Foreground = MakeBrush("#C4B5FD"), Margin = new Thickness(0, 0, 0, 4) });
        var repoBox = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(repoBox);

        root.Children.Add(new TextBlock { Text = "Target Platform Cloud", FontSize = 11, Foreground = MakeBrush("#C4B5FD"), Margin = new Thickness(0, 0, 0, 4) });
        var platformCombo = new ComboBox { Height = 34, Margin = new Thickness(0, 0, 0, 16) };
        platformCombo.Items.Add("Depup Edge CDN");
        platformCombo.Items.Add("Railway (Singapore)");
        platformCombo.Items.Add("Vercel + Edge DNS");
        platformCombo.Items.Add("Cloudflare Pages");
        platformCombo.SelectedIndex = 0;
        root.Children.Add(platformCombo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancelBtn = new Button { Content = "Batal", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var saveBtn = new Button { Content = "Hubungkan", Width = 110, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        saveBtn.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(repoBox.Text)) dialog.DialogResult = true;
        };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);
        root.Children.Add(buttons);
        dialog.Content = root;

        if (dialog.ShowDialog() != true) return;
        var projName = nameBox.Text.Trim();
        var plat = platformCombo.SelectedItem?.ToString() ?? "Depup Edge CDN";
        var newRecord = new ProjectRecord
        {
            Name = projName,
            RepositoryUrl = repoBox.Text.Trim(),
            Branch = "main",
            CommitHash = "",
            Author = "Local catalog",
            Framework = "Unknown",
            Platform = plat,
            PlatformSub = "Belum terhubung ke platform",
            SiteUrl = "",
            Status = "Belum dideploy",
            BuildProgress = 0,
            IconKind = "bolt",
            UpdatedAt = DateTime.UtcNow
        };
        _store.UpsertProject(newRecord);
        ReloadCatalog();
        _projectsUpdated?.Invoke();
        StatusText.Text = $"Repository '{projName}' tersimpan di katalog lokal. Belum ada deploy yang dijalankan.";
    }

    private void ProjectTitle_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectRecord project)
            ExecutePrimaryProjectAction(project);
    }

    private void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectRecord project)
            ExecutePrimaryProjectAction(project);
    }

    private async void ExecutePrimaryProjectAction(ProjectRecord project)
    {
        if (project.IsCurrentWorkspace)
        {
            _navigateTo?.Invoke("editor");
            return;
        }

        if (project.IsLocalOnly)
        {
            await TriggerQuickDeployAsync(project);
            return;
        }

        if (!string.IsNullOrWhiteSpace(project.LocalPath) && Directory.Exists(project.LocalPath))
        {
            _openWorkspaceFolder?.Invoke(project.LocalPath);
            _navigateTo?.Invoke("editor");
            return;
        }

        if (!string.IsNullOrWhiteSpace(project.RepositoryUrl))
        {
            await CloneProjectToLocalAsync(project);
            return;
        }

        // Open folder picker to link local directory to this project
        var dialog = new OpenFolderDialog { Title = $"Pilih folder lokal untuk {project.Name}" };
        if (dialog.ShowDialog() == true)
        {
            project.LocalPath = dialog.FolderName;
            project.IsCurrentWorkspace = true;
            _store.SaveProjects(_projects);
            _openWorkspaceFolder?.Invoke(dialog.FolderName);
            _navigateTo?.Invoke("editor");
        }
    }

    private async Task CloneProjectToLocalAsync(ProjectRecord project)
    {
        var defaultBase = _settings.Load().DefaultProjectsPath;
        if (string.IsNullOrWhiteSpace(defaultBase))
            defaultBase = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projects");

        StatusText.Text = $"Meng-clone {project.RepositoryUrl} ke {defaultBase}…";
        try
        {
            var url = project.RepositoryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? project.RepositoryUrl
                : "https://" + project.RepositoryUrl;
            var clonedFolder = await GitService.CloneRepositoryAsync(url, defaultBase, _settings.GetToken("github"));
            project.LocalPath = clonedFolder;
            project.UpdatedAt = DateTime.UtcNow;
            _store.SaveProjects(_projects);
            ReloadCatalog();
            _projectsUpdated?.Invoke();
            _openWorkspaceFolder?.Invoke(clonedFolder);
            StatusText.Text = $"Clone selesai: {clonedFolder}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Clone gagal ({ex.Message}). Membuka halaman repository…";
            var url = project.RepositoryUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? project.RepositoryUrl
                : "https://" + project.RepositoryUrl;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
    }

    private async Task TriggerQuickDeployAsync(ProjectRecord project)
    {
        StatusText.Text = $"Menjalankan pipeline deployment untuk '{project.Name}' ke Depup Cloud…";
        project.Status = "Preparing deployment";
        project.BuildProgress = 0;
        _projectsView?.Refresh();

        try
        {
            RepoAnalysis analysis;
            if (!string.IsNullOrWhiteSpace(project.LocalPath) && Directory.Exists(project.LocalPath))
                analysis = _deploy.AnalyzeDirectory(project.LocalPath);
            else if (!string.IsNullOrWhiteSpace(project.RepositoryUrl) && project.RepositoryUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                analysis = await _deploy.AnalyzeGitHubAsync(
                    project.RepositoryUrl.StartsWith("http") ? project.RepositoryUrl : "https://" + project.RepositoryUrl,
                    _settings.GetToken("github"));
            else
                analysis = new RepoAnalysis { Repository = project.Name, Branch = project.Branch, Framework = project.Framework };

            var result = await _platforms.DeployAsync(analysis, DeployPlatform.DepupCloud, project.Name);
            project.SiteUrl = result.Url;
            project.Status = result.Status;
            project.BuildProgress = 100;
            project.UpdatedAt = DateTime.UtcNow;
            _store.SaveProjects(_projects);
            _projectsView?.Refresh();
            UpdateCountsAndFooter();
            _projectsUpdated?.Invoke();
            StatusText.Text = result.Message;
        }
        catch (Exception ex)
        {
            project.Status = "Deploy gagal";
            _projectsView?.Refresh();
            StatusText.Text = "Deploy gagal: " + ex.Message;
        }
    }

    private void SiteUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectRecord project || string.IsNullOrWhiteSpace(project.SiteUrl))
            return;
        var raw = project.SiteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? project.SiteUrl
            : "https://" + project.SiteUrl;
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async void Redeploy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ProjectRecord project) return;
        if (project.Platform.Contains("Railway", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(project.ProviderServiceId))
        {
            StatusText.Text = $"Redeploy {project.Name} di Railway…";
            try
            {
                var result = await _platforms.RedeployRailwayAsync(project);
                project.Status = "Building (Pipeline)";
                project.BuildProgress = 65;
                project.UpdatedAt = DateTime.UtcNow;
                _store.SaveProjects(_projects);
                _projectsView?.Refresh();
                StatusText.Text = result.Message;
                return;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Redeploy Railway gagal: " + ex.Message;
                return;
            }
        }

        await TriggerQuickDeployAsync(project);
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ProjectRecord project) return;
        var menu = new ContextMenu
        {
            Background = MakeBrush("#16132B"),
            Foreground = Brushes.White,
            BorderBrush = MakeBrush("#382F69")
        };

        var openEditorItem = new MenuItem { Header = "Buka di Editor" };
        openEditorItem.Click += (_, _) => ExecutePrimaryProjectAction(project);
        menu.Items.Add(openEditorItem);

        var deployItem = new MenuItem { Header = "⚡ Deploy ke Depup Cloud" };
        deployItem.Click += async (_, _) => await TriggerQuickDeployAsync(project);
        menu.Items.Add(deployItem);

        if (!string.IsNullOrWhiteSpace(project.SiteUrl))
        {
            var openSiteItem = new MenuItem { Header = "↗ Buka URL Production" };
            openSiteItem.Click += (_, _) =>
            {
                var raw = project.SiteUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? project.SiteUrl
                    : "https://" + project.SiteUrl;
                if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            };
            menu.Items.Add(openSiteItem);

            var copyUrlItem = new MenuItem { Header = "📋 Salin URL Deployment" };
            copyUrlItem.Click += (_, _) =>
            {
                Clipboard.SetText(project.SiteUrl);
                StatusText.Text = $"URL disalin: {project.SiteUrl}";
            };
            menu.Items.Add(copyUrlItem);
        }

        var envItem = new MenuItem { Header = "⚿ Kelola Environment Variables" };
        envItem.Click += (_, _) => _navigateTo?.Invoke("environment");
        menu.Items.Add(envItem);

        menu.Items.Add(new Separator());

        var deleteItem = new MenuItem { Header = "🗑 Hapus dari Katalog Lokal", Foreground = MakeBrush("#F87171") };
        deleteItem.Click += (_, _) =>
        {
            _projects.Remove(project);
            _store.SaveProjects(_projects);
            UpdateCountsAndFooter();
            _projectsUpdated?.Invoke();
            StatusText.Text = $"Project '{project.Name}' dihapus dari katalog.";
        };
        menu.Items.Add(deleteItem);

        btn.ContextMenu = menu;
        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    private void ChangeDefaultPath_Click(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih Lokasi Simpan Default untuk Project Baru"
        };
        if (dialog.ShowDialog() != true) return;
        var s = _settings.Load();
        s.DefaultProjectsPath = dialog.FolderName;
        _settings.Save(s);
        DefaultPathText.Text = dialog.FolderName;
        StatusText.Text = "Lokasi simpan default diperbarui: " + dialog.FolderName;
    }

    private async void CloneGitCard_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDarkDialog("Clone dari Git Remote", 460, 250);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "Clone Repository Git Remote",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = "Masukkan URL GitHub, GitLab, atau Bitbucket untuk di-clone ke lokal.",
            FontSize = 11.5,
            Foreground = MakeBrush("#9490B8"),
            Margin = new Thickness(0, 4, 0, 12)
        });
        var urlBox = new TextBox
        {
            Text = "",
            Height = 36,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        };
        root.Children.Add(urlBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Batal", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var cloneBtn = new Button { Content = "Clone & Buka", Width = 120, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        cloneBtn.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(urlBox.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(cloneBtn);
        root.Children.Add(buttons);
        dialog.Content = root;

        if (dialog.ShowDialog() != true) return;
        var repoUrl = urlBox.Text.Trim();
        var repoName = Path.GetFileNameWithoutExtension(repoUrl.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(repoName)) repoName = "cloned-repo";

        var rec = new ProjectRecord
        {
            Name = repoName,
            RepositoryUrl = repoUrl,
            Branch = "main",
            CommitHash = "",
            Author = "Git remote",
            Framework = "Git Repository",
            Platform = "Belum terhubung platform",
            PlatformSub = "Belum dideploy",
            SiteUrl = "",
            Status = "Hanya Lokal",
            IconKind = "bolt",
            UpdatedAt = DateTime.UtcNow
        };
        _store.UpsertProject(rec);
        ReloadCatalog();
        await CloneProjectToLocalAsync(rec);
    }

    private void InitTemplateCard_Click(object sender, RoutedEventArgs e)
    {
        var dialog = CreateDarkDialog("Inisialisasi Template Baru", 470, 310);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "Scaffold Project Baru (Depup Cloud Ready)",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        });
        root.Children.Add(new TextBlock
        {
            Text = "Buat project siap pakai lengkap dengan konfigurasi depup.config.json.",
            FontSize = 11.5,
            Foreground = MakeBrush("#9490B8"),
            Margin = new Thickness(0, 4, 0, 12)
        });

        root.Children.Add(new TextBlock { Text = "Nama Project Baru", FontSize = 11, Foreground = MakeBrush("#C4B5FD"), Margin = new Thickness(0, 0, 0, 4) });
        var nameBox = new TextBox { Height = 34, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        root.Children.Add(nameBox);

        root.Children.Add(new TextBlock { Text = "Pilih Stack Template", FontSize = 11, Foreground = MakeBrush("#C4B5FD"), Margin = new Thickness(0, 0, 0, 4) });
        var templateCombo = new ComboBox { Height = 34, Margin = new Thickness(0, 0, 0, 16) };
        templateCombo.Items.Add("Next.js 14 + Capacitor (Full-Stack Web)");
        templateCombo.Items.Add("Fastify + Supabase (Backend REST API)");
        templateCombo.Items.Add("Vite + React + Tailwind (Frontend SPA)");
        templateCombo.Items.Add("Python FastAPI (Cloud Microservice)");
        templateCombo.SelectedIndex = 0;
        root.Children.Add(templateCombo);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Batal", Width = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var create = new Button { Content = "Buat Template", Width = 120, Style = (Style)Application.Current.FindResource("PrimaryButton"), IsDefault = true };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(create);
        root.Children.Add(buttons);
        dialog.Content = root;

        if (dialog.ShowDialog() != true) return;
        var projName = nameBox.Text.Trim();
        var stackChoice = templateCombo.SelectedIndex;
        var baseDir = _settings.Load().DefaultProjectsPath;
        if (string.IsNullOrWhiteSpace(baseDir) || !Directory.Exists(Path.GetPathRoot(baseDir) ?? ""))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projects");

        var targetFolder = Path.Combine(baseDir, projName);
        try
        {
            Directory.CreateDirectory(targetFolder);
            ScaffoldTemplateFiles(targetFolder, projName, stackChoice);
            var frameworkLabel = stackChoice switch
            {
                1 => "Fastify + Supabase",
                2 => "Vite + Tailwind",
                3 => "Python FastAPI",
                _ => "Next.js 14 + Capacitor"
            };
            var rec = new ProjectRecord
            {
                Name = projName,
                LocalPath = targetFolder,
                Branch = "main",
                CommitHash = "",
                Author = "Local workspace",
                Framework = frameworkLabel,
                Platform = "Belum terhubung platform",
                PlatformSub = "Belum dideploy",
                SiteUrl = "",
                Status = "Hanya Lokal",
                BuildProgress = 0,
                IconKind = "folder",
                IsCurrentWorkspace = true,
                UpdatedAt = DateTime.UtcNow
            };
            _store.UpsertProject(rec);
            ReloadCatalog();
            _projectsUpdated?.Invoke();
            _openWorkspaceFolder?.Invoke(targetFolder);
            _navigateTo?.Invoke("editor");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Gagal membuat template: " + ex.Message;
        }
    }

    private static void ScaffoldTemplateFiles(string folder, string projectName, int stackChoice)
    {
        var depupConfig = JsonConvert.SerializeObject(new
        {
            name = projectName,
            version = "2.5.0",
            cloud = "https://depup.cloud",
            platform = "depup-edge",
            tunnel = new { port = 3000, autoShare = true }
        }, Formatting.Indented);
        File.WriteAllText(Path.Combine(folder, "depup.config.json"), depupConfig);

        if (stackChoice == 3)
        {
            File.WriteAllText(Path.Combine(folder, "requirements.txt"), "fastapi>=0.110.0\nuvicorn>=0.29.0\n");
            File.WriteAllText(Path.Combine(folder, "main.py"),
                "from fastapi import FastAPI\n\napp = FastAPI(title=\"" + projectName + "\")\n\n@app.get(\"/\")\ndef root():\n    return {\"ok\": True, \"service\": \"" + projectName + "\", \"cloud\": \"depup.cloud\"}\n");
        }
        else
        {
            var pkg = JsonConvert.SerializeObject(new
            {
                name = projectName.ToLowerInvariant(),
                version = "0.1.0",
                private_ = true,
                scripts = new
                {
                    dev = "node server.js",
                    build = "echo \"Build complete\"",
                    start = "node server.js"
                }
            }, Formatting.Indented);
            File.WriteAllText(Path.Combine(folder, "package.json"), pkg);
            File.WriteAllText(Path.Combine(folder, "index.html"),
                $"<!doctype html>\n<html lang=\"id\">\n<head>\n  <meta charset=\"utf-8\">\n  <title>{projectName} — Depup Cloud</title>\n</head>\n<body>\n  <h1>⚡ {projectName}</h1>\n  <p>Configured for Depup Cloud (depup.cloud).</p>\n</body>\n</html>\n");
        }
    }

    private void OpenLocalFolderCard_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih Folder Project Lokal di Hard Drive"
        };
        if (dialog.ShowDialog() != true) return;
        var folder = dialog.FolderName;
        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var configPath = Path.Combine(folder, "depup.config.json");
        if (!File.Exists(configPath))
        {
            try
            {
                File.WriteAllText(configPath, JsonConvert.SerializeObject(new
                {
                    name,
                    version = "2.5.0",
                    cloud = "https://depup.cloud",
                    platform = "railway"
                }, Formatting.Indented));
            }
            catch { }
        }

        string framework = "Web Project";
        try { framework = _deploy.AnalyzeDirectory(folder).Framework; } catch { }
        var git = GitService.ReadStatus(folder);

        var rec = new ProjectRecord
        {
            Name = name,
            LocalPath = folder,
            RepositoryUrl = GitService.ReadOriginUrl(folder) ?? "",
            Branch = git.Branch == "No Git" ? "main" : git.Branch,
            CommitHash = string.IsNullOrWhiteSpace(git.ShortCommit) ? "lokal" : git.ShortCommit,
            Author = "commit lokal",
            Framework = framework,
            Platform = "Belum terhubung platform",
            PlatformSub = "",
            Status = "Hanya Lokal",
            IconKind = "folder",
            IsCurrentWorkspace = true,
            UpdatedAt = DateTime.UtcNow
        };
        _store.UpsertProject(rec);
        ReloadCatalog();
        _projectsUpdated?.Invoke();
        _openWorkspaceFolder?.Invoke(folder);
        _navigateTo?.Invoke("editor");
    }

    private void TunnelStatus_Click(object sender, MouseButtonEventArgs e) =>
        _navigateTo?.Invoke("linkgaib");

    private void OpenWebDashboard_Click(object sender, MouseButtonEventArgs e)
    {
        var url = DepupCloudService.Shared.GetBaseUrl();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private Window CreateDarkDialog(string title, double width, double height) =>
        new()
        {
            Title = title,
            Owner = Window.GetWindow(this),
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Background = MakeBrush("#120F24"),
            Foreground = Brushes.White
        };

    private static Brush MakeBrush(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}
