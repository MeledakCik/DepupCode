using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using DepupCode.Services;

namespace DepupCode.Views;

public partial class PrivacyConsentWindow : Window
{
    private readonly bool _requireAcceptance;

    public PrivacyConsentWindow(bool requireAcceptance = true)
    {
        InitializeComponent();
        _requireAcceptance = requireAcceptance;
        PolicyTextBox.Text = FormatPolicyForDisplay(LoadPolicy());
        PolicyVersionText.Text = "Versi kebijakan: " + SettingsService.PrivacyPolicyVersion;
        if (!requireAcceptance)
        {
            WindowHeading.Text = "Kebijakan privasi";
            AcceptPolicyCheckBox.Visibility = Visibility.Collapsed;
            DeclineButton.Visibility = Visibility.Collapsed;
            ContinueButton.Content = "Tutup";
            ContinueButton.IsEnabled = true;
        }
    }

    private static string LoadPolicy()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "PRIVACY.md");
        if (File.Exists(bundledPath)) return File.ReadAllText(bundledPath);

        return "Kebijakan privasi tidak ditemukan di folder aplikasi. Buka versi online untuk membaca kebijakan sebelum melanjutkan.\n\n" +
               "https://github.com/MeledakCik/DepupCode/blob/main/PRIVACY.md";
    }

    /// <summary>
    /// PRIVACY.md ditampilkan sebagai teks biasa. Ubah penanda Markdown yang umum
    /// menjadi format yang nyaman dibaca tanpa menambah dependency renderer.
    /// </summary>
    private static string FormatPolicyForDisplay(string markdown)
    {
        var builder = new StringBuilder();
        foreach (var rawLine in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                line = line[4..].ToUpperInvariant();
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal))
            {
                if (builder.Length > 0) builder.AppendLine();
                line = line[(line.StartsWith("# ", StringComparison.Ordinal) ? 2 : 3)..].ToUpperInvariant();
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = "• " + line[2..];
            }

            line = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
            line = Regex.Replace(line, @"\[(.+?)\]\((.+?)\)", "$1 ($2)");
            builder.AppendLine(line);
        }
        return builder.ToString().Trim();
    }

    private void AcceptPolicyCheckBox_Changed(object sender, RoutedEventArgs e) =>
        ContinueButton.IsEnabled = AcceptPolicyCheckBox.IsChecked == true;

    private void OpenPolicyOnline_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://github.com/MeledakCik/DepupCode/blob/main/PRIVACY.md")
        {
            UseShellExecute = true
        });
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!_requireAcceptance || AcceptPolicyCheckBox.IsChecked == true) DialogResult = true;
    }
}
