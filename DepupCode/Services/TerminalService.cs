using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DepupCode.Services;

public sealed class TerminalService : IDisposable
{
    private Process? _process;
    private string _workingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly List<int> _suspendedPids = [];

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? WorkingDirectoryChanged;

    public bool IsRunning => IsProcessRunning(_process);
    public string WorkingDirectory => _workingDirectory;

    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled);

    private static readonly (string Garbled, string Clean)[] MojibakeReplacements =
    [
        ("â–²", "▲"),
        ("â–³", "△"),
        ("â–¶", "▶"),
        ("â–¸", "▸"),
        ("âœ“", "✓"),
        ("âœ”", "✔"),
        ("âœ•", "✕"),
        ("âœ–", "✖"),
        ("â—‹", "○"),
        ("â—<", "○"),
        ("â—", "●"),
        ("â—†", "◆"),
        ("â—‡", "◇"),
        ("â”€", "─"),
        ("â”‚", "│"),
        ("â”Œ", "┌"),
        ("â”", "┐"),
        ("â””", "└"),
        ("â”˜", "┘"),
        ("â”œ", "├"),
        ("â”¤", "┤"),
        ("â”¬", "┬"),
        ("â”´", "┴"),
        ("â•", "═"),
        ("â†’", "→"),
        ("â†", "←"),
        ("â†‘", "↑"),
        ("â†“", "↓"),
        ("âš¡", "⚡"),
        ("âš ", "⚠"),
        ("â€¢", "•"),
        ("â€”", "—"),
        ("â€“", "–"),
        ("â€¦", "…"),
        ("Â·", "·")
    ];

    public void Start(string workingDirectory)
    {
        var targetDir = Directory.Exists(workingDirectory)
            ? Path.GetFullPath(workingDirectory)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dirChanged = !string.Equals(_workingDirectory, targetDir, StringComparison.OrdinalIgnoreCase);
        _workingDirectory = targetDir;

        if (IsRunning)
        {
            if (dirChanged)
            {
                _ = SendRawLineAsync($"Set-Location -LiteralPath '{_workingDirectory.Replace("'", "''")}'");
                WorkingDirectoryChanged?.Invoke(this, _workingDirectory);
            }
            return;
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var start = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -ExecutionPolicy Bypass -Command -")
        {
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = utf8NoBom,
            CreateNoWindow = true
        };

        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["LANG"] = "en_US.UTF-8";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["FORCE_COLOR"] = "0";
        start.Environment["TERM"] = "dumb";

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            var cleaned = SanitizeTerminalOutput(e.Data);
            if (cleaned == "__DEPUP_INIT_READY__") return;
            if (cleaned.StartsWith("__DEPUP_CWD__:", StringComparison.Ordinal))
            {
                var newCwd = cleaned["__DEPUP_CWD__:".Length..].Trim();
                if (Directory.Exists(newCwd))
                {
                    _workingDirectory = newCwd;
                    WorkingDirectoryChanged?.Invoke(this, _workingDirectory);
                }
                return;
            }
            OutputReceived?.Invoke(this, cleaned);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            OutputReceived?.Invoke(this, SanitizeTerminalOutput(e.Data));
        };
        _process.Exited += (_, _) => OutputReceived?.Invoke(this, "[process exited]");

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        // Configure UTF-8 console encoding inside PowerShell so Next.js / Vite / npm / git symbols render cleanly
        _ = SendRawLineAsync(
            "try { chcp 65001 >$null; [Console]::InputEncoding = [Console]::OutputEncoding = $OutputEncoding = [System.Text.UTF8Encoding]::new($false) } catch {}");
        WorkingDirectoryChanged?.Invoke(this, _workingDirectory);
    }

    public static string SanitizeTerminalOutput(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var text = AnsiEscapeRegex.Replace(raw, string.Empty);
        foreach (var (garbled, clean) in MojibakeReplacements)
        {
            if (text.Contains(garbled, StringComparison.Ordinal))
                text = text.Replace(garbled, clean, StringComparison.Ordinal);
        }
        return text.Replace("\0", string.Empty);
    }

    private async Task SendRawLineAsync(string line)
    {
        var process = _process;
        if (process is null || !IsProcessRunning(process)) return;
        try
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
        }
        catch { }
    }

    public async Task RunAsync(string command)
    {
        if (!IsRunning) Start(_workingDirectory);
        if (_process is null) return;

        var trimmed = command.Trim();
        if (trimmed.Equals("fg", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("bg", StringComparison.OrdinalIgnoreCase))
        {
            if (ResumeSuspendedProcesses())
            {
                OutputReceived?.Invoke(this, "[RESUMED] Melanjutkan proses yang di-suspend (Ctrl+Z)...");
                return;
            }
        }

        // Run the command, then capture its result and working directory without adding noisy prompt text.
        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.WriteLineAsync("$__depupCommandSucceeded = $?; $__depupCommandExitCode = $LASTEXITCODE; Write-Output (\"__DEPUP_CWD__:\" + (Get-Location).Path); if (-not $__depupCommandSucceeded) { Write-Output (\"__DEPUP_COMMAND_FAILED__:\" + $__depupCommandExitCode) }");
        await _process.StandardInput.FlushAsync();
    }

    /// <summary>
    /// Sends SIGINT / Ctrl+C behavior like VS Code Terminal:
    /// Terminates active child process trees (such as npm run dev / node.exe / next dev)
    /// without closing the IDE, or resets the shell cleanly if stuck inside a PowerShell loop.
    /// </summary>
    public bool Interrupt()
    {
        ResumeSuspendedProcesses();
        var process = _process;
        if (process is null || !IsProcessRunning(process))
        {
            Start(_workingDirectory);
            return false;
        }

        var killedAnyChild = KillDescendantProcesses(process.Id);
        if (!killedAnyChild)
        {
            try
            {
                process.StandardInput.Write("\x03");
                process.StandardInput.WriteLine();
                process.StandardInput.Flush();
            }
            catch { }
        }
        return killedAnyChild;
    }

    /// <summary>
    /// Sends SIGTSTP / Ctrl+Z behavior: suspends active child processes (can be resumed with 'fg')
    /// and sends EOF (\x1A) to standard input.
    /// </summary>
    public bool Suspend()
    {
        var process = _process;
        if (process is null || !IsProcessRunning(process)) return false;
        var descendants = GetDescendantProcessIds(process.Id);
        var suspendedAny = false;
        foreach (var pid in descendants)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    NtSuspendProcess(proc.Handle);
                    if (!_suspendedPids.Contains(pid))
                        _suspendedPids.Add(pid);
                    suspendedAny = true;
                }
            }
            catch { }
        }

        try
        {
            process.StandardInput.Write("\x1A");
            process.StandardInput.WriteLine();
            process.StandardInput.Flush();
        }
        catch { }

        return suspendedAny;
    }

    public bool ResumeSuspendedProcesses()
    {
        if (_suspendedPids.Count == 0) return false;
        var resumed = false;
        foreach (var pid in _suspendedPids.ToList())
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    NtResumeProcess(proc.Handle);
                    resumed = true;
                }
            }
            catch { }
        }
        _suspendedPids.Clear();
        return resumed;
    }

    public void SendEof()
    {
        var process = _process;
        if (process is null || !IsProcessRunning(process)) return;
        try
        {
            process.StandardInput.Write("\x04");
            process.StandardInput.WriteLine();
            process.StandardInput.Flush();
        }
        catch { }
    }

    public bool HasActiveChildProcess()
    {
        var process = _process;
        if (process is null || !IsProcessRunning(process)) return false;
        return GetDescendantProcessIds(process.Id).Count > 0;
    }

    private static bool KillDescendantProcesses(int rootPid)
    {
        var descendants = GetDescendantProcessIds(rootPid);
        var killed = false;
        // Kill deepest children first (e.g. node.exe before cmd.exe)
        descendants.Reverse();
        foreach (var pid in descendants)
        {
            try
            {
                using var proc = Process.GetProcessById(pid);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                    killed = true;
                }
            }
            catch { }
        }
        return killed;
    }

    private static List<int> GetDescendantProcessIds(int rootPid)
    {
        var parentMap = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return [];

        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    var pid = (int)entry.th32ProcessID;
                    var ppid = (int)entry.th32ParentProcessID;
                    var exe = entry.szExeFile ?? "";
                    // Skip conhost.exe so PowerShell's own console host stays alive
                    if (exe.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!parentMap.TryGetValue(ppid, out var list))
                    {
                        list = [];
                        parentMap[ppid] = list;
                    }
                    list.Add(pid);
                } while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var result = new List<int>();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!parentMap.TryGetValue(current, out var children)) continue;
            foreach (var child in children)
            {
                if (child != rootPid && !result.Contains(child))
                {
                    result.Add(child);
                    queue.Enqueue(child);
                }
            }
        }
        return result;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ntdll.dll", PreserveSig = false)]
    private static extern void NtSuspendProcess(IntPtr processHandle);

    [DllImport("ntdll.dll", PreserveSig = false)]
    private static extern void NtResumeProcess(IntPtr processHandle);

    public void Dispose()
    {
        ResumeSuspendedProcesses();
        var process = _process;
        _process = null;
        try { if (IsProcessRunning(process)) process!.Kill(true); } catch { }
        process?.Dispose();
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null) return false;
        try { return !process.HasExited; }
        catch (InvalidOperationException) { return false; }
    }
}
