using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record DebugLaunchConfiguration(string Name, string Type, string Request, JObject Arguments);
public sealed record DebugStackFrame(string Name, string SourcePath, int Line, int Column, int FrameId)
{
    public override string ToString() => string.IsNullOrWhiteSpace(SourcePath)
        ? Name
        : $"{Name}  ·  {System.IO.Path.GetFileName(SourcePath)}:{Line}";
}
public sealed record DebugVariable(string Name, string Value, string Type, int VariablesReference)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Type) ? $"{Name} = {Value}" : $"{Name}: {Type} = {Value}";
}
public sealed record DebugBreakpoint(string Path, int Line)
{
    public override string ToString() => $"{System.IO.Path.GetFileName(Path)}:{Line}";
}

/// <summary>DAP host for stdio, TCP, and named-pipe adapters.</summary>
public sealed class DebugAdapterService : IAsyncDisposable
{
    private const int MaxConfigBytes = 64 * 1024;
    private readonly string _root;
    private readonly Dictionary<string, DebugAdapterDefinition> _adapters;
    private readonly List<DebugLaunchConfiguration> _configurations;
    private DapConnection? _connection;
    private bool _disposed;
    private volatile bool _sessionEnded;
    private int _currentThreadId = 1;

    public event Action<string>? StatusChanged;
    public event Action<string>? OutputReceived;
    public event Action<IReadOnlyList<DebugStackFrame>, IReadOnlyList<DebugVariable>>? Stopped;

    public bool IsRunning => !_sessionEnded && _connection is { HasExited: false };
    public IReadOnlyList<DebugLaunchConfiguration> Configurations => _configurations;

    public DebugAdapterService(string workspaceRoot)
    {
        _root = Path.GetFullPath(workspaceRoot);
        _adapters = LoadAdapters(_root).GroupBy(item => item.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        _configurations = LoadConfigurations(_root);
    }

    public static string CreateStarterLaunchConfig() => new JObject
    {
        ["version"] = "0.2.0",
        ["configurations"] = new JArray
        {
            new JObject
            {
                ["name"] = "Python: current file", ["type"] = "python", ["request"] = "launch",
                ["program"] = "${file}", ["cwd"] = "${workspaceFolder}", ["console"] = "internalConsole",
                ["justMyCode"] = true
            }
        }
    }.ToString(Formatting.Indented);

    public static string CreateStarterAdapterConfig() => new JObject
    {
        ["adapters"] = new JArray
        {
            new JObject { ["type"] = "coreclr", ["command"] = "netcoredbg", ["args"] = new JArray("--interpreter=vscode") },
            new JObject { ["type"] = "python", ["command"] = "python", ["args"] = new JArray("-m", "debugpy.adapter") }
        }
    }.ToString(Formatting.Indented);

    public async Task StartAsync(string configurationName, IReadOnlyList<DebugBreakpoint> breakpoints,
        string? activeFile,
        Func<string, string, Task<bool>> approve,
        Func<JObject, Task<JObject?>>? handleReverseRequest = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DebugAdapterService));
        if (_connection is not null) await StopAsync();
        _sessionEnded = false;
        var configuration = _configurations.FirstOrDefault(item => item.Name.Equals(configurationName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Konfigurasi debug tidak ditemukan di .vscode/launch.json.");
        var debugServerPort = configuration.Arguments["debugServer"]?.Value<int?>();
        DebugAdapterDefinition? adapter = null;
        if (debugServerPort is null && !_adapters.TryGetValue(configuration.Type, out adapter))
            throw new InvalidOperationException($"Debug adapter untuk '{configuration.Type}' belum dikonfigurasi. Siapkan .depup/debug-adapters.json.");
        if (configuration.Request is not ("launch" or "attach"))
            throw new InvalidOperationException("DAP saat ini mendukung request 'launch' dan 'attach'.");
        var launchArguments = (JObject)configuration.Arguments.DeepClone();
        launchArguments.Remove("name");
        launchArguments.Remove("type");
        launchArguments.Remove("request");
        launchArguments.Remove("preLaunchTask");
        launchArguments.Remove("debugServer");
        launchArguments.Remove("debugServerHost");
        ExpandVariables(launchArguments, _root, activeFile);
        if (System.Text.RegularExpressions.Regex.IsMatch(launchArguments.ToString(Formatting.None), @"\$\{(?:file|fileDirname|fileBasename|fileBasenameNoExtension|relativeFile)\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Profil memakai variabel file, tetapi belum ada file workspace yang aktif. Buka file sumber lalu tekan F5.");
        var target = new[] { "program", "module", "executable", "runtimeExecutable" }
            .Select(key => launchArguments[key]?.Value<string>()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "target ditentukan oleh adapter";
        var workingDirectory = launchArguments["cwd"]?.Value<string>() ?? _root;
        var environmentNames = (launchArguments["env"] as JObject)?.Properties().Select(property => property.Name).Take(30).ToArray() ?? [];
        if (debugServerPort is < 1 or > 65535) throw new InvalidDataException("debugServer harus memakai port TCP 1–65535.");
        var debugServerHost = configuration.Arguments["debugServerHost"]?.Value<string>() ?? "127.0.0.1";
        if (debugServerPort is not null && (string.IsNullOrWhiteSpace(debugServerHost) || debugServerHost.Length > 255))
            throw new InvalidDataException("debugServerHost tidak valid.");
        var isStdio = debugServerPort is null && adapter!.Transport.Equals("stdio", StringComparison.OrdinalIgnoreCase);
        var command = adapter is null ? "(server DAP yang telah berjalan)" : adapter.Transport == "namedpipe" ? "(named pipe DAP yang telah berjalan)" :
            LanguageServerService.ResolveExecutable(_root, adapter.Command)
            ?? throw new FileNotFoundException($"Debug adapter tidak ditemukan: {adapter.Command}");
        var sandboxAdapter = debugServerPort is null && adapter is not null && adapter.Transport is ("stdio" or "tcp");
        var adapterDetails = debugServerPort is not null
            ? $"DAP TCP {debugServerHost}:{debugServerPort} (debugServer launch.json)"
            : adapter!.Transport switch
            {
                "stdio" => $"{command}{(adapter.Arguments.Count == 0 ? "" : " " + string.Join(" ", adapter.Arguments))} (stdio)",
                "tcp" => $"{command}{(adapter.Arguments.Count == 0 ? "" : " " + string.Join(" ", adapter.Arguments))} → TCP {adapter.Host}:{adapter.Port}",
                "namedpipe" => $"DAP named pipe {adapter.PipeName}",
                _ => throw new NotSupportedException("Transport adapter harus stdio, tcp, atau namedPipe.")
            };
        var details = $"Adapter: {adapterDetails}\n" +
                      $"Profil: {configuration.Name} ({configuration.Type}, {configuration.Request})\n" +
                      $"Program/module: {DiagnosticSecretRedactor.Redact(target)}\nWorking directory: {DiagnosticSecretRedactor.Redact(workingDirectory)}\n" +
                      $"Environment dari launch.json: {(environmentNames.Length == 0 ? "tidak ada" : string.Join(", ", environmentNames))}\n" +
                      (sandboxAdapter
                          ? "Adapter yang diluncurkan DepupCode berjalan dalam AppContainer: workspace dapat dibaca/ditulis, toolchain yang disetujui dapat dibaca, dan internet/private network diizinkan. Proses debuggee anak ikut dibatasi oleh job sandbox."
                          : "Adapter server yang sudah berjalan melalui debugServer/named pipe berada di luar sandbox DepupCode; profil launch dan target yang diproses server tetap mengikuti izin server tersebut.");
        if (!await approve(command, details)) throw new OperationCanceledException("Pengguna menolak menjalankan debug adapter.");

        Process? process = null;
        Stream inputStream;
        Stream outputStream;
        IDisposable transportOwner;
        if (debugServerPort is not null)
        {
            var client = await ConnectTcpAsync(debugServerHost, debugServerPort.Value, cancellationToken);
            inputStream = outputStream = client.GetStream();
            transportOwner = client;
        }
        else if (adapter!.Transport.Equals("namedpipe", StringComparison.OrdinalIgnoreCase))
        {
            var pipe = await ConnectNamedPipeAsync(adapter.PipeName!, cancellationToken);
            inputStream = outputStream = pipe;
            transportOwner = pipe;
        }
        else if (isStdio)
        {
            var sandbox = StartAdapterSandbox(command, adapter!.Arguments, _root, configuration.Type);
            process = sandbox.Process;
            inputStream = sandbox.Input;
            outputStream = sandbox.Output;
            transportOwner = sandbox;
            PumpSandboxStream(sandbox.Error, message => OutputReceived?.Invoke("DAP: " + DiagnosticSecretRedactor.Redact(message)));
        }
        else
        {
            var sandbox = StartAdapterSandbox(command, adapter!.Arguments, _root, configuration.Type);
            process = sandbox.Process;
            TcpClient client;
            try { client = await ConnectTcpAsync(adapter.Host, adapter.Port, cancellationToken, process); }
            catch
            {
                sandbox.Dispose();
                throw;
            }
            inputStream = outputStream = client.GetStream();
            transportOwner = new CompositeLifetime(client, sandbox);
            PumpSandboxStream(sandbox.Output, message => OutputReceived?.Invoke("DAP server: " + DiagnosticSecretRedactor.Redact(message)));
            PumpSandboxStream(sandbox.Error, message => OutputReceived?.Invoke("DAP server: " + DiagnosticSecretRedactor.Redact(message)));
        }

        DapConnection connection;
        try
        {
            connection = new DapConnection(inputStream, outputStream, transportOwner, process, HandleDapEvent, message =>
            {
                StatusChanged?.Invoke(message);
                if (message.StartsWith("DAP:", StringComparison.OrdinalIgnoreCase))
                    OutputReceived?.Invoke(message);
            }, handleReverseRequest);
        }
        catch
        {
            try { if (process is not null && !process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { transportOwner.Dispose(); } catch { }
            if (process is not null && !ReferenceEquals(transportOwner, process))
                try { process.Dispose(); } catch { }
            throw;
        }
        _connection = connection;
        try
        {
            var initialize = await connection.RequestAsync("initialize", new JObject
            {
                ["clientID"] = "depupcode",
                ["clientName"] = "Depup Code",
                ["adapterID"] = configuration.Type,
                ["locale"] = "id-ID",
                ["linesStartAt1"] = true,
                ["columnsStartAt1"] = true,
                ["pathFormat"] = "path",
                ["supportsVariableType"] = true,
                ["supportsVariablePaging"] = true,
                ["supportsRunInTerminalRequest"] = handleReverseRequest is not null,
                ["supportsProgressReporting"] = false,
                ["supportsInvalidatedEvent"] = true
            }, cancellationToken);

            var initialized = connection.WaitForEventAsync("initialized", TimeSpan.FromSeconds(30), cancellationToken);
            var launchResponse = connection.RequestAsync(configuration.Request, launchArguments, cancellationToken);
            var firstSignal = await Task.WhenAny(initialized, launchResponse);
            if (firstSignal == launchResponse && launchResponse.IsFaulted) await launchResponse;
            await initialized;

            foreach (var group in breakpoints.Where(item => IsUnderWorkspace(item.Path))
                         .GroupBy(item => Path.GetFullPath(item.Path), StringComparer.OrdinalIgnoreCase))
            {
                await SendBreakpointsAsync(connection, group.Key, group.Select(item => item.Line).Distinct().Order().ToArray(), cancellationToken);
            }

            if (initialize?["supportsConfigurationDoneRequest"]?.Value<bool>() == true)
                await connection.RequestAsync("configurationDone", new JObject(), cancellationToken);
            await launchResponse;
            StatusChanged?.Invoke("Debug session aktif · " + configuration.Name);
        }
        catch
        {
            await connection.DisposeAsync();
            if (ReferenceEquals(_connection, connection)) _connection = null;
            throw;
        }
    }

    public Task ContinueAsync(CancellationToken cancellationToken = default) => RequestIfRunningAsync("continue", new JObject
    {
        ["threadId"] = _currentThreadId, ["singleThread"] = false
    }, cancellationToken);

    public Task NextAsync(CancellationToken cancellationToken = default) => RequestIfRunningAsync("next", new JObject
    {
        ["threadId"] = _currentThreadId, ["singleThread"] = false
    }, cancellationToken);

    public Task StepInAsync(CancellationToken cancellationToken = default) => RequestIfRunningAsync("stepIn", new JObject
    {
        ["threadId"] = _currentThreadId, ["singleThread"] = false
    }, cancellationToken);

    public Task StepOutAsync(CancellationToken cancellationToken = default) => RequestIfRunningAsync("stepOut", new JObject
    {
        ["threadId"] = _currentThreadId, ["singleThread"] = false
    }, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken = default) => RequestIfRunningAsync("pause", new JObject
    {
        ["threadId"] = _currentThreadId
    }, cancellationToken);

    public async Task SetBreakpointsAsync(string path, IReadOnlyList<int> lines, CancellationToken cancellationToken = default)
    {
        if (_connection is not { HasExited: false } connection || !IsUnderWorkspace(path)) return;
        await SendBreakpointsAsync(connection, Path.GetFullPath(path), lines.Distinct().Where(line => line > 0).Order().ToArray(), cancellationToken);
    }

    public bool HasAdapterFor(string configurationName)
    {
        var configuration = _configurations.FirstOrDefault(item => item.Name.Equals(configurationName, StringComparison.OrdinalIgnoreCase));
        return configuration is not null && _adapters.ContainsKey(configuration.Type);
    }

    private static Task<JObject?> SendBreakpointsAsync(DapConnection connection, string path, IReadOnlyList<int> lines,
        CancellationToken cancellationToken) => connection.RequestAsync("setBreakpoints", new JObject
    {
        ["source"] = new JObject { ["name"] = Path.GetFileName(path), ["path"] = path },
        ["breakpoints"] = new JArray(lines.Select(line => new JObject { ["line"] = line })),
        ["sourceModified"] = false
    }, cancellationToken);

    private async Task RequestIfRunningAsync(string command, JObject arguments, CancellationToken cancellationToken)
    {
        if (_connection is not { HasExited: false } connection) return;
        try { await connection.RequestAsync(command, arguments, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"DAP {command} gagal: {DiagnosticSecretRedactor.Redact(ex.Message)}");
        }
    }

    public async Task StopAsync(bool graceful = true)
    {
        var connection = _connection;
        _connection = null;
        _sessionEnded = true;
        if (connection is null) return;
        try
        {
            if (graceful && !connection.HasExited)
                await connection.RequestAsync("disconnect", new JObject
                {
                    ["restart"] = false,
                    ["terminateDebuggee"] = true,
                    ["suspendDebuggee"] = false
                }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { }
        await connection.DisposeAsync();
        StatusChanged?.Invoke("Debug session berhenti.");
    }

    private void HandleDapEvent(JObject message)
    {
        var name = message["event"]?.Value<string>();
        var body = message["body"] as JObject ?? new JObject();
        if (name == "output")
        {
            var output = body["output"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(output)) OutputReceived?.Invoke(DiagnosticSecretRedactor.Redact(output));
        }
        else if (name == "stopped")
        {
            StatusChanged?.Invoke("Debug berhenti: " + (body["reason"]?.Value<string>() ?? "breakpoint"));
            _currentThreadId = body["threadId"]?.Value<int>() ?? 1;
            _ = RefreshStoppedStateAsync(_currentThreadId);
        }
        else if (name == "continued")
        {
            StatusChanged?.Invoke("Debug berjalan.");
            Stopped?.Invoke([], []);
        }
        else if (name is "terminated" or "exited")
        {
            _sessionEnded = true;
            StatusChanged?.Invoke("Program debug selesai.");
            Stopped?.Invoke([], []);
        }
        else if (name == "breakpoint") StatusChanged?.Invoke("Breakpoint diperbarui oleh adapter.");
    }

    private async Task RefreshStoppedStateAsync(int threadId)
    {
        var connection = _connection;
        if (connection is null || connection.HasExited) return;
        var frames = new List<DebugStackFrame>();
        var variables = new List<DebugVariable>();
        try
        {
            var stack = await connection.RequestAsync("stackTrace", new JObject { ["threadId"] = threadId, ["startFrame"] = 0, ["levels"] = 40 }, CancellationToken.None);
            foreach (var frame in (stack?["stackFrames"] as JArray ?? []).OfType<JObject>())
            {
                var sourcePath = frame["source"]?["path"]?.Value<string>() ?? "";
                frames.Add(new DebugStackFrame(frame["name"]?.Value<string>() ?? "(frame)", sourcePath,
                    Math.Max(1, frame["line"]?.Value<int>() ?? 1), Math.Max(1, frame["column"]?.Value<int>() ?? 1),
                    frame["id"]?.Value<int>() ?? 0));
            }

            if (frames.FirstOrDefault() is { FrameId: > 0 } top)
            {
                var scopes = await connection.RequestAsync("scopes", new JObject { ["frameId"] = top.FrameId }, CancellationToken.None);
                var scope = (scopes?["scopes"] as JArray)?.OfType<JObject>().FirstOrDefault(item =>
                    item["variablesReference"]?.Value<int>() > 0);
                if (scope is not null)
                {
                    var response = await connection.RequestAsync("variables", new JObject
                    {
                        ["variablesReference"] = scope["variablesReference"]!.Value<int>(), ["start"] = 0, ["count"] = 100
                    }, CancellationToken.None);
                    foreach (var variable in (response?["variables"] as JArray ?? []).OfType<JObject>())
                        variables.Add(new DebugVariable(variable["name"]?.Value<string>() ?? "",
                            DiagnosticSecretRedactor.Redact(variable["value"]?.Value<string>() ?? ""),
                            variable["type"]?.Value<string>() ?? "", variable["variablesReference"]?.Value<int>() ?? 0));
                }
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("Tidak dapat membaca call stack/variables: " + DiagnosticSecretRedactor.Redact(ex.Message));
        }
        Stopped?.Invoke(frames, variables);
    }

    private bool IsUnderWorkspace(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_root, Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static List<DebugAdapterDefinition> LoadAdapters(string root)
    {
        var adapters = new List<DebugAdapterDefinition>();
        var path = Path.Combine(root, ".depup", "debug-adapters.json");
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length <= MaxConfigBytes)
            {
                var json = JObject.Parse(File.ReadAllText(path));
                foreach (var item in (json["adapters"] as JArray ?? []).OfType<JObject>().Take(20))
                {
                    var type = item["type"]?.Value<string>()?.Trim();
                    var command = item["command"]?.Value<string>()?.Trim();
                    var transport = (item["transport"]?.Value<string>()?.Trim() ?? "stdio").ToLowerInvariant();
                    if (string.IsNullOrWhiteSpace(type)) continue;
                    if (transport is not ("stdio" or "tcp" or "namedpipe")) continue;
                    if (transport != "namedpipe" && string.IsNullOrWhiteSpace(command)) continue;
                    var args = (item["args"] as JArray ?? []).Values<string>().Where(value => value is not null).Take(40).Select(value => value!).ToList();
                    var host = item["host"]?.Value<string>()?.Trim() ?? "127.0.0.1";
                    var port = item["port"]?.Value<int?>() ?? 0;
                    var pipeName = item["pipeName"]?.Value<string>()?.Trim();
                    if (transport == "tcp" && (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(command))) continue;
                    if (transport == "namedpipe" && (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200)) continue;
                    if (transport == "stdio" && string.IsNullOrWhiteSpace(command)) continue;
                    adapters.Add(new DebugAdapterDefinition(type, command ?? "", args, transport, host, port, pipeName));
                }
            }
        }
        catch (Exception ex) { Debug.WriteLine("DAP adapter config error: " + ex.Message); }

        if (!adapters.Any(item => item.Type.Equals("coreclr", StringComparison.OrdinalIgnoreCase)) &&
            LanguageServerService.ResolveExecutable(root, "netcoredbg") is not null)
            adapters.Add(new DebugAdapterDefinition("coreclr", "netcoredbg", ["--interpreter=vscode"], "stdio", "127.0.0.1", 0, null));
        if (!adapters.Any(item => item.Type.Equals("python", StringComparison.OrdinalIgnoreCase)) &&
            LanguageServerService.ResolveExecutable(root, "debugpy-adapter") is not null)
            adapters.Add(new DebugAdapterDefinition("python", "debugpy-adapter", [], "stdio", "127.0.0.1", 0, null));
        return adapters;
    }

    private static WindowsExtensionSandbox StartAdapterSandbox(string command, IReadOnlyList<string> args, string root, string adapterId)
    {
        var start = CreateStartInfo(command, args, root);
        var executable = Path.IsPathRooted(start.FileName)
            ? Path.GetFullPath(start.FileName)
            : LanguageServerService.ResolveExecutable(root, start.FileName)
              ?? throw new FileNotFoundException($"Executable adapter tidak ditemukan: {start.FileName}");
        var environment = start.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var readOnlyPaths = new List<string>();
        if (Path.GetDirectoryName(executable) is { Length: > 0 } executableDirectory) readOnlyPaths.Add(executableDirectory);
        if (environment.TryGetValue("PATH", out var pathValue))
            readOnlyPaths.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return WindowsExtensionSandbox.StartCommand(executable, start.ArgumentList.ToArray(), root, root,
            $"dap:{root}:{adapterId}", environment, readOnlyPaths, allowInternet: true, allowPrivateNetwork: true);
    }

    private static async Task<TcpClient> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken, Process? serverProcess = null)
    {
        if (port is < 1 or > 65535 || string.IsNullOrWhiteSpace(host) || host.Length > 255)
            throw new InvalidDataException("Alamat TCP adapter tidak valid.");
        var client = new TcpClient();
        var timeout = Stopwatch.StartNew();
        Exception? lastError = null;
        try
        {
            while (timeout.Elapsed < TimeSpan.FromSeconds(12))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (serverProcess?.HasExited == true)
                    throw new InvalidOperationException($"Server debug adapter berhenti dengan exit code {serverProcess.ExitCode} sebelum menerima koneksi.");
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await client.ConnectAsync(host, port, attempt.Token);
                    return client;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (SocketException ex) { lastError = ex; }
                client.Dispose();
                client = new TcpClient();
                await Task.Delay(150, cancellationToken);
            }
            throw new TimeoutException($"Tidak dapat terhubung ke DAP {host}:{port} dalam 12 detik.", lastError);
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<NamedPipeClientStream> ConnectNamedPipeAsync(string pipeName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 || pipeName.Contains('\\') || pipeName.Contains('/'))
            throw new InvalidDataException("Nama DAP named pipe tidak valid.");
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            await pipe.ConnectAsync(timeout.Token);
            return pipe;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            throw new TimeoutException($"DAP named pipe '{pipeName}' tidak tersedia dalam 12 detik.");
        }
        catch { pipe.Dispose(); throw; }
    }

    private static void PumpSandboxStream(Stream stream, Action<string> output)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                while (await reader.ReadLineAsync() is { } line)
                    if (!string.IsNullOrWhiteSpace(line)) output(DiagnosticSecretRedactor.Redact(line));
            }
            catch { }
        });
    }

    private sealed class CompositeLifetime(IDisposable first, IDisposable second) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { first.Dispose(); }
            finally { second.Dispose(); }
        }
    }

    private static List<DebugLaunchConfiguration> LoadConfigurations(string root)
    {
        var result = new List<DebugLaunchConfiguration>();
        var path = Path.Combine(root, ".vscode", "launch.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes) return result;
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            var json = JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, LineInfoHandling = LineInfoHandling.Ignore });
            foreach (var config in (json["configurations"] as JArray ?? []).OfType<JObject>().Take(40))
            {
                var name = config["name"]?.Value<string>()?.Trim();
                var type = config["type"]?.Value<string>()?.Trim();
                var request = config["request"]?.Value<string>()?.Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || request is null) continue;
                result.Add(new DebugLaunchConfiguration(name, type, request, (JObject)config.DeepClone()));
            }
        }
        catch (Exception ex) { Debug.WriteLine("launch.json error: " + ex.Message); }
        return result;
    }

    private static void ExpandVariables(JToken token, string root, string? activeFile)
    {
        if (token is JObject obj)
        {
            foreach (var property in obj.Properties().ToList()) ExpandVariables(property.Value, root, activeFile);
        }
        else if (token is JArray array)
        {
            foreach (var item in array) ExpandVariables(item, root, activeFile);
        }
        else if (token.Type == JTokenType.String)
        {
            var value = token.Value<string>() ?? "";
            var workspaceName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            value = value.Replace("${workspaceFolder}", root, StringComparison.OrdinalIgnoreCase)
                .Replace("${workspaceFolderBasename}", workspaceName, StringComparison.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(activeFile))
            {
                var fullFile = Path.GetFullPath(activeFile);
                var fileDirectory = Path.GetDirectoryName(fullFile) ?? root;
                var relativeFile = Path.GetRelativePath(root, fullFile);
                value = value.Replace("${file}", fullFile, StringComparison.OrdinalIgnoreCase)
                    .Replace("${fileDirname}", fileDirectory, StringComparison.OrdinalIgnoreCase)
                    .Replace("${fileBasenameNoExtension}", Path.GetFileNameWithoutExtension(fullFile), StringComparison.OrdinalIgnoreCase)
                    .Replace("${fileBasename}", Path.GetFileName(fullFile), StringComparison.OrdinalIgnoreCase)
                    .Replace("${relativeFile}", relativeFile, StringComparison.OrdinalIgnoreCase);
            }
            value = System.Text.RegularExpressions.Regex.Replace(value, @"\$\{env:([^}]+)\}", match =>
                Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? "");
            ((JValue)token).Value = value;
        }
    }

    private sealed record DebugAdapterDefinition(string Type, string Command, IReadOnlyList<string> Arguments,
        string Transport, string Host, int Port, string? PipeName);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync(graceful: false);
    }

    private sealed class DapConnection : IAsyncDisposable
    {
        private static readonly string[] SafeEnvironmentNames =
        [
            "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE",
            "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)",
            "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
        ];

        private readonly Stream _input;
        private readonly Stream _output;
        private readonly IDisposable _transportOwner;
        private readonly Process? _process;
        private readonly Action<JObject> _eventHandler;
        private readonly Action<string> _log;
        private readonly Func<JObject, Task<JObject?>>? _requestHandler;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JObject>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _events = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource<bool> _processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _reader;
        private int _sequence;
        private bool _disposed;

        public bool HasExited => _process?.HasExited ?? _reader.IsCompleted;

        public DapConnection(Stream input, Stream output, IDisposable transportOwner, Process? process,
            Action<JObject> eventHandler, Action<string> log,
            Func<JObject, Task<JObject?>>? requestHandler)
        {
            _input = input;
            _output = output;
            _transportOwner = transportOwner;
            _process = process;
            _eventHandler = eventHandler;
            _log = log;
            _requestHandler = requestHandler;
            if (_process is not null)
            {
                _process.Exited += (_, _) => _processExited.TrySetResult(true);
                if (_process.HasExited) _processExited.TrySetResult(true);
                if (ReferenceEquals(transportOwner, _process))
                {
                    if (!_process.StartInfo.RedirectStandardOutput)
                        throw new InvalidOperationException("DAP stdio adapter memerlukan stdout terarah.");
                    _process.ErrorDataReceived += (_, args) =>
                    {
                        if (!string.IsNullOrWhiteSpace(args.Data)) _log("DAP: " + DiagnosticSecretRedactor.Redact(args.Data));
                    };
                    _process.BeginErrorReadLine();
                }
            }
            _reader = Task.Run(ReadLoopAsync);
        }

        public async Task<JObject?> RequestAsync(string command, JObject arguments, CancellationToken cancellationToken)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var pending = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[sequence] = pending;
            try
            {
                await SendAsync(new JObject
                {
                    ["seq"] = sequence, ["type"] = "request", ["command"] = command, ["arguments"] = arguments
                }, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var response = await pending.Task.WaitAsync(timeout.Token);
                if (response["success"]?.Value<bool>() == false)
                    throw new InvalidOperationException(response["message"]?.Value<string>() ?? $"DAP {command} rejected.");
                return response["body"] as JObject;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException($"DAP {command} melewati batas 30 detik.");
            }
            finally { _pending.TryRemove(sequence, out _); }
        }

        public async Task WaitForEventAsync(string name, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var completion = _events.GetOrAdd(name, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            linked.CancelAfter(timeout);
            try
            {
                var first = await Task.WhenAny(completion.Task, _processExited.Task).WaitAsync(linked.Token);
                if (first == _processExited.Task && !completion.Task.IsCompleted)
                    throw new InvalidOperationException($"Debug adapter memutus koneksi sebelum event '{name}'. Periksa log adapter dan Debug Console.");
                await completion.Task;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Debug adapter tidak mengirim event '{name}'.");
            }
        }

        private async Task SendAsync(JObject message, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _input.WriteAsync(header, cancellationToken);
                await _input.WriteAsync(body, cancellationToken);
                await _input.FlushAsync(cancellationToken);
            }
            finally { _writeGate.Release(); }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var json = await ReadMessageAsync(_output, _lifetime.Token);
                    if (json is null) break;
                    JObject message;
                    try { message = JObject.Parse(json); }
                    catch { continue; }
                    var type = message["type"]?.Value<string>();
                    if (type == "response" && message["request_seq"]?.Value<int>() is { } responseSeq && _pending.TryGetValue(responseSeq, out var pending))
                        pending.TrySetResult(message);
                    else if (type == "event")
                    {
                        var eventName = message["event"]?.Value<string>();
                        if (eventName is not null && _events.TryGetValue(eventName, out var completion)) completion.TrySetResult(true);
                        _eventHandler(message);
                    }
                    else if (type == "request" && message["seq"]?.Value<int>() is { } requestSeq)
                    {
                        var command = message["command"]?.Value<string>() ?? "";
                        try
                        {
                            if (!command.Equals("runInTerminal", StringComparison.Ordinal) || _requestHandler is null)
                                throw new NotSupportedException($"Reverse request DAP '{command}' belum didukung.");
                            var body = await _requestHandler(message);
                            await SendAsync(new JObject
                            {
                                ["seq"] = Interlocked.Increment(ref _sequence), ["type"] = "response",
                                ["request_seq"] = requestSeq, ["command"] = command,
                                ["success"] = true, ["body"] = body ?? new JObject()
                            }, _lifetime.Token);
                        }
                        catch (Exception ex)
                        {
                            await SendAsync(new JObject
                            {
                                ["seq"] = Interlocked.Increment(ref _sequence), ["type"] = "response",
                                ["request_seq"] = requestSeq, ["command"] = command,
                                ["success"] = false, ["message"] = DiagnosticSecretRedactor.Redact(ex.Message)
                            }, _lifetime.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_lifetime.IsCancellationRequested) _log("DAP koneksi berhenti: " + DiagnosticSecretRedactor.Redact(ex.Message));
            }
            finally
            {
            _processExited.TrySetResult(true);
                foreach (var item in _pending.Values) item.TrySetException(new InvalidOperationException("DAP adapter menutup koneksi."));
            }
        }

        private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            const int maxHeaderBytes = 8_192;
            var header = new List<byte>(128);
            var one = new byte[1];
            var delimiter = new byte[] { 13, 10, 13, 10 };
            var matched = 0;
            while (header.Count < maxHeaderBytes)
            {
                var read = await stream.ReadAsync(one.AsMemory(), cancellationToken);
                if (read == 0) return header.Count == 0 ? null : throw new EndOfStreamException("DAP header terpotong.");
                header.Add(one[0]);
                matched = one[0] == delimiter[matched] ? matched + 1 : one[0] == delimiter[0] ? 1 : 0;
                if (matched == delimiter.Length) break;
            }
            if (matched != delimiter.Length) throw new InvalidDataException("Header DAP melewati batas 8 KB.");
            var text = Encoding.ASCII.GetString(header.ToArray());
            var lengthText = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(':', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))?[1];
            if (!int.TryParse(lengthText?.Trim(), out var length) || length < 0 || length > 8 * 1024 * 1024)
                throw new InvalidDataException("Panjang pesan DAP tidak valid atau melebihi 8 MB.");
            var body = new byte[length];
            var total = 0;
            while (total < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(total), cancellationToken);
                if (read == 0) throw new EndOfStreamException("DAP body terpotong.");
                total += read;
            }
            return Encoding.UTF8.GetString(body);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            try
            {
                if (_process is not null && !_process.HasExited && ReferenceEquals(_transportOwner, _process))
                    _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { _transportOwner.Dispose(); } catch { }
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (_process is not null && !ReferenceEquals(_transportOwner, _process))
            {
                try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
                _process.Dispose();
            }
            _writeGate.Dispose();
            _lifetime.Dispose();
        }
    }

    private static ProcessStartInfo CreateStartInfo(string command, IReadOnlyList<string> args, string root)
    {
        var extension = Path.GetExtension(command);
        var isBatch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        if (OperatingSystem.IsWindows() && isBatch)
        {
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d"); start.ArgumentList.Add("/s"); start.ArgumentList.Add("/c");
            var commandLine = "\"\"" + command + "\"" + (args.Count == 0 ? "" : " " + string.Join(" ", args.Select(QuoteCommandArgument))) + "\"";
            start.ArgumentList.Add(commandLine);
        }
        else
        {
            start.FileName = command;
            foreach (var arg in args) start.ArgumentList.Add(arg);
        }
        return start;
    }

    private static string QuoteCommandArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
