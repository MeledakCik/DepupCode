using System.Diagnostics;
using System.IO;
using System.Text;
using DepupCode.Services;
using Newtonsoft.Json.Linq;

namespace DepupCode.Tests;

public static class SandboxRuntimeScenarios
{
    public static async Task<IReadOnlyList<string>> RunAllAsync(
        CancellationToken cancellationToken = default, Func<string, Task>? progress = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Sandbox runtime smoke hanya berjalan pada Windows.");
        if (WindowsExtensionSandbox.IsCurrentProcessAppContainer())
            throw new InvalidOperationException("Jalankan smoke dari proses desktop biasa; host yang sudah berada dalam AppContainer tidak dapat memverifikasi pembuatan sandbox anak.");

        var completed = new List<string>();
        completed.Add(await VerifyAppContainerAsync(cancellationToken));
        if (progress is not null) await progress(completed[^1]);
        completed.Add(await VerifyConPtyAsync(cancellationToken));
        if (progress is not null) await progress(completed[^1]);
        completed.Add(await VerifyExternalConsoleAsync(cancellationToken));
        if (progress is not null) await progress(completed[^1]);
        return completed;
    }

    public static async Task<string> VerifyAppContainerAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "DepupCode.RuntimeSmoke." + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        try
        {
            await VerifyAppContainerProcessAsync(root, workspace, cancellationToken);
            return "AppContainer launch, workspace write, and sibling-file denial";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static async Task<string> VerifyConPtyAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "DepupCode.ConPtySmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await VerifyConPtyProcessAsync(root, cancellationToken);
            return "ConPTY launch, output capture, and process exit";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public static async Task<string> VerifyExternalConsoleAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "DepupCode.ExternalConsoleSmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = "DEPUP_EXTERNAL_CONSOLE_" + Guid.NewGuid().ToString("N");
            var outputPath = Path.Combine(root, "external-console-smoke.txt");
            var script = "[System.IO.File]::WriteAllText('external-console-smoke.txt','" + marker + "')";
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var command = GetPowerShellPath();
            using var sandbox = WindowsExtensionSandbox.StartCommand(command,
                ["-NoLogo", "-NoProfile", "-EncodedCommand", encodedScript], root, root,
                "runtime-smoke-external:" + Guid.NewGuid().ToString("N"),
                readOnlyPaths: [Path.GetDirectoryName(command)!], allowInternet: false,
                createNewConsole: true, consoleTitle: "DepupCode external runtime smoke");
            await sandbox.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            if (sandbox.Process.ExitCode != 0)
                throw new InvalidOperationException($"External console child returned {sandbox.Process.ExitCode}.");
            if (!File.Exists(outputPath) || !(await File.ReadAllTextAsync(outputPath, cancellationToken)).Contains(marker, StringComparison.Ordinal))
                throw new InvalidOperationException("External console child did not write its approved workspace marker.");
            return "External console launch, AppContainer workspace write, and process exit";
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task VerifyAppContainerProcessAsync(string root, string workspace, CancellationToken cancellationToken)
    {
        var command = GetPowerShellPath();
        var marker = "DEPUP_APP_CONTAINER_" + Guid.NewGuid().ToString("N");
        var outputPath = Path.Combine(workspace, "sandbox-write.txt");
        var outsideMarker = "DEPUP_OUTSIDE_CANARY_" + Guid.NewGuid().ToString("N");
        var outsidePath = Path.Combine(root, "outside-canary.txt");
        await File.WriteAllTextAsync(outsidePath, outsideMarker, cancellationToken);

        var launch = await RunSandboxCommandAsync(command,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", $"[System.IO.File]::WriteAllText('sandbox-write.txt','{marker}')"], workspace,
            "runtime-smoke-write:" + Guid.NewGuid().ToString("N"), cancellationToken);
        if (launch.ExitCode != 0) throw new InvalidOperationException($"AppContainer child returned {launch.ExitCode}: {launch.StandardError}");
        if (!File.Exists(outputPath) || !(await File.ReadAllTextAsync(outputPath, cancellationToken)).Contains(marker, StringComparison.Ordinal))
            throw new InvalidOperationException("AppContainer child could not write to its approved workspace.");
        var probe = await RunSandboxCommandAsync(command,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                "try { [System.IO.File]::ReadAllText('..\\outside-canary.txt') } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 0 }"], workspace,
            "runtime-smoke-denial:" + Guid.NewGuid().ToString("N"), cancellationToken);
        if (probe.StandardOutput.Contains(outsideMarker, StringComparison.Ordinal))
            throw new InvalidOperationException("AppContainer child read a sibling file outside the approved workspace.");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunSandboxCommandAsync(
        string command, IReadOnlyList<string> arguments, string workspace, string sandboxId,
        CancellationToken cancellationToken)
    {
        var readOnlyPaths = new[] { Path.GetDirectoryName(command)! };
        using var sandbox = WindowsExtensionSandbox.StartCommand(command, arguments, workspace, workspace, sandboxId,
            new Dictionary<string, string>(), readOnlyPaths, allowInternet: false, allowPrivateNetwork: false);
        var stdout = ReadAllAsync(sandbox.Output, cancellationToken);
        var stderr = ReadAllAsync(sandbox.Error, cancellationToken);
        await sandbox.Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        var exitCode = sandbox.Process.ExitCode;
        return (exitCode,
            await stdout.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken),
            await stderr.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
    }

    private static async Task<string> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task VerifyConPtyProcessAsync(string workspace, CancellationToken cancellationToken)
    {
        var command = GetPowerShellPath();
        var input = "café-漢";
        var marker = "DEPUP_CONPTY_ECHO_" + input;
        const string readyMarker = "DEPUP_CONPTY_READY";
        var script = "$utf8=[System.Text.UTF8Encoding]::new($false);" +
                     "[Console]::InputEncoding=$utf8;[Console]::OutputEncoding=$utf8;" +
                     "[Console]::WriteLine('" + readyMarker + "');" +
                     "$line=[Console]::ReadLine();[Console]::WriteLine('DEPUP_CONPTY_ECHO_'+$line);exit 0";
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string latestOutput = "";
        await using var terminal = await IntegratedDebugTerminal.StartAsync("DepupCode runtime smoke",
            [command, "-NoLogo", "-NoProfile", "-EncodedCommand", encodedScript],
            workspace, workspace, new JObject(), interpretByShell: false,
            output: text =>
            {
                Volatile.Write(ref latestOutput, text);
                if (text.Contains(readyMarker, StringComparison.Ordinal)) ready.TrySetResult(true);
                if (text.Contains(marker, StringComparison.Ordinal)) received.TrySetResult(true);
            }, cancellationToken);
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("ConPTY child did not signal it was ready. Exited=" + terminal.HasExited +
                ", exit code=" + terminal.ExitCode + ". Last terminal snapshot: " + Volatile.Read(ref latestOutput), ex);
        }
        terminal.Resize(100, 30);
        await terminal.SendInputAsync(input + "\r", cancellationToken);
        try { await received.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("ConPTY child did not echo interactive UTF-8 input. Exited=" + terminal.HasExited +
                ", exit code=" + terminal.ExitCode + ". Last terminal snapshot: " + Volatile.Read(ref latestOutput), ex);
        }
        var exitDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!terminal.HasExited && DateTime.UtcNow < exitDeadline)
            await Task.Delay(50, cancellationToken);
        if (!terminal.HasExited)
            throw new TimeoutException("ConPTY child produced output but did not exit. Last terminal snapshot: " + Volatile.Read(ref latestOutput));
        if (terminal.ExitCode != 0)
            throw new InvalidOperationException($"ConPTY child exited with code {terminal.ExitCode}. Last terminal snapshot: " + Volatile.Read(ref latestOutput));
    }

    private static string GetPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
}
