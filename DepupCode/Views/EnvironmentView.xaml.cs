using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DepupCode.Models;
using DepupCode.Services;
using Microsoft.Win32;

namespace DepupCode.Views;

public partial class EnvironmentView : UserControl
{
    private sealed record ProjectOption(string Key, string Name);

    private readonly LocalDataStore _store;
    private readonly SettingsService _settings;
    private readonly DepupCloudService _cloud;
    private readonly ObservableCollection<EnvironmentVariable> _variables = [];
    private readonly ICollectionView _variableView;
    private string _searchQuery = "";
    private string _activeFilter = "all";
    private bool _initialized;

    public event EventHandler? EnvironmentStateChanged;

    public string CurrentProjectKey => ProjectKey;
    public string CurrentProjectName
    {
        get
        {
            var project = _store.LoadProjects().FirstOrDefault(value =>
                string.Equals(value.RepositoryUrl, ProjectKey, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Name, ProjectKey, StringComparison.OrdinalIgnoreCase));
            return project?.Name ?? ProjectKey;
        }
    }
    public string ActiveSidebarFilter => _activeFilter;
    public string SearchQuery => _searchQuery;
    public int VariableCount => _variables.Count;
    public int SecretCount => _variables.Count(value => value.IsSecret);
    public int PublicCount => _variables.Count(value => !value.IsSecret);
    public int VisibleVariableCount => _variableView.Cast<EnvironmentVariable>().Count();

    public EnvironmentView() : this(new LocalDataStore(), new SettingsService()) { }

    public EnvironmentView(LocalDataStore store, SettingsService settings)
    {
        InitializeComponent();
        _store = store;
        _settings = settings;
        _cloud = DepupCloudService.Shared;
        _variableView = CollectionViewSource.GetDefaultView(_variables);
        _variableView.Filter = FilterVariable;
        var projects = store.LoadProjects()
            .Select(p => new ProjectOption(
                string.IsNullOrWhiteSpace(p.RepositoryUrl) ? p.Name : p.RepositoryUrl,
                string.IsNullOrWhiteSpace(p.Name) ? p.RepositoryUrl : p.Name))
            .Where(project => !string.IsNullOrWhiteSpace(project.Key))
            .DistinctBy(project => project.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (projects.Count == 0) projects.Add(new ProjectOption("Default project", "Default project"));
        ProjectBox.ItemsSource = projects;
        ProjectBox.SelectedIndex = 0;
        VariablesGrid.ItemsSource = _variableView;
        _initialized = true;
        LoadSelected();
        Loaded += async (_, _) => await LoadCloudEnvironmentAsync();
    }

    private async void Project_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized)
        {
            LoadSelected();
            await LoadCloudEnvironmentAsync();
        }
    }

    private void LoadSelected()
    {
        _variables.Clear();
        foreach (var value in _store.LoadEnvironment(ProjectKey))
            _variables.Add(value);
        _variableView.Refresh();
        StatusText.Text = $"{_variables.Count} variabel tersimpan.";
        UpdateDashboard();
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string ProjectKey => ProjectBox.SelectedValue?.ToString() ?? "Default project";

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AddVariable();
    }

    public void AddVariable()
    {
        SetSearchQuery("");
        var isSecret = _activeFilter != "public";
        var target = _activeFilter.StartsWith("target:", StringComparison.OrdinalIgnoreCase)
            ? _activeFilter[7..]
            : "Production";
        var added = new EnvironmentVariable
        {
            Name = "NEW_VARIABLE",
            Target = target,
            IsSecret = isSecret,
            Category = isSecret ? "Production Secrets" : "Public Config"
        };
        _variables.Add(added);
        _variableView.Refresh();
        var visibleIndex = _variableView.Cast<EnvironmentVariable>().ToList().IndexOf(added);
        if (visibleIndex >= 0)
        {
            VariablesGrid.SelectedIndex = visibleIndex;
            VariablesGrid.ScrollIntoView(VariablesGrid.SelectedItem);
            VariablesGrid.BeginEdit();
        }
        StatusText.Text = "Variabel baru ditambahkan. Lengkapi nilai lalu simpan.";
        UpdateDashboard();
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = _variables.Where(value => value.IsSelected).ToList();
        if (selected.Count == 0 && VariablesGrid.SelectedItem is EnvironmentVariable current)
            selected.Add(current);

        foreach (var item in selected)
            _variables.Remove(item);

        _variableView.Refresh();
        StatusText.Text = selected.Count == 0 ? "Pilih variabel yang ingin dihapus." : $"{selected.Count} variabel dihapus. Simpan untuk menerapkan perubahan.";
        UpdateDashboard();
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSidebarFilter(string filter)
    {
        _activeFilter = string.IsNullOrWhiteSpace(filter) ? "all" : filter;
        _variableView.Refresh();
        UpdateFilterButtonStates();
        UpdateDashboard();
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSearchQuery(string query)
    {
        var normalizedQuery = query?.Trim() ?? "";
        if (_searchQuery == normalizedQuery) return;
        _searchQuery = normalizedQuery;
        if (EnvironmentSearchBox.Text != normalizedQuery)
            EnvironmentSearchBox.Text = normalizedQuery;
        _variableView.Refresh();
        UpdateDashboard();
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string filter)
            SetSidebarFilter(filter);
    }

    private void EnvironmentSearch_TextChanged(object sender, TextChangedEventArgs e) => SetSearchQuery(EnvironmentSearchBox.Text);

    private void UpdateFilterButtonStates()
    {
        SetFilterButtonState(AllFilterButton, _activeFilter == "all");
        SetFilterButtonState(SecretFilterButton, _activeFilter == "secret");
        SetFilterButtonState(PublicFilterButton, _activeFilter == "public");
    }

    private void SetFilterButtonState(Button button, bool isActive)
    {
        if (isActive)
        {
            button.SetResourceReference(Control.BackgroundProperty, "ThemeActiveNavBrush");
            button.SetResourceReference(Control.ForegroundProperty, "ThemePrimaryBrush");
            button.SetResourceReference(Control.BorderBrushProperty, "ThemeAccentBrush");
            return;
        }

        button.ClearValue(Control.BackgroundProperty);
        button.ClearValue(Control.ForegroundProperty);
        button.ClearValue(Control.BorderBrushProperty);
    }

    private void UpdateDashboard()
    {
        TotalCountText.Text = _variables.Count.ToString();
        SecretsCountText.Text = SecretCount.ToString();
        PublicCountText.Text = PublicCount.ToString();

        var visibleCount = VisibleVariableCount;
        EmptyStatePanel.Visibility = visibleCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = _variables.Count == 0
            ? "Belum ada environment variable untuk project ini."
            : "Tidak ada variabel yang cocok dengan filter atau pencarian.";
    }

    private static Brush CreateBrush(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;

    private Brush ThemeBrush(string key, string fallback) =>
        TryFindResource(key) as Brush ?? CreateBrush(fallback);

    private bool FilterVariable(object item)
    {
        if (item is not EnvironmentVariable variable) return false;

        var matchesFilter = _activeFilter switch
        {
            "secret" => variable.IsSecret,
            "public" => !variable.IsSecret,
            _ when _activeFilter.StartsWith("target:", StringComparison.OrdinalIgnoreCase) =>
                string.Equals(variable.Target, _activeFilter[7..], StringComparison.OrdinalIgnoreCase),
            _ => true
        };
        if (!matchesFilter || _searchQuery.Length == 0) return matchesFilter;

        return variable.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrEmpty(variable.Category) && variable.Category.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrEmpty(variable.Target) && variable.Target.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveLocal();
    }

    private void ToggleAllSecrets_Click(object sender, RoutedEventArgs e)
    {
        var secrets = _variables.Where(value => value.IsSecret).ToList();
        if (secrets.Count == 0) return;

        var reveal = secrets.Any(value => !value.IsRevealed);
        foreach (var secret in secrets)
            secret.IsRevealed = reveal;

        ToggleSecretsButton.Content = reveal ? "Mask All Values" : "Buka Semua Nilai";
        VariablesGrid.Items.Refresh();
    }

    private void ToggleReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentVariable variable || !variable.IsSecret) return;
        variable.IsRevealed = !variable.IsRevealed;
        VariablesGrid.Items.Refresh();
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentVariable variable) return;
        try
        {
            Clipboard.SetText(variable.Value);
            StatusText.Text = $"Nilai {variable.Name} disalin ke clipboard.";
        }
        catch (Exception ex) { StatusText.Text = "Tidak dapat menyalin nilai: " + ex.Message; }
    }

    private void ToggleSecret_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentVariable variable) return;
        variable.IsSecret = !variable.IsSecret;
        variable.IsRevealed = false;
        variable.Category = variable.IsSecret ? "Production Secrets" : "Public Config";
        variable.UpdatedAt = DateTime.UtcNow;
        VariablesGrid.Items.Refresh();
        UpdateDashboard();
        StatusText.Text = $"{variable.Name} diubah menjadi {(variable.IsSecret ? "secret DPAPI" : "public config")}. Simpan untuk menerapkan perubahan.";
        EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EditValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentVariable variable) return;

        var editor = new TextBox
        {
            Text = variable.Value,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 120,
            Padding = new Thickness(9),
            Background = ThemeBrush("ThemeInputBrush", "#100A20"),
            Foreground = ThemeBrush("ThemePrimaryBrush", "#E2E8F0"),
            BorderBrush = ThemeBrush("ThemeBorderLightBrush", "#382F69"),
            CaretBrush = ThemeBrush("ThemeAccentSoftBrush", "#C4B5FD")
        };

        var dialog = new Window
        {
            Title = $"Edit {variable.Name}",
            Width = 480,
            Height = 280,
            MinWidth = 360,
            MinHeight = 220,
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
            Background = ThemeBrush("ThemePanelBrush", "#120D22"),
            Foreground = ThemeBrush("ThemePrimaryBrush", "#E2E8F0")
        };

        var layout = new Grid { Margin = new Thickness(16) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = variable.IsSecret ? "Secret value" : "Configuration value",
            Foreground = ThemeBrush("ThemeAccentSoftBrush", "#A78BFA"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(editor, 1);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 76,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            Background = ThemeBrush("ThemeSurfaceBrush", "#1C1736"),
            Foreground = ThemeBrush("ThemePrimaryBrush", "#E2E8F0"),
            BorderBrush = ThemeBrush("ThemeBorderLightBrush", "#382F69"),
            BorderThickness = new Thickness(1)
        };
        var save = new Button
        {
            Content = "Save value",
            IsDefault = true,
            MinWidth = 90,
            Padding = new Thickness(10, 5, 10, 5),
            Background = ThemeBrush("ThemeAccentHoverBrush", "#7C3AED"),
            Foreground = Brushes.White,
            BorderBrush = ThemeBrush("ThemeAccentBrush", "#8B5CF6")
        };
        save.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        Grid.SetRow(actions, 2);

        layout.Children.Add(title);
        layout.Children.Add(editor);
        layout.Children.Add(actions);
        dialog.Content = layout;

        if (dialog.ShowDialog() != true) return;
        variable.Value = editor.Text;
        variable.IsRevealed = false;
        variable.UpdatedAt = DateTime.UtcNow;
        VariablesGrid.Items.Refresh();
        StatusText.Text = $"Nilai {variable.Name} diperbarui. Simpan untuk menerapkan perubahan.";
    }

    private void ImportDotEnv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import environment variables",
            Filter = "Environment files (.env)|.env;*.env|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var imported = new List<EnvironmentVariable>();
            foreach (var rawLine in File.ReadLines(dialog.FileName))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.Ordinal))
                    line = line[7..].TrimStart();

                var separator = line.IndexOf('=');
                if (separator <= 0) continue;

                var name = line[..separator].Trim();
                if (!Regex.IsMatch(name, @"^[A-Z_][A-Z0-9_]*$")) continue;

                var value = line[(separator + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                    value = value[1..^1];

                imported.Add(new EnvironmentVariable
                {
                    Name = name,
                    Value = value,
                    Target = "Production",
                    Category = "Production Secrets",
                    IsSecret = true
                });
            }

            if (imported.Count == 0)
            {
                StatusText.Text = "Tidak ditemukan variabel valid dalam file .env.";
                return;
            }

            foreach (var item in imported)
            {
                var existing = _variables.FirstOrDefault(value => value.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                    _variables.Add(item);
                else
                {
                    existing.Value = item.Value;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.IsSecret = true;
                    existing.Category = "Production Secrets";
                    existing.IsRevealed = false;
                }
            }

            _variableView.Refresh();
            UpdateDashboard();
            StatusText.Text = $"{imported.Count} variabel diimpor dari .env. Simpan untuk mengenkripsi salinan lokal.";
            EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) { StatusText.Text = "Impor .env gagal: " + ex.Message; }
    }

    private void VariablesGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.Row.Item is not EnvironmentVariable variable) return;
        Dispatcher.BeginInvoke(() =>
        {
            variable.UpdatedAt = DateTime.UtcNow;
            VariablesGrid.Items.Refresh();
            UpdateDashboard();
            EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void SyncSave_Click(object sender, RoutedEventArgs e) => await SaveAndSyncAsync();

    public async Task SaveAndSyncAsync()
    {
        if (!SaveLocal()) return;
        if (_settings.Load().IsOfflineMode)
        {
            StatusText.Text = $"Disimpan lokal ({_variables.Count} variabel). Mode Offline aktif; sinkronisasi cloud dijeda.";
            return;
        }
        try
        {
            var user = await _cloud.GetSessionUserAsync();
            if (user is null)
            {
                StatusText.Text = "Tersimpan lokal. Hubungkan Depup Cloud di Settings untuk menyinkronkan secrets.";
                return;
            }
            await _cloud.SaveEnvironmentAsync(CloudProjectName, _variables.ToList());
            StatusText.Text = $"{_variables.Count} variabel tersimpan lokal dan tersinkron ke akun Depup Cloud.";
        }
        catch (Exception ex) { StatusText.Text = "Tersimpan lokal, tetapi sinkronisasi cloud gagal: " + ex.Message; }
    }

    private bool SaveLocal()
    {
        VariablesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        VariablesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var invalid = _variables.FirstOrDefault(v => !Regex.IsMatch(v.Name, @"^[A-Z_][A-Z0-9_]*$"));
        if (invalid is not null)
        {
            StatusText.Text = $"Nama variabel {invalid.Name} tidak valid. Gunakan huruf besar, angka, dan underscore.";
            return false;
        }
        if (_variables.GroupBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            StatusText.Text = "Nama variabel harus unik.";
            return false;
        }
        _store.SaveEnvironment(ProjectKey, _variables);
        StatusText.Text = $"Disimpan terenkripsi di perangkat ini ({_variables.Count} variabel).";
        return true;
    }

    private string CloudProjectName
    {
        get
        {
            var project = _store.LoadProjects().FirstOrDefault(p =>
                p.RepositoryUrl.Equals(ProjectKey, StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals(ProjectKey, StringComparison.OrdinalIgnoreCase));
            return project?.Name ?? ProjectKey;
        }
    }

    private async Task LoadCloudEnvironmentAsync()
    {
        if (ProjectBox.SelectedItem is null) return;
        var requestedProjectKey = ProjectKey;
        var requestedCloudProject = CloudProjectName;
        if (_settings.Load().IsOfflineMode)
        {
            StatusText.Text = $"Mode Offline — {_variables.Count} variabel dimuat dari cache lokal.";
            return;
        }
        if (string.IsNullOrWhiteSpace(_settings.GetDepupSessionCookie())) return;
        try
        {
            var remote = await _cloud.LoadEnvironmentAsync(requestedCloudProject);
            if (!string.Equals(requestedProjectKey, ProjectKey, StringComparison.OrdinalIgnoreCase)) return;
            if (remote.Count == 0) return;
            _store.SaveEnvironment(requestedProjectKey, remote);
            _variables.Clear();
            foreach (var value in remote) _variables.Add(value);
            _variableView.Refresh();
            UpdateDashboard();
            StatusText.Text = $"{remote.Count} variabel dimuat dari Depup Cloud; simpan lokal untuk membuat salinan terenkripsi di perangkat ini.";
            EnvironmentStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            if (!string.Equals(requestedProjectKey, ProjectKey, StringComparison.OrdinalIgnoreCase)) return;
            StatusText.Text = "Data lokal tetap tersedia. Sinkronisasi cloud gagal: " + ex.Message;
        }
    }
}
