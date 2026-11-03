using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DepupCode.Models;
using DepupCode.Services;

namespace DepupCode.Views;

public partial class SettingsView : UserControl
{
    private readonly SettingsService _settings;
    private readonly DepupCloudService _cloud = DepupCloudService.Shared;
    private readonly Action<AppSettings>? _settingsUpdated;
    private readonly Action? _openExtensions;
    private readonly PhpRuntimeService _phpRuntime = new();
    private readonly System.Windows.Threading.DispatcherTimer _cloudRefreshTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private string _selectedSection = "general";
    // Nilai token Depup yang terakhir ditampilkan di kotak. Dipakai supaya Simpan tidak menimpa sesi baru
    // (mis. hasil login Google di proses lain) dengan nilai lama yang basi.
    private string _depupTokenShown = "";

    public SettingsView() : this(new SettingsService()) { }

    public SettingsView(SettingsService settings, Action<AppSettings>? settingsUpdated = null, Action? openExtensions = null)
    {
        InitializeComponent();
        _settings = settings;
        _settingsUpdated = settingsUpdated;
        _openExtensions = openExtensions;
        var loaded = settings.Load();
        DepupUrlBox.Text = string.IsNullOrWhiteSpace(loaded.DepupCloudUrl) ? "https://depup.cloud" : loaded.DepupCloudUrl;
        _depupTokenShown = settings.GetToken("depup");
        DepupTokenBox.Password = _depupTokenShown;
        EditorThemeBox.ItemsSource = UiThemePalette.Names;
        EditorThemeBox.SelectedItem = UiThemePalette.Normalize(loaded.AppTheme);
        if (EditorThemeBox.SelectedItem is null) EditorThemeBox.SelectedIndex = 0;
        EditorFontSizeBox.SelectedValuePath = "Content";
        EditorFontSizeBox.SelectedValue = ((int)Math.Clamp(loaded.EditorFontSize, 9, 28)).ToString();
        if (EditorFontSizeBox.SelectedItem is null) EditorFontSizeBox.SelectedIndex = 2;
        FormatterIndentBox.SelectedValuePath = "Tag";
        FormatterIndentBox.SelectedValue = loaded.FormatterUseTabs ? "tab" : Math.Clamp(loaded.FormatterIndentSize, 1, 8).ToString();
        if (FormatterIndentBox.SelectedItem is null) FormatterIndentBox.SelectedValue = "2";
        WordWrapCheck.IsChecked = loaded.WordWrap;
        MinimapCheck.IsChecked = loaded.ShowMinimap;
        DefaultProjectsPathBox.Text = loaded.DefaultProjectsPath;
        SourceControlFeatureCheck.IsChecked = loaded.EnableSourceControl;
        DeployFeatureCheck.IsChecked = loaded.EnableDeployFeature;
        DomainsFeatureCheck.IsChecked = loaded.EnableDomainsFeature;
        EnvironmentFeatureCheck.IsChecked = loaded.EnableEnvironmentFeature;
        LinkGaibFeatureCheck.IsChecked = loaded.EnableLinkGaibFeature;
        LocalServerAutoPreviewCheck.IsChecked = loaded.AutoOpenHtmlPreviewOnServerStart;
        LocalServerFallbackCheck.IsChecked = loaded.LocalServerUsePhpFallback;
        LocalPortStartBox.Text = Math.Clamp(loaded.LocalServerPortStart, 1024, 65535).ToString();
        LocalPortEndBox.Text = Math.Clamp(loaded.LocalServerPortEnd, 1024, 65535).ToString();
        PhpPathBox.Text = loaded.PhpRuntimePath;
        ApachePathBox.Text = loaded.ApacheRuntimePath;
        RefreshLocalRuntimeStatus();
        LoadProviderBoxes();
        ApplySettingsFilter();

        _cloudRefreshTimer.Tick += async (_, _) => await RefreshDepupStatusAsync();
        Loaded += async (_, _) =>
        {
            await RefreshDepupStatusAsync();
            _cloudRefreshTimer.Start();
        };
        Unloaded += (_, _) => _cloudRefreshTimer.Stop();
    }

    private void LoadProviderBoxes()
    {
        GitHubTokenBox.Password = _settings.GetToken("github");
        VercelTokenBox.Password = _settings.GetToken("vercel");
        CloudflareTokenBox.Password = _settings.GetToken("cloudflare");
        RailwayTokenBox.Password = _settings.GetToken("railway");
        CloudflareAccountBox.Text = _settings.Load().CloudflareAccountId;
    }

    private void ProviderSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplySettingsFilter();
    }

    private void OpenExtensions_Click(object sender, RoutedEventArgs e) => _openExtensions?.Invoke();

    private void OpenPrivacyPolicy_Click(object sender, RoutedEventArgs e)
    {
        var consent = new PrivacyConsentWindow(requireAcceptance: false) { Owner = Window.GetWindow(this) };
        consent.ShowDialog();
    }

    private void RevokePrivacyConsent_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            "Persetujuan privasi akan dihapus. Aplikasi akan meminta Anda meninjau kebijakan lagi saat dibuka berikutnya. Pengaturan dan proyek tidak akan dihapus.",
            "Hapus persetujuan privasi", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var settings = _settings.Load();
        settings.HasAcceptedPrivacyPolicy = false;
        settings.AcceptedPrivacyPolicyVersion = "";
        _settings.Save(settings);
        SaveStatus.Text = "Persetujuan privasi dihapus. Kebijakan akan ditampilkan lagi pada pembukaan berikutnya.";
    }

    private void ApplySettingsFilter()
    {
        var query = ProviderSearchBox.Text.Trim();
        bool Matches(string terms) => query.Length == 0 || terms.Contains(query, StringComparison.OrdinalIgnoreCase);

        var searching = query.Length > 0;
        var showPreferences = searching ? Matches("general preferences editor appearance theme font wrap minimap folder projects path") : _selectedSection == "general";
        var showExtensions = searching ? Matches("extensions plugins features modules sidebar activity bar git deploy domains environment link gaib") : _selectedSection == "extensions";
        var showDepup = searching ? Matches("depup cloud primary overview status link gaib edge tunnel gateway session google login") : _selectedSection is "overview" or "depup" or "linkgaib";
        var showVercel = searching ? Matches("vercel api token personal access deployment") : _selectedSection == "vercel";
        var showCloudflare = searching ? Matches("cloudflare pages account id api token") : _selectedSection == "cloudflare";
        var showRailway = searching ? Matches("railway infrastructure platform personal api token deployment") : _selectedSection == "railway";
        var showGitHub = searching ? Matches("github git source control repository token") : _selectedSection == "github";
        var showLocal = searching ? Matches("local server php laravel apache runtime port preview xampp composer artisan") : _selectedSection == "local";
        var showSecurity = searching ? Matches("security dpapi encrypted credential store windows encryption save configuration") : _selectedSection == "security";

        PreferencesCard.Visibility = showPreferences ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsCard.Visibility = showExtensions ? Visibility.Visible : Visibility.Collapsed;
        LocalServerCard.Visibility = showLocal ? Visibility.Visible : Visibility.Collapsed;
        DepupCloudCard.Visibility = showDepup ? Visibility.Visible : Visibility.Collapsed;
        VercelCard.Visibility = showVercel ? Visibility.Visible : Visibility.Collapsed;
        CloudflareCard.Visibility = showCloudflare ? Visibility.Visible : Visibility.Collapsed;
        RailwayCard.Visibility = showRailway ? Visibility.Visible : Visibility.Collapsed;
        GitHubCard.Visibility = showGitHub ? Visibility.Visible : Visibility.Collapsed;
        SecurityInfoCard.Visibility = showSecurity ? Visibility.Visible : Visibility.Collapsed;
        SettingsNoResults.Visibility = showPreferences || showExtensions || showLocal || showDepup || showVercel || showCloudflare || showRailway || showGitHub || showSecurity
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public void NavigateToSection(string section)
    {
        _selectedSection = section.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(ProviderSearchBox.Text))
            ProviderSearchBox.Clear();
        ApplySettingsFilter();

        FrameworkElement target = _selectedSection switch
        {
            "overview" or "general" => PreferencesCard,
            "extensions" => ExtensionsCard,
            "vercel" => VercelCard,
            "cloudflare" => CloudflareCard,
            "railway" => RailwayCard,
            "linkgaib" or "depup" => DepupCloudCard,
            "github" => GitHubCard,
            "local" => LocalServerCard,
            "security" => SecurityInfoCard,
            _ => DepupCloudCard
        };
        target.BringIntoView();
    }

    private static Brush MakeBrush(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;

    private void SetDepupStatus(string text, string hex)
    {
        DepupCloudStatus.Text = text;
        DepupCloudStatus.Foreground = MakeBrush(hex);
    }


    private void ShowSessionInfo(DepupSessionInfo info)
    {
        if (info.Message.Contains("Mode Offline", StringComparison.OrdinalIgnoreCase))
        {
            SetDepupStatus(info.Message + " Pengaturan dan cache lokal tetap tersedia.", "#FBBF24");
            return;
        }
        if (info.IsConnected && !string.IsNullOrWhiteSpace(info.Email))
            SetDepupStatus($"✓ Masuk sebagai {info.Name} ({info.Email}) — {info.BaseUrl}", "#34D399");
        else if (info.IsConnected)
            SetDepupStatus($"Gateway {info.BaseUrl} aktif, tetapi belum masuk akun. Pilih “Masuk dengan Google”.", "#FBBF24");
        else
            SetDepupStatus("✕ " + info.Message, "#FBBF24");
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Token Depup hanya ditulis kalau pengguna benar-benar mengubahnya.
        if (DepupTokenBox.Password != _depupTokenShown)
        {
            _settings.SaveToken("depup", DepupTokenBox.Password);
            _depupTokenShown = DepupTokenBox.Password;
        }
        _settings.SaveToken("github", GitHubTokenBox.Password);
        _settings.SaveToken("vercel", VercelTokenBox.Password);
        _settings.SaveToken("cloudflare", CloudflareTokenBox.Password);
        _settings.SaveToken("railway", RailwayTokenBox.Password);
        var settings = _settings.Load();
        settings.AppTheme = UiThemePalette.Normalize(EditorThemeBox.SelectedItem?.ToString());
        settings.EditorColorTheme = settings.AppTheme;
        settings.EditorFontSize = double.TryParse((EditorFontSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var fontSize)
            ? Math.Clamp(fontSize, 9, 28) : 13;
        var indentChoice = (FormatterIndentBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        settings.FormatterUseTabs = string.Equals(indentChoice, "tab", StringComparison.OrdinalIgnoreCase);
        settings.FormatterIndentSize = int.TryParse(indentChoice, out var indentSize) ? Math.Clamp(indentSize, 1, 8) : 2;
        settings.WordWrap = WordWrapCheck.IsChecked == true;
        settings.ShowMinimap = MinimapCheck.IsChecked == true;
        settings.DefaultProjectsPath = string.IsNullOrWhiteSpace(DefaultProjectsPathBox.Text)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projects")
            : DefaultProjectsPathBox.Text.Trim();
        settings.EnableSourceControl = SourceControlFeatureCheck.IsChecked == true;
        settings.EnableDeployFeature = DeployFeatureCheck.IsChecked == true;
        settings.EnableDomainsFeature = DomainsFeatureCheck.IsChecked == true;
        settings.EnableEnvironmentFeature = EnvironmentFeatureCheck.IsChecked == true;
        settings.EnableLinkGaibFeature = LinkGaibFeatureCheck.IsChecked == true;
        settings.AutoOpenHtmlPreviewOnServerStart = LocalServerAutoPreviewCheck.IsChecked == true;
        settings.LocalServerUsePhpFallback = LocalServerFallbackCheck.IsChecked == true;
        var localStart = int.TryParse(LocalPortStartBox.Text.Trim(), out var parsedStart) ? Math.Clamp(parsedStart, 1024, 65535) : 8000;
        var localEnd = int.TryParse(LocalPortEndBox.Text.Trim(), out var parsedEnd) ? Math.Clamp(parsedEnd, 1024, 65535) : 8099;
        if (localEnd < localStart) (localStart, localEnd) = (localEnd, localStart);
        settings.LocalServerPortStart = localStart;
        settings.LocalServerPortEnd = localEnd;
        LocalPortStartBox.Text = localStart.ToString();
        LocalPortEndBox.Text = localEnd.ToString();
        // Path override hanya disimpan bila benar-benar valid; selain itu kembali ke runtime terkelola.
        settings.PhpRuntimePath = PhpRuntimeService.ResolvePhpDirectory(PhpPathBox.Text) ?? "";
        settings.ApacheRuntimePath = PhpRuntimeService.ResolveApacheRoot(ApachePathBox.Text) ?? "";
        PhpPathBox.Text = settings.PhpRuntimePath;
        ApachePathBox.Text = settings.ApacheRuntimePath;
        settings.DepupCloudUrl = string.IsNullOrWhiteSpace(DepupUrlBox.Text) ? "https://depup.cloud" : DepupUrlBox.Text.Trim();
        settings.DepupBaseUrl = ""; // field lama Codex; dikosongkan agar tidak menimpa URL baru saat migrasi
        settings.CloudflareAccountId = CloudflareAccountBox.Text.Trim();
        _settings.Save(settings);
        _settingsUpdated?.Invoke(settings);
        RefreshLocalRuntimeStatus();
        SaveStatus.Text = "✓ Pengaturan disimpan lokal; kredensial provider dienkripsi dengan DPAPI.";
    }

    private void OpenDepupWeb_Click(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(DepupUrlBox.Text) ? "https://depup.cloud" : DepupUrlBox.Text.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenDashboard_Click(object sender, RoutedEventArgs e)
    {
        try { _cloud.OpenDashboard(); }
        catch (Exception ex) { SetDepupStatus("Dashboard tidak dapat dibuka: " + ex.Message, "#FBBF24"); }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string provider) return;
        if (provider == "depup")
        {
            button.IsEnabled = false;
            SetDepupStatus("Menghubungi gateway depup.cloud…", "#C4B5FD");
            try
            {
                // Pakai nilai yang sedang diketik (URL/token) supaya bisa dites sebelum disimpan.
                var info = await _cloud.CheckSessionAsync(DepupTokenBox.Password, DepupUrlBox.Text.Trim());
                ShowSessionInfo(info);
            }
            catch (Exception ex) { SetDepupStatus("Tes gagal: " + ex.Message, "#FBBF24"); }
            finally { button.IsEnabled = true; CloudControlGate.Refresh(this); }
            return;
        }

        var (box, status) = provider switch
        {
            "github" => (GitHubTokenBox, GitHubStatus),
            "vercel" => (VercelTokenBox, VercelStatus),
            "cloudflare" => (CloudflareTokenBox, CloudflareStatus),
            _ => (RailwayTokenBox, RailwayStatus)
        };
        button.IsEnabled = false;
        status.Text = "Menguji koneksi…";
        try
        {
            var ok = await _settings.TestConnectionAsync(provider, box.Password, CloudflareAccountBox.Text.Trim());
            status.Text = ok ? "✓ Koneksi berhasil." : "✕ Token ditolak atau tidak memiliki izin yang diperlukan.";
        }
        catch (Exception ex) { status.Text = "Tes koneksi gagal: " + ex.Message; }
        finally { button.IsEnabled = true; CloudControlGate.Refresh(this); }
    }

    private async Task RefreshDepupStatusAsync()
    {
        try
        {
            // Ambil token tersimpan; kalau berubah dari luar (login Google di proses lain) dan kotak belum diedit, perbarui.
            var stored = _settings.GetToken("depup");
            if (stored != _depupTokenShown && DepupTokenBox.Password == _depupTokenShown)
            {
                _depupTokenShown = stored;
                DepupTokenBox.Password = stored;
            }

            var info = await _cloud.CheckSessionAsync();
            ShowSessionInfo(info);
        }
        catch (Exception ex) { SetDepupStatus("Tidak dapat memeriksa sesi: " + ex.Message, "#FBBF24"); }
    }

    private void DepupLogin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _cloud.StartLogin();
            SetDepupStatus("Browser dibuka. Selesaikan login Google, lalu kembali ke Depup Code.", "#C4B5FD");
        }
        catch (Exception ex) { SetDepupStatus("Login tidak dapat dimulai: " + ex.Message, "#FBBF24"); }
    }

    private void DepupDisconnect_Click(object sender, RoutedEventArgs e)
    {
        _cloud.DisconnectThisDevice();
        _depupTokenShown = "";
        DepupTokenBox.Password = "";
        SetDepupStatus("Anda sudah keluar dari Depup Cloud di aplikasi ini. Data cloud dan akun Google di browser tetap tersimpan.", "#FBBF24");
    }

    private async void SyncCloudSettings_Click(object sender, RoutedEventArgs e) => await SyncCloudSettingsAsync();

    public async Task SyncCloudSettingsAsync()
    {
        try
        {
            // Pastikan token yang baru diketik ikut terkirim.
            _settings.SaveToken("github", GitHubTokenBox.Password);
            _settings.SaveToken("vercel", VercelTokenBox.Password);
            _settings.SaveToken("cloudflare", CloudflareTokenBox.Password);
            _settings.SaveToken("railway", RailwayTokenBox.Password);
            var local = _settings.Load();
            local.CloudflareAccountId = CloudflareAccountBox.Text.Trim();
            _settings.Save(local);

            await _cloud.SyncSettingsToCloudAsync();
            SetDepupStatus("Token provider yang terisi berhasil disinkronkan ke Depup Cloud.", "#34D399");
        }
        catch (Exception ex) { SetDepupStatus("Sinkronisasi token gagal: " + ex.Message, "#FBBF24"); }
    }

    private async void ImportCloudSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _cloud.ImportSettingsFromCloudAsync();
            LoadProviderBoxes();
            SetDepupStatus("Token yang tersedia sudah diimpor dan disimpan terenkripsi di perangkat ini.", "#34D399");
        }
        catch (Exception ex) { SetDepupStatus("Impor token gagal: " + ex.Message, "#FBBF24"); }
    }

    private void RefreshLocalRuntimeStatus()
    {
        var settings = _settings.Load();
        var runtime = _phpRuntime.Resolve(settings);
        var php = runtime.HasPhp ? $"PHP {runtime.PhpVersion} ✓" : "PHP belum terpasang";
        var apache = runtime.HasApache ? $"Apache {runtime.ApacheVersion} ✓" : "Apache belum terpasang";
        LocalRuntimeStatus.Text = $"{php}  ·  {apache}";
        LocalRuntimeStatus.Foreground = MakeBrush(runtime.IsComplete ? "#34D399" : "#FBBF24");
        LocalRuntimeStatus.ToolTip = runtime.IsComplete
            ? $"PHP: {runtime.PhpDirectory}\nApache: {runtime.ApacheRoot}"
            : "Klik Unduh / Perbaiki untuk memasang runtime resmi tanpa XAMPP.";
        LocalRuntimeDownloadButton.Content = runtime.IsComplete ? "Perbaiki" : "Unduh Runtime";
        LocalRuntimeRemoveButton.IsEnabled = runtime.HasPhp || runtime.HasApache;
    }

    private async void DownloadLocalRuntime_Click(object sender, RoutedEventArgs e)
    {
        LocalRuntimeDownloadButton.IsEnabled = false;
        LocalRuntimeRemoveButton.IsEnabled = false;
        var settings = _settings.Load();
        var progress = new Progress<RuntimeInstallProgress>(p => LocalServerActionStatus.Text = p.Message);
        try
        {
            LocalServerActionStatus.Text = "Menyiapkan unduhan runtime…";
            var runtime = await _phpRuntime.EnsureInstalledAsync(settings, progress, includeApache: true);
            LocalServerActionStatus.Text = runtime.IsComplete
                ? "✓ Runtime siap. Kembali ke workspace lalu jalankan “PHP: Start Local Server”."
                : "Runtime belum lengkap. Periksa koneksi lalu coba lagi.";
            LocalServerActionStatus.Foreground = MakeBrush(runtime.IsComplete ? "#34D399" : "#FBBF24");
        }
        catch (Exception ex)
        {
            LocalServerActionStatus.Text = "Gagal menyiapkan runtime: " + ex.Message;
            LocalServerActionStatus.Foreground = MakeBrush("#F87171");
        }
        finally
        {
            LocalRuntimeDownloadButton.IsEnabled = true;
            RefreshLocalRuntimeStatus();
        }
    }

    private void RemoveLocalRuntime_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Hapus runtime PHP dan Apache yang dikelola Depup Code?\n\n" +
            "Unduhan berikutnya akan memerlukan koneksi internet. Hentikan server lokal terlebih dahulu.",
            "Hapus runtime lokal", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            _phpRuntime.RemoveAll();
            LocalServerActionStatus.Text = "Runtime lokal dihapus. Pengaturan workspace tidak berubah.";
            LocalServerActionStatus.Foreground = MakeBrush("#FBBF24");
        }
        catch (Exception ex)
        {
            LocalServerActionStatus.Text = "Sebagian runtime tidak dapat dihapus (mungkin sedang dipakai): " + ex.Message;
            LocalServerActionStatus.Foreground = MakeBrush("#F87171");
        }
        RefreshLocalRuntimeStatus();
    }

    private void OpenLocalRuntimeFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_phpRuntime.RootDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_phpRuntime.RootDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            LocalServerActionStatus.Text = "Folder runtime tidak dapat dibuka: " + ex.Message;
        }
    }
}
