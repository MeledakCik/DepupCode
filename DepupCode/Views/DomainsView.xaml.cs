using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using DepupCode.Models;
using DepupCode.Services;

namespace DepupCode.Views;

public partial class DomainsView : UserControl
{
    private readonly LocalDataStore _store;
    private readonly DepupCloudService _cloud;
    private readonly SettingsService _settings;
    private readonly List<ProjectRecord> _items;

    public DomainsView(LocalDataStore store) : this(store, new SettingsService()) { }

    public DomainsView(LocalDataStore store, SettingsService settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _cloud = DepupCloudService.Shared;
        _items = store.LoadProjects().Where(p => !string.IsNullOrWhiteSpace(p.SiteUrl)).ToList();
        foreach (var domain in store.LoadDomains())
            _items.Add(ToProjectRecord(domain));
        ApplyFilter();
        Loaded += async (_, _) => await LoadCloudDomainsAsync();
    }

    private void ApplyFilter()
    {
        var q = DomainSearchBox?.Text.Trim() ?? "";
        var filtered = string.IsNullOrWhiteSpace(q)
            ? _items.ToList()
            : _items.Where(p =>
                p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.SiteUrl.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                p.Platform.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        DomainsList.ItemsSource = filtered;
        StatusText.Text = $"{filtered.Count} alamat tersedia";
    }

    private void DomainSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void AddDomain_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var url = owner is not null
            ? InputDialog.Show(owner, "Tambah Custom Domain", "Masukkan nama domain untuk disimpan ke akun Depup Cloud:", "", "Contoh: app.domain.com")
            : null;
        if (string.IsNullOrWhiteSpace(url)) return;
        var normalizedDomain = url.Trim();
        if (!normalizedDomain.StartsWith("http", StringComparison.OrdinalIgnoreCase)) normalizedDomain = "https://" + normalizedDomain;
        if (!Uri.TryCreate(normalizedDomain, UriKind.Absolute, out var parsed))
        {
            StatusText.Text = "Domain tidak valid.";
            return;
        }
        normalizedDomain = parsed.Host;
        var hostName = parsed.Host.Split('.')[0];
        var project = _store.LoadProjects().FirstOrDefault(item =>
            item.Name.Equals(hostName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(item.SiteUrl))?.Name ?? hostName;
        var record = new DomainRecord
        {
            Domain = normalizedDomain,
            Project = project,
            Status = "Pending",
            UpdatedAt = DateTime.UtcNow
        };
        _store.UpsertDomain(record);
        _items.Insert(0, ToProjectRecord(record));
        ApplyFilter();
        try
        {
            if (await _cloud.GetSessionUserAsync() is null)
            {
                StatusText.Text = $"{normalizedDomain} disimpan di cache lokal. Masuk ke Depup Cloud untuk sinkronisasi akun.";
                return;
            }
            await _cloud.SaveDomainAsync(record);
            StatusText.Text = $"{normalizedDomain} disimpan di cache lokal dan akun Depup Cloud.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{normalizedDomain} tersimpan lokal; sinkronisasi cloud gagal: {ex.Message}";
        }
    }

    private static ProjectRecord ToProjectRecord(DomainRecord domain) => new()
    {
        Name = string.IsNullOrWhiteSpace(domain.Project) ? domain.Domain : domain.Project,
        SiteUrl = domain.Domain.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? domain.Domain : "https://" + domain.Domain,
        Platform = "Custom Domain",
        Status = domain.Status,
        IconKind = "cloud",
        UpdatedAt = domain.UpdatedAt
    };

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectRecord project &&
            Uri.TryCreate(project.SiteUrl, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProjectRecord project && !string.IsNullOrWhiteSpace(project.SiteUrl))
        {
            Clipboard.SetText(project.SiteUrl);
            StatusText.Text = "Disalin: " + project.SiteUrl;
        }
    }

    private async Task LoadCloudDomainsAsync()
    {
        if (_settings.Load().IsOfflineMode || string.IsNullOrWhiteSpace(_settings.GetDepupSessionCookie())) return;
        try
        {
            var domains = await _cloud.ListDomainsAsync();
            foreach (var domain in domains)
            {
                _store.UpsertDomain(domain);
                if (_items.Any(item => item.SiteUrl.Contains(domain.Domain, StringComparison.OrdinalIgnoreCase))) continue;
                _items.Add(ToProjectRecord(domain));
            }
            ApplyFilter();
        }
        catch (Exception ex) { StatusText.Text = "Cache domain tersedia; sinkronisasi Depup Cloud gagal: " + ex.Message; }
    }

    private void ManageDomains_Click(object sender, RoutedEventArgs e)
    {
        try { _cloud.OpenDashboard("domains"); }
        catch (Exception ex) { StatusText.Text = "Dashboard domain: " + ex.Message; }
    }
}
