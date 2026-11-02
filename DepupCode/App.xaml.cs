using System.Windows;
using DepupCode.Services;

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
            try
            {
                await new DepupCloudService(new SettingsService()).CompleteLoginAsync(deepLink);
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

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
