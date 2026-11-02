using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DepupCode.Services;

namespace DepupCode.Views;

public partial class ExtensionsView : UserControl
{
    private readonly ExtensionMarketplaceService _marketplace;
    private readonly VsCodeExtensionHostService _extensionHost;
    private readonly Func<string?> _workspaceRoot;
    private readonly Func<ExtensionMarketplaceItem, bool> _confirmActivation;
    private readonly ObservableCollection<ExtensionMarketplaceItem> _items = [];
    private IReadOnlyList<ExtensionMarketplaceItem> _lastSearchResults = [];
    private IReadOnlyList<ExtensionTheme> _themes = [];
    private ExtensionMarketplaceItem? _selected;
    private string? _detailIdentifier;
    private bool _installedMode;
    private bool _loaded;
    private string _categoryFilter = "all";
    private CancellationTokenSource? _searchCancellation;

    public event Action<IReadOnlyList<ExtensionTheme>>? InstalledExtensionsChanged;
    public event Action<ExtensionTheme>? ThemeSelected;

    public ExtensionsView(
        ExtensionMarketplaceService marketplace,
        VsCodeExtensionHostService extensionHost,
        Func<string?> workspaceRoot,
        Func<ExtensionMarketplaceItem, bool> confirmActivation)
    {
        InitializeComponent();
        _marketplace = marketplace;
        _extensionHost = extensionHost;
        _workspaceRoot = workspaceRoot;
        _confirmActivation = confirmActivation;
        _extensionHost.CommandsChanged += () => Dispatcher.BeginInvoke(() => ShowDetails(_selected));
        _themes = marketplace.LoadInstalledThemes();
        ExtensionList.ItemsSource = _items;
        SetTabState();
        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await SearchAsync("theme");
        };
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text);

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync(SearchBox.Text);
    }

    private async void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
        await SearchAsync("");
    }

    private async Task SearchAsync(string query)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var token = _searchCancellation.Token;
        _installedMode = false;
        SetTabState();
        SearchBox.Text = query;
        ResultStatus.Text = "Menghubungkan ke Open VSX…";
        EmptyStateTitle.Text = "Memuat Extensions…";
        EmptyStateMessage.Text = "Sedang memuat katalog extension dari Open VSX.";
        EmptyState.Visibility = Visibility.Visible;
        ExtensionList.IsEnabled = false;
        try
        {
            var results = await _marketplace.SearchAsync(query, token);
            if (token.IsCancellationRequested) return;
            _lastSearchResults = results;
            ApplyCategoryFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ResultStatus.Text = "Koneksi gagal";
            EmptyState.Visibility = Visibility.Visible;
            EmptyStateTitle.Text = "Tidak dapat membuka Extensions";
            EmptyStateMessage.Text = "Open VSX tidak bisa dihubungi. Periksa internet lalu coba lagi.\n" + ex.Message;
        }
        finally { ExtensionList.IsEnabled = true; }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_installedMode) _ = SearchAsync(SearchBox.Text);
    }

    private void Installed_Click(object sender, RoutedEventArgs e)
    {
        _searchCancellation?.Cancel();
        _installedMode = true;
        SetTabState();
        _lastSearchResults = _marketplace.GetInstalled();
        ApplyCategoryFilter();
    }

    private void SetTabState()
    {
        BrowseTab.Tag = _installedMode ? null : "Selected";
        InstalledTab.Tag = _installedMode ? "Selected" : null;
        InstalledTab.Content = $"Terpasang ({_marketplace.GetInstalled().Count})";
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoryCombo.SelectedItem is not ComboBoxItem { Tag: string category }) return;
        _categoryFilter = category;
        if (ExtensionList is null || !IsLoaded) return;
        if (_lastSearchResults.Count > 0 || _installedMode) ApplyCategoryFilter();
    }

    private void ApplyCategoryFilter()
    {
        var filtered = _categoryFilter switch
        {
            "all" => _lastSearchResults,
            "compatible" => _lastSearchResults.Where(item => item.HasNativeCategory || item.HasActivationEntryPoint || item.IsInstalled).ToArray(),
            _ => _lastSearchResults.Where(item => item.Categories.Any(value => value.Equals(_categoryFilter, StringComparison.OrdinalIgnoreCase))).ToArray()
        };
        _items.Clear();
        foreach (var item in filtered) _items.Add(item);
        ResultStatus.Text = _installedMode ? $"{filtered.Count} terpasang" : $"{filtered.Count} hasil · Open VSX";
        var empty = filtered.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateTitle.Text = empty
            ? (_installedMode ? "Belum ada extension pada filter ini" : "Tidak ada hasil")
            : "Memuat Extensions…";
        EmptyStateMessage.Text = empty
            ? (_installedMode ? "Pasang extension tema, snippets, atau Node.js dari tab Jelajahi." : "Coba kata kunci lain atau ubah filter kategori.")
            : "";
        ExtensionList.SelectedIndex = filtered.Count == 0 ? -1 : 0;
    }

    private void ExtensionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ExtensionList.SelectedItem as ExtensionMarketplaceItem;
        ShowDetails(_selected);
    }

    private void ShowDetails(ExtensionMarketplaceItem? item)
    {
        var hasSelection = item is not null;
        DetailTitleRun.Text = hasSelection ? item!.Title : "Pilih extension";
        DetailPublisher.Text = hasSelection ? $"{item!.Publisher}  ·  {item.Namespace}.{item.Name}  ·  v{item.Version}" : "";
        DetailDescription.Text = item?.Description ?? "Pilih extension dari daftar untuk melihat informasi dan memasangnya.";
        DetailDownloads.Text = item is null ? "" : item.DownloadsText;
        DetailImage.Source = null;
        DetailIcon.Visibility = Visibility.Visible;
        DetailIcon.Text = item?.IconText ?? "◇";
        if (item?.IconUrl is { Length: > 0 } iconUrl &&
            Uri.TryCreate(iconUrl, UriKind.Absolute, out var iconUri) &&
            iconUri.Scheme == Uri.UriSchemeHttps)
        {
            try
            {
                DetailImage.Source = new BitmapImage(iconUri);
            }
            catch { }
        }
        var category = item?.Categories.FirstOrDefault();
        CategoryBadge.Visibility = category is null ? Visibility.Collapsed : Visibility.Visible;
        DetailCategory.Text = category ?? "";

        VerifiedBadge.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        UpdateMetadata(item);

        var installed = item?.IsInstalled == true;
        var hasSupportedCategory = item?.HasNativeCategory == true;
        var runtimeActive = item is not null && _extensionHost.IsActive(item.Identifier);
        CompatibilityText.Text = installed
            ? $"Terpasang. {(item!.HasActivationEntryPoint ? runtimeActive ? "Runtime Node.js aktif di AppContainer." : "Entry point Node.js tersedia; aktivasi memerlukan persetujuan Anda." : "Depup Code hanya menerapkan kontribusi tema/snippets deklaratif.")}"
            : item?.HasActivationEntryPoint == true
                ? "Paket memiliki entry point Node.js. Setelah dipasang, aktivasi manual diperlukan dan kode berjalan di AppContainer setelah Anda menyetujuinya."
                : hasSupportedCategory
                    ? "Depup Code hanya menerapkan tema/snippets secara deklaratif dan tidak menjalankan kode aktivasi extension VS Code demi keamanan runtime browser."
                    : "Paket diperiksa saat dipasang. Entry point Node.js didukung melalui compatibility host terbatas.";
        InstallButton.Visibility = installed || _installedMode ? Visibility.Collapsed : Visibility.Visible;
        InstallButton.IsEnabled = item is not null;
        ActivateButton.Visibility = installed && item!.HasActivationEntryPoint && !runtimeActive ? Visibility.Visible : Visibility.Collapsed;
        ActivateButton.IsEnabled = item is not null;
        DeactivateButton.Visibility = installed && runtimeActive ? Visibility.Visible : Visibility.Collapsed;
        UninstallButton.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        UninstallSeparator.Visibility = UninstallButton.Visibility;
        MoreButton.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        OpenRegistryButton.IsEnabled = hasSelection;
        CopyIdentifierButton.IsEnabled = hasSelection;
        var themes = item is null ? [] : _themes.Where(theme =>
            theme.ExtensionId.Equals(item.Identifier, StringComparison.OrdinalIgnoreCase)).ToArray();
        ThemeChoiceBox.ItemsSource = themes;
        ThemeChoiceBox.SelectedIndex = themes.Length == 0 ? -1 : 0;
        ThemeChoiceBox.Visibility = installed && themes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UseThemeButton.Visibility = installed && themes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderFeatures(item);

        if (_detailIdentifier != item?.Identifier)
        {
            _detailIdentifier = item?.Identifier;
            DetailScroll.ScrollToTop();
        }
    }

    private void UpdateMetadata(ExtensionMarketplaceItem? item)
    {
        MetaIdentifier.Text = item?.Identifier ?? "—";
        MetaVersion.Text = item is null ? "—" : $"v{item.Version}";
        MetaPublisher.Text = item?.Publisher ?? "—";
        MetaCategories.Text = item is { Categories.Count: > 0 } ? string.Join(" · ", item.Categories) : "—";
        MetaSource.Text = item is null ? "—" : item.IsInstalled ? "Open VSX · terpasang" : "Open VSX";
    }

    private void RenderFeatures(ExtensionMarketplaceItem? item)
    {
        FeatureList.Children.Clear();
        if (item is null) return;
        var features = new List<string>();
        if (item.Categories.Any(category => category.Equals("Themes", StringComparison.OrdinalIgnoreCase)))
            features.Add("Menyediakan tema warna yang dapat diterapkan ke editor dan antarmuka Depup Code.");
        if (item.Categories.Any(category => category.Equals("Snippets", StringComparison.OrdinalIgnoreCase)))
            features.Add("Menyediakan snippet yang muncul melalui completion pada bahasa yang didukung.");
        if (item.HasActivationEntryPoint)
            features.Add("Memiliki entry point Node.js; aktivasi manual dan persetujuan diperlukan sebelum kode berjalan.");
        features.Add(item.IsInstalled
            ? "Terpasang di perangkat ini dan siap dipakai."
            : "Dapat dipasang dari Open VSX lalu dihapus kembali kapan saja.");
        features.Add("Konfigurasi tambahan mengikuti pengaturan dan settings.json Depup Code.");
        foreach (var feature in features) FeatureList.Children.Add(CreateFeatureRow(feature));
    }

    private UIElement CreateFeatureRow(string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = TryFindResource("ThemeAccentSoftBrush") as Brush ?? Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(1, 6, 0, 0)
        };
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 10.5,
            LineHeight = 16,
            Foreground = TryFindResource("ThemeSecondaryBrush") as Brush ?? Brushes.Gray
        };
        Grid.SetColumn(label, 1);
        row.Children.Add(dot);
        row.Children.Add(label);
        return row;
    }

    private void More_Click(object sender, RoutedEventArgs e) => MorePopup.IsOpen = true;

    private void OpenRegistry_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://open-vsx.org/extension/{Uri.EscapeDataString(_selected.Namespace)}/{Uri.EscapeDataString(_selected.Name)}",
                UseShellExecute = true
            });
            ResultStatus.Text = "Membuka halaman Open VSX";
        }
        catch (Exception ex)
        {
            ResultStatus.Text = "Tidak dapat membuka browser";
            CompatibilityText.Text = "Halaman Open VSX tidak dapat dibuka: " + ex.Message;
        }
        MorePopup.IsOpen = false;
    }

    private void CopyIdentifier_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            Clipboard.SetText(_selected.Identifier);
            ResultStatus.Text = $"ID {_selected.Identifier} disalin";
        }
        catch (Exception ex)
        {
            ResultStatus.Text = "Tidak dapat menyalin ID";
            CompatibilityText.Text = "Clipboard tidak tersedia: " + ex.Message;
        }
        MorePopup.IsOpen = false;
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var item = _selected;
        if (item is null || !item.IsInstalled || !item.HasActivationEntryPoint || !_confirmActivation(item)) return;
        ActivateButton.IsEnabled = false;
        ActivateButton.Content = "Mengaktifkan…";
        try
        {
            await _extensionHost.ActivateAsync(item, _marketplace.GetInstalledDirectory(item), _workspaceRoot());
            ResultStatus.Text = $"{item.Title} aktif";
        }
        catch (Exception ex)
        {
            ResultStatus.Text = "Aktivasi gagal";
            CompatibilityText.Text = "Extension tidak dapat diaktifkan: " + ex.Message;
        }
        finally
        {
            ActivateButton.Content = "Aktifkan";
            ActivateButton.IsEnabled = true;
            ShowDetails(_selected);
        }
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var item = _selected;
        await _extensionHost.DeactivateAsync(item.Identifier);
        ResultStatus.Text = $"{item.Title} dinonaktifkan";
        ShowDetails(item);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || InstallButton.IsEnabled is false) return;
        InstallButton.IsEnabled = false;
        InstallButton.Content = "Memasang…";
        ResultStatus.Text = "Mengunduh paket extension…";
        try
        {
            var installed = await _marketplace.InstallAsync(_selected);
            _themes = _marketplace.LoadInstalledThemes();
            InstalledExtensionsChanged?.Invoke(_themes);
            ResultStatus.Text = "Extension diterapkan";
            SetTabState();
            if (_installedMode) Installed_Click(this, new RoutedEventArgs());
            else
            {
                var index = -1;
                for (var i = 0; i < _items.Count; i++)
                    if (_items[i].Identifier.Equals(installed.Identifier, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                if (index >= 0) _items[index] = installed;
                _selected = installed;
                ShowDetails(installed);
            }
        }
        catch (Exception ex)
        {
            ResultStatus.Text = "Tidak dapat memasang";
            CompatibilityText.Text = ex is NotSupportedException
                ? ex.Message
                : "Pemasangan gagal: " + ex.Message;
        }
        finally
        {
            InstallButton.Content = "Pasang";
            InstallButton.IsEnabled = _selected is not null;
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var selected = _selected;
        try
        {
            await _extensionHost.DeactivateAsync(selected.Identifier);
            _marketplace.Uninstall(selected);
            _themes = _marketplace.LoadInstalledThemes();
            InstalledExtensionsChanged?.Invoke(_themes);
            ResultStatus.Text = $"{selected.Title} dihapus";
            SetTabState();
            if (_installedMode) Installed_Click(this, new RoutedEventArgs());
            else
            {
                for (var i = 0; i < _items.Count; i++)
                    if (_items[i].Identifier.Equals(selected.Identifier, StringComparison.OrdinalIgnoreCase))
                    {
                        _items[i] = _items[i] with { IsInstalled = false };
                        _selected = _items[i];
                        break;
                    }
                ShowDetails(_selected);
            }
        }
        catch (Exception ex) { CompatibilityText.Text = "Gagal menghapus extension: " + ex.Message; }
    }

    private void UseTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var theme = ThemeChoiceBox.SelectedItem as ExtensionTheme;
        if (theme is null) return;
        ThemeSelected?.Invoke(theme);
        ResultStatus.Text = $"Tema {theme.Name} diterapkan";
    }
}
