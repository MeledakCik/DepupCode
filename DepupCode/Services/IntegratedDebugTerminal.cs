using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

/// <summary>Windows ConPTY session used by DAP runInTerminal; exposes interactive byte input and resizable terminal dimensions.</summary>
public sealed class IntegratedDebugTerminal : IAsyncDisposable
{
    private readonly PseudoConsoleSession _session;
    private readonly Action<string> _output;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _outputLength;
    private int _disposeStarted;
    private int _lastColumns = 120;
    private int _lastRows = 32;

    public int ProcessId => _session.Process.Id;
    public string Title { get; }
    public int? ExitCode
    {
        get { try { return _session.Process.HasExited ? _session.Process.ExitCode : null; } catch { return null; } }
    }
    public bool HasExited
    {
        get { try { return _session.Process.HasExited; } catch { return true; } }
    }

    private IntegratedDebugTerminal(PseudoConsoleSession session, string title, Action<string> output)
    {
        _session = session;
        Title = title;
        _output = output;
        // ConPTY uses synchronous pipe handles. Drain them on a dedicated background thread so
        // starting the terminal never blocks the WPF dispatcher or starves the shared thread pool.
        _reader = Task.Factory.StartNew(ReadOutput, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _session.Process.Exited += (_, _) => Forward($"\r\n[process exited ({SafeExitCode()})]\r\n", flush: true);
    }

    public static Task<IntegratedDebugTerminal> StartAsync(string title, IReadOnlyList<string> args, string cwd,
        string workspaceRoot, JObject? environment, bool interpretByShell, Action<string> output,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Start(title, args, cwd, workspaceRoot, environment, interpretByShell, output), cancellationToken);

    public static IntegratedDebugTerminal Start(string title, IReadOnlyList<string> args, string cwd, string workspaceRoot,
        JObject? environment, bool interpretByShell, Action<string> output)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY debug terminal hanya tersedia pada Windows.");
        if (args.Count is < 1 or > 64 || string.IsNullOrWhiteSpace(args[0]) || args.Any(arg => arg.Length > 16_384))
            throw new InvalidDataException("Request runInTerminal memiliki command/arguments yang tidak valid.");
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Folder terminal debug tidak ditemukan: " + cwd);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var removedEnvironmentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SafeEnvironmentNames)
            if (System.Environment.GetEnvironmentVariable(name) is { } value) env[name] = value;
        foreach (var property in environment?.Properties() ?? [])
        {
            var key = property.Name;
            if (!WindowsExtensionSandbox.IsValidEnvironmentName(key))
                throw new InvalidDataException("Environment variable dari adapter tidak valid.");
            if (property.Value.Type == JTokenType.Null) env.Remove(key);
            else
            {
                var value = property.Value.Value<string>() ?? "";
                if (!WindowsExtensionSandbox.IsValidEnvironmentValue(value)) throw new InvalidDataException($"Environment variable '{key}' melewati batas ukuran atau mengandung karakter NUL.");
                env[key] = value;
            }
            if (property.Value.Type == JTokenType.Null) removedEnvironmentNames.Add(key);
            else removedEnvironmentNames.Remove(key);
        }

        string executable;
        IReadOnlyList<string> arguments;
        if (interpretByShell)
        {
            executable = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            arguments = ["/d", "/s", "/c", string.Join(" ", args)];
        }
        else
        {
            executable = LanguageServerService.ResolveExecutable(cwd, args[0])
                ?? throw new FileNotFoundException("Command terminal debug tidak ditemukan: " + args[0]);
            arguments = args.Skip(1).ToArray();
        }

        var readOnlyPaths = new List<string>();
        if (Path.GetDirectoryName(executable) is { Length: > 0 } executableDirectory) readOnlyPaths.Add(executableDirectory);
        var adapterPath = environment?.Properties()
            .FirstOrDefault(property => property.Name.Equals("PATH", StringComparison.OrdinalIgnoreCase))?.Value;
        if (adapterPath is not null && adapterPath.Type != JTokenType.Null && adapterPath.Value<string>() is { Length: > 0 } pathValue)
            readOnlyPaths.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var session = PseudoConsoleSession.Start(executable, arguments, Path.GetFullPath(cwd), Path.GetFullPath(workspaceRoot),
            env, readOnlyPaths, 120, 32, "debug-terminal:" + Path.GetFullPath(workspaceRoot), removedEnvironmentNames);
        try { return new IntegratedDebugTerminal(session, string.IsNullOrWhiteSpace(title) ? args[0] : title, output); }
        catch { session.Dispose(); throw; }
    }

    public async Task SendInputAsync(string input, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || HasExited) throw new InvalidOperationException("Terminal debug sudah berhenti.");
        if (input.Length > 16_384) throw new InvalidDataException("Input terminal melewati batas 16 KB.");
        var bytes = Encoding.UTF8.GetBytes(input);
        await SendInputBytesAsync(bytes, cancellationToken);
    }

    public async Task SendInputBytesAsync(byte[] bytes, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || HasExited) throw new InvalidOperationException("Terminal debug sudah berhenti.");
        if (bytes.Length > 65_536) throw new InvalidDataException("Input terminal melewati batas 64 KB.");
        await _session.Input.WriteAsync(bytes, cancellationToken);
        await _session.Input.FlushAsync(cancellationToken);
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposeStarted) != 0 || HasExited) return;
        columns = Math.Clamp(columns, 20, 300);
        rows = Math.Clamp(rows, 5, 120);
        if (columns == _lastColumns && rows == _lastRows) return;
        _lastColumns = columns;
        _lastRows = rows;
        _session.Resize(columns, rows);
    }

    private void ReadOutput()
    {
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = _session.Output.Read(bytes, 0, bytes.Length);
                if (count == 0) break;
                var charCount = decoder.GetChars(bytes, 0, count, chars, 0, flush: false);
                if (charCount > 0) Forward(new string(chars, 0, charCount));
            }
            var finalCount = decoder.GetChars([], 0, 0, chars, 0, flush: true);
            if (finalCount > 0) Forward(new string(chars, 0, finalCount), flush: true);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException ex) { if (!_lifetime.IsCancellationRequested) Forward("\r\n[terminal] " + ex.Message + "\r\n", flush: true); }
        finally { Forward("", flush: true); }
    }

    private void Forward(string text, bool flush = false)
    {
        if (text.Length == 0) return;
        if (text.Length > 0 && Interlocked.Add(ref _outputLength, text.Length) > 2_000_000) return;
        _output(text);
    }

    private int SafeExitCode()
    {
        try { return _session.Process.ExitCode; } catch { return -1; }
    }

    private static readonly string[] SafeEnvironmentNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
    ];

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _lifetime.Cancel();
        try { await _session.StopAsync().ConfigureAwait(false); } catch { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await Task.Run(_session.Dispose).ConfigureAwait(false); } catch { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _lifetime.Dispose();
    }

    private sealed class PseudoConsoleSession : IDisposable
    {
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint PseudoConsoleAttribute = 0x00020016;
        private const int ErrorInsufficientBuffer = 122;
        private IntPtr _pseudoConsole;
        private readonly WindowsExtensionSandbox _sandbox;
        private readonly object _sync = new();
        private bool _stopped;
        private bool _disposed;

        public Process Process { get; }
        public Stream Input { get; }
        public Stream Output { get; }

        private PseudoConsoleSession(IntPtr pseudoConsole, SafeFileHandle input, SafeFileHandle output, WindowsExtensionSandbox sandbox)
        {
            _pseudoConsole = pseudoConsole;
            _sandbox = sandbox;
            Process = sandbox.Process;
            Input = new FileStream(input, FileAccess.Write, 4096, isAsync: false);
            Output = new FileStream(output, FileAccess.Read, 8192, isAsync: false);
        }

        public static PseudoConsoleSession Start(string executable, IReadOnlyList<string> args, string cwd,
            string workspaceRoot, IReadOnlyDictionary<string, string> environment, IReadOnlyList<string> readOnlyPaths,
            int columns, int rows, string sandboxId, IReadOnlyCollection<string> removedEnvironmentVariables)
        {
            CreatePipeChecked(out var consoleInput, out var hostInput);
            SafeFileHandle hostOutput = null!;
            SafeFileHandle consoleOutput = null!;
            try { CreatePipeChecked(out hostOutput, out consoleOutput); }
            catch
            {
                consoleInput.Dispose(); hostInput.Dispose();
                throw;
            }
            var size = new Coord { X = (short)columns, Y = (short)rows };
            var createResult = CreatePseudoConsole(size, consoleInput, consoleOutput, 0, out var hpc);
            if (createResult < 0)
            {
                consoleInput.Dispose(); hostInput.Dispose(); hostOutput.Dispose(); consoleOutput.Dispose();
                Marshal.ThrowExceptionForHR(createResult);
            }

            WindowsExtensionSandbox? sandbox = null;
            try
            {
                sandbox = WindowsExtensionSandbox.StartCommand(executable, args, workspaceRoot, cwd, sandboxId,
                    environment, readOnlyPaths, allowInternet: true, allowPrivateNetwork: true, pseudoConsole: hpc,
                    removedEnvironmentVariables: removedEnvironmentVariables);

                // The pseudoconsole duplicates its pipe handles; close the child-only ends after process startup.
                consoleInput.Dispose();
                consoleOutput.Dispose();
                var session = new PseudoConsoleSession(hpc, hostInput, hostOutput, sandbox);
                sandbox = null;
                hpc = IntPtr.Zero;
                return session;
            }
            catch
            {
                if (sandbox is not null)
                {
                    try { if (!sandbox.Process.HasExited) sandbox.Process.Kill(entireProcessTree: true); } catch { }
                    try { sandbox.Process.WaitForExit(3000); } catch { }
                }
                try { hostInput.Dispose(); } catch { }
                try { consoleInput.Dispose(); } catch { }
                try { consoleOutput.Dispose(); } catch { }
                if (hpc != IntPtr.Zero) ClosePseudoConsole(hpc);
                try { hostOutput.Dispose(); } catch { }
                try { sandbox?.Dispose(); } catch { }
                throw;
            }
        }

        public void Resize(int columns, int rows)
        {
            lock (_sync)
            {
                if (_stopped || _pseudoConsole == IntPtr.Zero) return;
                var result = ResizePseudoConsole(_pseudoConsole, new Coord { X = (short)columns, Y = (short)rows });
                if (result < 0) Marshal.ThrowExceptionForHR(result);
            }
        }

        private static string MakeEnvironmentBlock(IReadOnlyDictionary<string, string> environment) =>
            string.Join('\0', environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";

        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            var slashCount = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashCount++; continue; }
                if (character == '"') result.Append('\\', slashCount * 2 + 1).Append('"');
                else result.Append('\\', slashCount).Append(character);
                slashCount = 0;
            }
            result.Append('\\', slashCount * 2).Append('"');
            return result.ToString();
        }

        private static void ThrowLastWin32(string operation) => throw new Win32Exception(Marshal.GetLastWin32Error(), operation);

        private static void CreatePipeChecked(out SafeFileHandle read, out SafeFileHandle write)
        {
            if (!CreatePipe(out var readHandle, out var writeHandle, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Membuat pipe ConPTY.");
            read = new SafeFileHandle(readHandle, ownsHandle: true);
            write = new SafeFileHandle(writeHandle, ownsHandle: true);
        }

        public async Task StopAsync()
        {
            lock (_sync)
            {
                if (_stopped || _disposed) return;
                _stopped = true;
            }

            try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
            try { await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
            try { Input.Dispose(); } catch { }
            lock (_sync)
            {
                if (_pseudoConsole != IntPtr.Zero)
                {
                    try { ClosePseudoConsole(_pseudoConsole); } catch { }
                    _pseudoConsole = IntPtr.Zero;
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            if (!_stopped)
            {
                try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { }
                try { Process.WaitForExit(3000); } catch { }
                try { Input.Dispose(); } catch { }
                lock (_sync)
                {
                    if (_pseudoConsole != IntPtr.Zero)
                    {
                        try { ClosePseudoConsole(_pseudoConsole); } catch { }
                        _pseudoConsole = IntPtr.Zero;
                    }
                }
            }
            try { Output.Dispose(); } catch { }
            try { _sandbox.Dispose(); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Cb; public string? Reserved; public string? Desktop; public string? Title; public uint X; public uint Y; public uint XSize; public uint YSize; public uint XCountChars; public uint YCountChars; public uint FillAttribute; public uint DwFlags; public short ShowWindow; public short Reserved2; public IntPtr Reserved2Pointer; public IntPtr HStdInput; public IntPtr HStdOutput; public IntPtr HStdError; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process; public IntPtr Thread; public uint ProcessId; public uint ThreadId; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr pseudoConsole);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr pseudoConsole);
        [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, uint attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation processInformation);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    }

}
