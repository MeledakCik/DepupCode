using System.IO;
using System.Windows;
using System.Windows.Controls;
using DepupCode.Models;
using DepupCode.Services;
using Microsoft.Win32;

namespace DepupCode.Views;

/// <summary>
/// Onboarding pertama setelah kebijakan privasi diterima. Memilih workspace awal dan
/// preferensi dasar sebelum MainWindow dibuat, seperti wizard "Set up" pada IDE lain.
/// </summary>
public partial class FirstRunSetupWindow : Window
{
    private readonly SettingsService _settings;
    private bool _completed;

    public string? SelectedFolder { get; private set; }

    public FirstRunSetupWindow(SettingsService settings, AppSettings current)
    {
        InitializeComponent();
        _settings = settings;

        ThemeBox.ItemsSource = UiThemePalette.Names;
        ThemeBox.SelectedItem = UiThemePalette.Normalize(current.AppTheme);
        FontSizeBox.ItemsSource = Enumerable.Range(10, 9).Select(size => size.ToString()).ToArray();
        FontSizeBox.SelectedItem = Math.Clamp((int)Math.Round(current.EditorFontSize), 10, 18).ToString();

        var recents = current.RecentWorkspaces
            .Where(item => !string.IsNullOrWhiteSpace(item.FullPath) && Directory.Exists(item.FullPath))
            .Take(8)
            .ToList();
        if (recents.Count > 0)
        {
            RecentList.ItemsSource = recents;
            RecentPanel.Visibility = Visibility.Visible;
        }

        if (Directory.Exists(current.LastFolder))
            PathBox.Text = current.LastFolder;

        UpdatePathStatus();
        Loaded += (_, _) =>
        {
            PathBox.Focus();
            PathBox.CaretIndex = PathBox.Text.Length;
        };
    }

    private static string NormalizeFolder(string? value)
    {
        var folder = (value ?? "").Trim().Trim('"');
        if (folder.Length == 0) return "";
        try
        {
            var full = Path.GetFullPath(folder);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch { return folder; }
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePathStatus();

    private void UpdatePathStatus()
    {
        var folder = NormalizeFolder(PathBox.Text);
        if (folder.Length == 0)
        {
            PathStatusText.Text = "Belum ada folder dipilih.";
            PathStatusText.Foreground = (System.Windows.Media.Brush)FindResource("ThemeMutedBrush");
            StartButton.IsEnabled = false;
            FooterStatusText.Text = "Pilih folder untuk memulai, atau lewati dan pilih folder nanti dari Welcome screen.";
            return;
        }

        if (Directory.Exists(folder))
        {
            PathStatusText.Text = "Folder siap dibuka: " + folder;
            PathStatusText.Foreground = (System.Windows.Media.Brush)FindResource("BrandLime");
            StartButton.IsEnabled = true;
            FooterStatusText.Text = "File project tidak dipindahkan. Depup Code hanya membaca folder ini.";
        }
        else
        {
            PathStatusText.Text = "Folder tidak ditemukan. Periksa kembali path atau gunakan tombol Telusuri.";
            PathStatusText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 113, 113));
            StartButton.IsEnabled = false;
            FooterStatusText.Text = "Pilih folder yang sudah ada di komputer ini.";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = NormalizeFolder(PathBox.Text);
        var dialog = new OpenFolderDialog
        {
            Title = "Pilih folder workspace",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true) return;

        PathBox.Text = dialog.FolderName;
        RecentList.SelectedItem = null;
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentWorkspaceItem item && Directory.Exists(item.FullPath))
            PathBox.Text = item.FullPath;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var folder = NormalizeFolder(PathBox.Text);
        if (!Directory.Exists(folder))
        {
            UpdatePathStatus();
            return;
        }
        Complete(folder);
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Complete(null);

    private void Complete(string? folder)
    {
        var settings = _settings.Load();
        settings.HasCompletedFirstRunSetup = true;
        if (!string.IsNullOrWhiteSpace(folder)) settings.LastFolder = folder;
        if (ThemeBox.SelectedItem is string theme) settings.AppTheme = UiThemePalette.Normalize(theme);
        if (int.TryParse(FontSizeBox.SelectedItem as string, out var fontSize))
            settings.EditorFontSize = Math.Clamp(fontSize, 10, 18);
        _settings.Save(settings);

        _completed = true;
        SelectedFolder = folder;
        DialogResult = true;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_completed) return;

        // Menutup jendela dianggap "lewati": setup tidak ditampilkan lagi, tetapi
        // Welcome screen tetap menyediakan tombol Buka Folder kapan saja.
        var settings = _settings.Load();
        settings.HasCompletedFirstRunSetup = true;
        _settings.Save(settings);
        _completed = true;
    }
}
