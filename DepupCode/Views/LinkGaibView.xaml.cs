using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DepupCode.Services;
using QRCoder;

namespace DepupCode.Views;

public partial class LinkGaibView : UserControl
{
    private Process? _process;
    private bool _tunnelEventsAttached;

    public LinkGaibView()
    {
        InitializeComponent();
        Loaded += (_, _) => AttachTunnelEvents();
        Unloaded += (_, _) => DetachTunnelEvents();
    }

    private void AttachTunnelEvents()
    {
        if (_tunnelEventsAttached) return;
        DepupCloudService.Shared.TunnelLog += OnNativeTunnelLog;
        DepupCloudService.Shared.TunnelStateChanged += OnNativeTunnelState;
        _tunnelEventsAttached = true;
        if (DepupCloudService.Shared.ActiveTunnel is { } active)
        {
            PortBox.Text = active.LocalPort.ToString();
            ShowActiveUrl(active.Url, $"Link Gaib aktif untuk localhost:{active.LocalPort} via depup.cloud.");
            ShareButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetDaemonBadge(true);
        }
        CloudControlGate.Refresh(this);
    }

    private void DetachTunnelEvents()
    {
        if (!_tunnelEventsAttached) return;
        DepupCloudService.Shared.TunnelLog -= OnNativeTunnelLog;
        DepupCloudService.Shared.TunnelStateChanged -= OnNativeTunnelState;
        _tunnelEventsAttached = false;
    }

    public async Task PauseForOfflineAsync()
    {
        await StopAllAsync();
        StatusText.Text = "Mode Offline aktif. Tunnel dihentikan; Link Gaib memerlukan koneksi Depup Cloud.";
        OutputBox.AppendText("[OFFLINE MODE] Tunnel lokal dihentikan dan fitur cloud dijeda." + Environment.NewLine);
        CloudControlGate.Refresh(this);
    }

    private void OnNativeTunnelLog(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            OutputBox.AppendText(message + Environment.NewLine);
            OutputBox.ScrollToEnd();
        });
    }

    private void OnNativeTunnelState(object? sender, DepupTunnelSession? session)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (session is not null)
            {
                ShowActiveUrl(session.Url, $"Link Gaib aktif untuk localhost:{session.LocalPort} (ID: {session.Id}).");
                ShareButton.IsEnabled = false;
                StopButton.IsEnabled = true;
                SetDaemonBadge(true);
            }
            else if (_process is null)
            {
                ShareButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                SetDaemonBadge(false);
            }
            CloudControlGate.Refresh(this);
        });
    }

    private void SetDaemonBadge(bool active)
    {
        DaemonDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#34D399" : "#9490B8"));
        DaemonBadgeText.Text = active ? "Connected (depup.cloud)" : "Tunnel Idle";
        DaemonBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(active ? "#34D399" : "#9490B8"));
    }

    private void Responsive_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var compact = ActualWidth < 850;
        TunnelColumn.Width = new GridLength(compact ? 1 : 2, GridUnitType.Star);
        QrColumn.Width = new GridLength(compact ? 0 : 1, GridUnitType.Star);
        Grid.SetRow(QrPanel, compact ? 2 : 1);
        Grid.SetColumn(QrPanel, compact ? 0 : 1);
        TunnelPanel.Margin = compact ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 14, 0);
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (new SettingsService().Load().IsOfflineMode)
        {
            StatusText.Text = "Mode Offline aktif. Link Gaib memerlukan koneksi Depup Cloud; ubah ke Mode Online untuk berbagi localhost.";
            return;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "Port harus berupa angka 1 sampai 65535.";
            return;
        }
        await StopAllAsync();
        if (new SettingsService().Load().IsOfflineMode)
        {
            StatusText.Text = "Mode Offline aktif. Link Gaib memerlukan koneksi Depup Cloud.";
            CloudControlGate.Refresh(this);
            return;
        }
        OutputBox.Clear();
        ShareLink.Text = "";
        CopyLinkButton.Visibility = Visibility.Collapsed;
        QrImage.Source = null;

        var prefix = SubdomainBox.Text.Trim();

        if (UseCliCheck.IsChecked != true)
        {
            ShareButton.IsEnabled = false;
            StatusText.Text = $"Menghubungkan tunnel native ke {DepupCloudService.Shared.GetBaseUrl()}/api/tunnel/create…";
            try
            {
                var session = await DepupCloudService.Shared.StartTunnelAsync(port, prefix, 120);
                ShowActiveUrl(session.Url, $"Link HTTPS aktif di depup.cloud! Meneruskan trafik ke http://localhost:{port}.");
                StopButton.IsEnabled = true;
                SetDaemonBadge(true);
                return;
            }
            catch (Exception ex)
            {
                OutputBox.AppendText($"[Native Tunnel Notice] {ex.Message}{Environment.NewLine}");
                OutputBox.AppendText(new SettingsService().Load().IsOfflineMode
                    ? "[OFFLINE MODE] Fallback CLI tidak dijalankan." + Environment.NewLine
                    : "Mencoba fallback via npx depup-cli share…" + Environment.NewLine);
            }
        }

        // CLI mode or fallback
        if (new SettingsService().Load().IsOfflineMode)
        {
            StatusText.Text = "Mode Offline aktif. CLI tunnel tidak dijalankan.";
            ShareButton.IsEnabled = true;
            CloudControlGate.Refresh(this);
            return;
        }
        var nameArg = string.IsNullOrWhiteSpace(prefix) ? "" : $" --name {prefix}";
        StatusText.Text = $"Menjalankan npx depup-cli share {port}{nameArg}…";
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c npx depup-cli share {port}{nameArg}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.CurrentDirectory
        };
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutput;
        _process.ErrorDataReceived += OnOutput;
        _process.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            ShareButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SetDaemonBadge(false);
            StatusText.Text = new SettingsService().Load().IsOfflineMode
                ? "Mode Offline aktif. Tunnel dihentikan; Link Gaib memerlukan koneksi Depup Cloud."
                : "Proses Link Gaib selesai.";
            CloudControlGate.Refresh(this);
        });
        try
        {
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            ShareButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            SetDaemonBadge(true);
            StatusText.Text = $"Link Gaib aktif untuk localhost:{port}.";
        }
        catch (Exception ex)
        {
            ShareButton.IsEnabled = true;
            StatusText.Text = "Tidak dapat menjalankan npx: " + ex.Message;
            CloudControlGate.Refresh(this);
        }
    }

    private void ShowActiveUrl(string url, string statusMessage)
    {
        ShareLink.Text = url;
        CopyLinkButton.Visibility = string.IsNullOrWhiteSpace(url) ? Visibility.Collapsed : Visibility.Visible;
        if (!string.IsNullOrWhiteSpace(url))
            QrImage.Source = MakeQr(url);
        StatusText.Text = statusMessage;
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            OutputBox.AppendText(e.Data + Environment.NewLine);
            OutputBox.ScrollToEnd();
            var match = Regex.Match(e.Data, @"https://[^\s""'<>]+", RegexOptions.IgnoreCase);
            if (!match.Success) return;
            var url = match.Value.TrimEnd('.', ',', ')', ']', ';');
            ShowActiveUrl(url, "Link HTTPS siap dibagikan. Tekan Hentikan untuk mematikan tunnel.");
        });
    }

    private static BitmapImage MakeQr(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var bytes = new PngByteQRCode(data).GetGraphic(10);
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) => await StopAllAsync();

    private async Task StopAllAsync()
    {
        await DepupCloudService.Shared.StopTunnelAsync();
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        _process?.Dispose();
        _process = null;
        ShareButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetDaemonBadge(false);
        StatusText.Text = "Tunnel dihentikan.";
        CloudControlGate.Refresh(this);
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ShareLink.Text))
        {
            Clipboard.SetText(ShareLink.Text);
            StatusText.Text = "URL tunnel disalin ke clipboard!";
        }
    }

    private void OpenLink(object sender, MouseButtonEventArgs e)
    {
        if (Uri.TryCreate(ShareLink.Text, UriKind.Absolute, out var uri))
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
