using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DepupCode.Models;
using DepupCode.Services;
using Microsoft.Win32;

namespace DepupCode.Views;

public partial class UploadView : UserControl
{
    private readonly SettingsService _settings;
    private readonly LocalDataStore _store;
    private readonly GitHubService _github = new();
    private readonly DeployService _deploy = new();
    private bool _isZip;

    public UploadView() : this(new SettingsService(), new LocalDataStore()) { }

    public UploadView(SettingsService settings, LocalDataStore store)
    {
        InitializeComponent();
        _settings = settings;
        _store = store;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Pilih folder project" };
        if (dialog.ShowDialog() != true) return;
        SourcePathBox.Text = dialog.FolderName;
        _isZip = false;
        if (string.IsNullOrWhiteSpace(RepositoryNameBox.Text)) RepositoryNameBox.Text = new DirectoryInfo(dialog.FolderName).Name;
    }

    private void ChooseZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ZIP archive (*.zip)|*.zip", Title = "Pilih ZIP project" };
        if (dialog.ShowDialog() != true) return;
        SourcePathBox.Text = dialog.FileName;
        _isZip = true;
        if (string.IsNullOrWhiteSpace(RepositoryNameBox.Text)) RepositoryNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
    }

    private async void DeployEdge_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePathBox.Text) || string.IsNullOrWhiteSpace(RepositoryNameBox.Text))
        {
            StatusText.Text = "Pilih folder atau ZIP dan isi nama site terlebih dahulu.";
            return;
        }

        string? temporaryFolder = null;
        var folder = SourcePathBox.Text;
        DeployEdgeButton.IsEnabled = UploadButton.IsEnabled = false;
        UploadProgress.IsIndeterminate = true;
        StatusText.Text = "Mempersiapkan bundle dan mempublikasikan ke Depup Edge CDN (depup.cloud)…";
        RepositoryLink.Text = "";
        try
        {
            if (_isZip)
            {
                temporaryFolder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DepupCode-upload-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(temporaryFolder);
                ZipFile.ExtractToDirectory(folder, temporaryFolder, overwriteFiles: false);
                var entries = Directory.GetFileSystemEntries(temporaryFolder);
                folder = entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : temporaryFolder;
            }

            if (File.Exists(Path.Combine(folder, ".env")))
            {
                var dotenv = GitHubService.ParseDotEnv(await File.ReadAllTextAsync(Path.Combine(folder, ".env")));
                _store.ImportDotEnv(RepositoryNameBox.Text.Trim(), dotenv);
            }

            var analysis = _deploy.AnalyzeDirectory(folder);
            var envVars = _store.LoadEnvironment(RepositoryNameBox.Text.Trim());
            var depup = new DepupCloudService(_settings);
            var res = await depup.DeployToDepupCloudAsync(analysis, RepositoryNameBox.Text.Trim(), envVars);

            _store.UpsertProject(new ProjectRecord
            {
                Name = RepositoryNameBox.Text.Trim(),
                LocalPath = _isZip ? "" : folder,
                Framework = analysis.Framework,
                Platform = "Depup Edge CDN",
                PlatformSub = "Link Instan",
                SiteUrl = res.Url,
                ProviderProjectId = res.ProviderProjectId,
                Status = res.Status,
                BuildProgress = 100,
                IconKind = "bolt",
                UpdatedAt = DateTime.UtcNow
            });

            StatusText.Text = res.Message;
            RepositoryLink.Text = res.Url;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Publikasi ke Depup Edge gagal: " + ex.Message;
        }
        finally
        {
            UploadProgress.IsIndeterminate = false;
            DeployEdgeButton.IsEnabled = UploadButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
            CleanupTemp(temporaryFolder);
        }
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var token = _settings.GetToken("github");
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusText.Text = "Tambahkan GitHub Token dengan izin repo di Settings (atau gunakan tombol Depup Edge di sebelah kiri).";
            return;
        }
        if (string.IsNullOrWhiteSpace(SourcePathBox.Text) || string.IsNullOrWhiteSpace(RepositoryNameBox.Text))
        {
            StatusText.Text = "Pilih folder atau ZIP dan isi nama repository.";
            return;
        }

        string? temporaryFolder = null;
        var folder = SourcePathBox.Text;
        DeployEdgeButton.IsEnabled = UploadButton.IsEnabled = false;
        UploadProgress.IsIndeterminate = true;
        StatusText.Text = "Membuat repository dan mengunggah file…";
        RepositoryLink.Text = "";
        try
        {
            if (_isZip)
            {
                temporaryFolder = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DepupCode-upload-" + Guid.NewGuid().ToString("N")));
                Directory.CreateDirectory(temporaryFolder);
                ZipFile.ExtractToDirectory(folder, temporaryFolder, overwriteFiles: false);
                var entries = Directory.GetFileSystemEntries(temporaryFolder);
                if (entries.Length == 1 && Directory.Exists(entries[0])) folder = entries[0];
                else folder = temporaryFolder;
            }
            var result = await _github.UploadFolderAsync(folder, RepositoryNameBox.Text.Trim(),
                PrivateCheckBox.IsChecked == true, token);
            _store.ImportDotEnv(result.RepositoryUrl, result.EnvironmentVariables);
            _store.UpsertProject(new ProjectRecord
            {
                Name = RepositoryNameBox.Text.Trim(),
                LocalPath = _isZip ? "" : folder,
                RepositoryUrl = result.RepositoryUrl,
                Platform = "GitHub Repository",
                PlatformSub = "GitHub",
                SiteUrl = "",
                Status = "Repository tersinkronkan",
                IconKind = "cloud",
                UpdatedAt = DateTime.UtcNow
            });
            StatusText.Text = $"{result.UploadedFiles} file diunggah. " +
                              $"{result.EnvironmentVariables.Count} environment variable dari .env disimpan terenkripsi.";
            RepositoryLink.Text = result.RepositoryUrl;
        }
        catch (Exception ex) { StatusText.Text = "Upload gagal: " + ex.Message; }
        finally
        {
            UploadProgress.IsIndeterminate = false;
            DeployEdgeButton.IsEnabled = UploadButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
            CleanupTemp(temporaryFolder);
        }
    }

    private static void CleanupTemp(string? temporaryFolder)
    {
        if (string.IsNullOrWhiteSpace(temporaryFolder)) return;
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var cleanup = Path.GetFullPath(temporaryFolder);
        if (cleanup.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(cleanup).StartsWith("DepupCode-upload-", StringComparison.Ordinal))
            try { Directory.Delete(cleanup, true); } catch { }
    }

    private void OpenRepository(object sender, MouseButtonEventArgs e)
    {
        if (Uri.TryCreate(RepositoryLink.Text, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
