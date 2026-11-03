using System.IO;
using System.Windows;
using DepupCode.Services;
using DepupCode.Views;

namespace DepupCode;

public partial class App : Application
{
    // StartupUri dihapus dari App.xaml: jendela dibuat di sini supaya argumen depup://auth
    // (callback login Google dari browser) bisa ditangani lebih dulu.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var deepLink = e.Args.FirstOrDefault(arg => arg.StartsWith("depup://auth", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(deepLink))
        {
            var callbackSettings = new SettingsService();
            var callbackProfile = callbackSettings.Load();
            if (!callbackProfile.HasAcceptedPrivacyPolicy ||
                !string.Equals(callbackProfile.AcceptedPrivacyPolicyVersion, SettingsService.PrivacyPolicyVersion, StringComparison.Ordinal))
            {
                Shutdown();
                return;
            }

            try
            {
                await new DepupCloudService(callbackSettings).CompleteLoginAsync(deepLink);
                MessageBox.Show("Akun Depup Cloud berhasil dihubungkan. Kembali ke jendela Depup Code yang sudah terbuka.",
                    "Depup Code", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Login Depup Cloud tidak selesai: " + ex.Message,
                    "Depup Code", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        var settings = new SettingsService();
        var currentSettings = settings.Load();
        if (!currentSettings.HasAcceptedPrivacyPolicy ||
            !string.Equals(currentSettings.AcceptedPrivacyPolicyVersion, SettingsService.PrivacyPolicyVersion, StringComparison.Ordinal))
        {
            var consent = new PrivacyConsentWindow();
            if (consent.ShowDialog() != true)
            {
                Shutdown();
                return;
            }

            currentSettings.HasAcceptedPrivacyPolicy = true;
            currentSettings.AcceptedPrivacyPolicyVersion = SettingsService.PrivacyPolicyVersion;
            settings.Save(currentSettings);
        }

        // "Open with Depup Code" / shortcut with a folder argument. Buka folder tersebut
        // dan lewati pemilihan workspace, tetapi tetap tampilkan kebijakan privasi di atas.
        var requestedFolder = e.Args
            .Where(arg => !string.IsNullOrWhiteSpace(arg))
            .Select(NormalizeFolderArgument)
            .FirstOrDefault(folder => folder.Length > 0 && Directory.Exists(folder));
        if (requestedFolder is not null)
        {
            currentSettings.LastFolder = requestedFolder;
            currentSettings.HasCompletedFirstRunSetup = true;
            settings.Save(currentSettings);
        }
        else if (!currentSettings.HasCompletedFirstRunSetup)
        {
            var setup = new FirstRunSetupWindow(settings, currentSettings);
            setup.ShowDialog();
            currentSettings = settings.Load();
            if (!string.IsNullOrWhiteSpace(setup.SelectedFolder))
                currentSettings.LastFolder = setup.SelectedFolder;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static string NormalizeFolderArgument(string argument)
    {
        var value = argument.Trim().Trim('"');
        if (value.Length == 0 || value.StartsWith('-') || value.StartsWith("depup://", StringComparison.OrdinalIgnoreCase))
            return "";
        try
        {
            var full = Path.GetFullPath(value);
            return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }
        catch { return ""; }
    }
}
