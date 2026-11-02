using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DepupCode.Tests;

public static class Program
{
    private static readonly string ReportPath = Path.Combine(AppContext.BaseDirectory, "desktop-runtime-smoke.json");

    [STAThread]
    public static int Main()
    {
        var output = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 28)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(16),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 14,
            Text = "Desktop AppContainer / ConPTY runtime smoke\nStarting..."
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = "DepupCode Runtime Smoke",
            Width = 760,
            Height = 420,
            MinWidth = 560,
            MinHeight = 300,
            Background = output.Background,
            Content = output
        };
        app.MainWindow = window;
        window.Loaded += async (_, _) => await RunAsync(output);
        app.Run(window);
        return 0;
    }

    private static async Task RunAsync(TextBox output)
    {
        var completed = new List<string>();
        var startedAtUtc = DateTimeOffset.UtcNow;
        await WriteReportAsync(new { status = "running", completed, startedAtUtc });
        try
        {
            var result = await Task.Run(async () => await SandboxRuntimeScenarios.RunAllAsync(progress: async item =>
            {
                completed.Add(item);
                await output.Dispatcher.InvokeAsync(() =>
                    output.Text = "Passed: " + string.Join("\nPassed: ", completed) + "\n\nRunning remaining smoke checks...");
                await WriteReportAsync(new { status = "running", completed, startedAtUtc });
            }));
            await output.Dispatcher.InvokeAsync(() => output.Text = "PASS: native runtime smoke\n\n" +
                string.Join("\n", result) + "\n\nRunning xUnit suite...");
            var terminalRendererResult = await VerifyTerminalRendererAsync(Window.GetWindow(output)!);
            completed.Add(terminalRendererResult);
            var suite = await RunTestSuiteAsync();
            if (suite.ExitCode != 0)
                throw new InvalidOperationException("xUnit suite failed (exit " + suite.ExitCode + ").\n" + suite.Output);
            completed.Add("xUnit suite passed: " + suite.Summary);
            await output.Dispatcher.InvokeAsync(() => output.Text = "PASS\n\n" + string.Join("\n", completed));
            await WriteReportAsync(new { status = "passed", completed, testOutput = suite.Output, startedAtUtc, finishedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception exception)
        {
            await output.Dispatcher.InvokeAsync(() => output.Text = "FAIL\n\n" + exception);
            await WriteReportAsync(new { status = "failed", completed, error = exception.ToString(), startedAtUtc, finishedAtUtc = DateTimeOffset.UtcNow });
        }
    }

    private static Task WriteReportAsync<T>(T report) =>
        File.WriteAllTextAsync(ReportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    private static async Task<string> VerifyTerminalRendererAsync(Window owner)
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Terminal");
        if (!File.Exists(Path.Combine(assets, "index.html")))
            throw new FileNotFoundException("Renderer xterm.js tidak ikut dipaketkan ke runner runtime.");

        var webView = new WebView2();
        var window = new Window
        {
            Owner = owner,
            Title = "DepupCode xterm.js Runtime Smoke",
            Width = 760,
            Height = 360,
            Content = webView,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sixelCapability = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialResize = new TaskCompletionSource<(int Columns, int Rows)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendererErrors = new List<string>();
        var outputProcessed = new ConcurrentDictionary<string, TaskCompletionSource<double>>();
        var refreshProcessed = new ConcurrentDictionary<string, TaskCompletionSource<ImagePixelResult>>();
        var initialColumns = 0;
        try
        {
            window.Show();
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 runtime tidak tersedia.");
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.AreDevToolsEnabled = false;
            core.SetVirtualHostNameToFolderMapping("depup-terminal.local", assets,
                CoreWebView2HostResourceAccessKind.DenyCors);
            core.WebMessageReceived += (_, args) =>
            {
                try
                {
                    using var message = JsonDocument.Parse(args.TryGetWebMessageAsString());
                    var root = message.RootElement;
                    if (!root.TryGetProperty("type", out var typeValue)) return;
                    switch (typeValue.GetString())
                    {
                        case "ready": ready.TrySetResult(true); break;
                        case "input":
                            if (root.TryGetProperty("data", out var inputValue))
                            {
                                var inputData = inputValue.GetString() ?? "";
                                if (inputData.Contains("?62;4;9;22c", StringComparison.Ordinal)) sixelCapability.TrySetResult(inputData);
                                if (inputData == "q") input.TrySetResult(inputData);
                            }
                            break;
                        case "renderer-error":
                            if (root.TryGetProperty("message", out var errorValue)) rendererErrors.Add(errorValue.GetString() ?? "unknown renderer error");
                            break;
                        case "output-processed":
                            if (root.TryGetProperty("probeId", out var probeValue) && probeValue.GetString() is { } probeId &&
                                outputProcessed.TryGetValue(probeId, out var processed))
                                processed.TrySetResult(root.TryGetProperty("imageStorageUsage", out var usageValue) ? usageValue.GetDouble() : -1);
                            break;
                        case "refresh-completed":
                            if (root.TryGetProperty("requestId", out var requestValue) && requestValue.GetString() is { } requestId &&
                                refreshProcessed.TryGetValue(requestId, out var renderCompletion))
                                renderCompletion.TrySetResult(new ImagePixelResult(
                                    root.GetProperty("pixels").GetInt32(),
                                    root.GetProperty("width").GetInt32(),
                                    root.GetProperty("height").GetInt32()));
                            break;
                        case "resize":
                            if (!root.TryGetProperty("cols", out var colsValue) || !root.TryGetProperty("rows", out var rowsValue)) return;
                            var cols = colsValue.GetInt32();
                            var rows = rowsValue.GetInt32();
                            if (cols <= 0 || rows <= 0) return;
                            if (initialColumns == 0)
                            {
                                initialColumns = cols;
                                initialResize.TrySetResult((cols, rows));
                            }
                            else if (cols != initialColumns) resized.TrySetResult(true);
                            break;
                    }
                }
                catch (JsonException) { }
            };
            core.Navigate("https://depup-terminal.local/index.html");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var dimensions = await initialResize.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (dimensions.Columns < 20 || dimensions.Rows < 5)
                throw new InvalidOperationException($"Ukuran xterm awal tidak valid ({dimensions.Columns}x{dimensions.Rows}).");

            await SendOutputAndWaitAsync(core, outputProcessed, "\u001b[0c");
            var sixelReport = await sixelCapability.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!sixelReport.Contains("?62;4;9;22c", StringComparison.Ordinal))
                throw new InvalidOperationException("Image addon tidak melaporkan dukungan Sixel ke aplikasi terminal.");

            await SendOutputAndWaitAsync(core, outputProcessed, "\u001b[31mVT renderer READY ✓\u001b[0m\r\n");
            await Task.Delay(100);
            var renderedJson = await core.ExecuteScriptAsync("document.querySelector('.xterm-rows')?.textContent || ''");
            var renderedText = JsonSerializer.Deserialize<string>(renderedJson) ?? "";
            if (!renderedText.Contains("VT renderer READY ✓", StringComparison.Ordinal))
                throw new InvalidOperationException("xterm.js tidak menampilkan output ANSI/Unicode yang dikirim host.");

            var imagePngBytes = CreateRedPng();
            var imagePng = Convert.ToBase64String(imagePngBytes);
            var sixel = "\u001bPq#0;2;100;0;0!60~-!60~\u001b\\\r\n";
            var itermImage = $"\u001b]1337;File=inline=1;size={imagePngBytes.Length};width=8px;height=8px:{imagePng}\u0007\r\n";
            var itermStorageUsage = await SendOutputAndWaitAsync(core, outputProcessed, itermImage);
            var itermStats = await RefreshTerminalAndWaitAsync(core, refreshProcessed);
            if (itermStats.Pixels == 0)
                throw new InvalidOperationException("iTerm IIP tidak menggambar image. Canvas=" +
                    JsonSerializer.Serialize(itermStats) + "; storage=" + itermStorageUsage +
                    "; renderer=" + string.Join(" | ", rendererErrors.TakeLast(5)));

            var itermBefore = itermStats.Pixels;
            await SendOutputAndWaitAsync(core, outputProcessed, sixel);
            var sixelStats = await RefreshTerminalAndWaitAsync(core, refreshProcessed);
            if (sixelStats.Pixels <= itermBefore)
                throw new InvalidOperationException("Sixel tidak menambahkan image ke layer terminal. IIP=" +
                    JsonSerializer.Serialize(itermStats) + "; sesudah Sixel=" + JsonSerializer.Serialize(sixelStats) +
                    "; renderer=" + string.Join(" | ", rendererErrors.TakeLast(5)));

            await core.ExecuteScriptAsync("""
                (() => {
                  const input = document.querySelector('.xterm-helper-textarea');
                  input?.focus();
                  input?.dispatchEvent(new KeyboardEvent('keydown', {
                    key: 'q', code: 'KeyQ', keyCode: 81, which: 81,
                    bubbles: true, cancelable: true
                  }));
                })();
                """);
            var typed = await input.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!typed.Contains('q', StringComparison.Ordinal))
                throw new InvalidOperationException("Input keyboard dari xterm.js tidak sampai ke host.");

            await owner.Dispatcher.InvokeAsync(() => window.Width += 180);
            await resized.Task.WaitAsync(TimeSpan.FromSeconds(8));
            return "xterm.js WebView2 renderer passed: ANSI/Unicode, Sixel/iTerm images, keyboard bridge, and resize";
        }
        finally { window.Close(); }
    }

    private static byte[] CreateRedPng()
    {
        byte[] pixels = [0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255];
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task<double> SendOutputAndWaitAsync(CoreWebView2 core,
        ConcurrentDictionary<string, TaskCompletionSource<double>> pending, string data)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "output", data, probeId = id }));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { pending.TryRemove(id, out _); }
    }

    private static async Task<ImagePixelResult> RefreshTerminalAndWaitAsync(CoreWebView2 core,
        ConcurrentDictionary<string, TaskCompletionSource<ImagePixelResult>> refreshPending)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<ImagePixelResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshPending[id] = completion;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "refresh", requestId = id, includeImageStats = true }));
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { refreshPending.TryRemove(id, out _); }
    }

    private sealed record ImagePixelResult(int Pixels, int Width, int Height);

    private static async Task<(int ExitCode, string Summary, string Output)> RunTestSuiteAsync()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var project = Path.Combine(repositoryRoot, "tests", "DepupCode.Tests", "DepupCode.Tests.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "test", project, "--no-build", "--no-restore", "--configuration", "Debug", "--logger", "console;verbosity=minimal" })
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Tidak dapat memulai dotnet test.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = (await stdout) + Environment.NewLine + (await stderr);
        var summary = output.Split('\n').Select(line => line.Trim())
            .LastOrDefault(line => line.StartsWith("Passed!", StringComparison.Ordinal) || line.StartsWith("Failed!", StringComparison.Ordinal))
            ?? "hasil suite tidak ditemukan pada output dotnet test";
        return (process.ExitCode, summary, output);
    }
}
