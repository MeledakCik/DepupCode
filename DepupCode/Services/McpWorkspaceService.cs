using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record McpServerDefinition(
    string Name,
    string Transport,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    string Url,
    IReadOnlyDictionary<string, string> Headers,
    int TimeoutMilliseconds,
    IReadOnlyList<string>? AllowedTools = null);

public sealed record McpToolBinding(
    string FunctionName,
    string ServerName,
    string ToolName,
    string Description,
    JObject InputSchema,
    bool ReadOnly,
    IMcpConnection Connection);

public interface IMcpConnection : IAsyncDisposable
{
    int ToolCount { get; }
    Task<IReadOnlyList<JObject>> ListToolsAsync(CancellationToken cancellationToken);
    Task<string> CallToolAsync(string toolName, JObject arguments, CancellationToken cancellationToken);
}

/// <summary>Reads common workspace MCP formats and connects local or Streamable HTTP MCP servers.</summary>
public sealed class McpWorkspaceService : IAsyncDisposable
{
    private const int MaxServers = 8;
    private const int MaxTools = 48;
    private readonly List<IMcpConnection> _connections = [];
    private readonly List<McpServerDefinition> _connectedServers = [];
    private readonly Dictionary<string, McpToolBinding> _tools = new(StringComparer.Ordinal);

    public IReadOnlyCollection<McpToolBinding> Tools => _tools.Values;
    public IReadOnlyCollection<McpServerDefinition> Servers { get; private set; } = [];
    public IReadOnlyCollection<McpServerDefinition> ConnectedServers => _connectedServers;

    public IReadOnlyList<McpToolBinding> SelectTools(string prompt, bool allowChanges, int maximum = 12)
    {
        var candidates = _tools.Values.Where(tool => allowChanges || tool.ReadOnly).ToList();
        if (candidates.Count <= Math.Min(maximum, 6)) return candidates;
        var terms = Regex.Matches(prompt.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (terms.Overlaps(["preview", "visual", "screenshot", "tampilan", "halaman", "browser", "ui"]))
            terms.UnionWith(["browser", "playwright", "screenshot", "page", "console", "inspect"]);
        if (terms.Overlaps(["bug", "error", "debug", "test", "verify", "gagal", "masalah"]))
            terms.UnionWith(["test", "browser", "playwright", "console", "log", "diagnostic"]);
        if (terms.Overlaps(["dokumentasi", "docs", "library", "package", "api"]))
            terms.UnionWith(["docs", "search", "documentation", "library"]);

        var ranked = candidates.Select(tool =>
        {
            var labels = (tool.ServerName + " " + tool.ToolName + " " + tool.Description + " " +
                          string.Join(" ", (tool.InputSchema["properties"] as JObject)?.Properties().Select(property => property.Name) ?? []))
                .ToLowerInvariant();
            var score = terms.Sum(term =>
                tool.ToolName.Contains(term, StringComparison.OrdinalIgnoreCase) ? 4 :
                tool.ServerName.Contains(term, StringComparison.OrdinalIgnoreCase) ? 3 :
                labels.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
            return (Tool: tool, Score: score);
        }).OrderByDescending(item => item.Score).ThenBy(item => item.Tool.ServerName).ThenBy(item => item.Tool.ToolName).ToList();

        var matching = ranked.Where(item => item.Score > 0).ToList();
        var selection = matching.Count > 0 ? matching : ranked;
        var perServer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return selection.Where(item =>
            {
                perServer.TryGetValue(item.Tool.ServerName, out var count);
                if (count >= (matching.Count > 0 ? 6 : 2)) return false;
                perServer[item.Tool.ServerName] = count + 1;
                return true;
            })
            .Take(matching.Count > 0 ? maximum : Math.Min(maximum, 4))
            .Select(item => item.Tool).ToList();
    }

    public async Task ConnectAsync(string workspaceRoot, Action<string>? status,
        Func<McpServerDefinition, Task<bool>> approveConnection, CancellationToken cancellationToken,
        bool includeRemote = true)
    {
        if (!Directory.Exists(workspaceRoot)) return;
        Servers = LoadWorkspaceServers(Path.GetFullPath(workspaceRoot)).Take(MaxServers).ToList();
        foreach (var server in Servers.Where(server => server.Transport == "stdio" || includeRemote && server.Transport == "http"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IMcpConnection? connection = null;
            try
            {
                status?.Invoke($"Menunggu persetujuan koneksi MCP {server.Name}…");
                if (!await approveConnection(server))
                {
                    status?.Invoke($"MCP {server.Name} ditolak; proses tidak dijalankan.");
                    continue;
                }
                status?.Invoke($"Menghubungkan MCP {server.Name}…");
                connection = server.Transport == "stdio"
                    ? await McpStdioConnection.StartAsync(server, cancellationToken)
                    : await McpHttpConnection.StartAsync(server, cancellationToken);
                _connections.Add(connection);
                foreach (var tool in await connection.ListToolsAsync(cancellationToken))
                {
                    if (_tools.Count >= MaxTools) break;
                    var rawName = tool["name"]?.Value<string>();
                    if (string.IsNullOrWhiteSpace(rawName) || !IsAllowedTool(server.AllowedTools, rawName)) continue;
                    var functionName = MakeFunctionName(server.Name, rawName);
                    if (functionName.Length > 64) functionName = functionName[..64];
                    var schema = tool["inputSchema"] as JObject ?? new JObject
                    {
                        ["type"] = "object", ["properties"] = new JObject()
                    };
                    if (schema["type"] is null) schema["type"] = "object";
                    _tools.TryAdd(functionName, new McpToolBinding(
                        functionName, server.Name, rawName,
                        tool["description"]?.Value<string>() ?? "MCP tool",
                        (JObject)schema.DeepClone(),
                        tool["annotations"]?["readOnlyHint"]?.Value<bool>() == true,
                        connection));
                }
                _connectedServers.Add(server);
                status?.Invoke($"MCP {server.Name}: {connection.ToolCount} tool tersedia.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (connection is not null)
                {
                    foreach (var name in _tools.Where(pair => ReferenceEquals(pair.Value.Connection, connection))
                                 .Select(pair => pair.Key).ToList())
                        _tools.Remove(name);
                    await connection.DisposeAsync();
                    _connections.Remove(connection);
                }
                _connectedServers.Remove(server);
                status?.Invoke($"MCP {server.Name} tidak tersedia: {Trim(ex.Message, 180)}");
            }
        }
        foreach (var server in Servers.Where(server => server.Transport == "sse"))
            status?.Invoke($"MCP {server.Name}: transport SSE lama belum didukung; gunakan endpoint Streamable HTTP atau stdio.");
    }

    public bool TryGetTool(string functionName, out McpToolBinding tool) => _tools.TryGetValue(functionName, out tool!);

    public static IReadOnlyList<McpServerDefinition> LoadWorkspaceServers(string workspaceRoot)
    {
        var result = new List<McpServerDefinition>();
        ReadConfig(Path.Combine(workspaceRoot, ".vscode", "mcp.json"), "vscode", "servers", result, workspaceRoot);
        ReadConfig(Path.Combine(workspaceRoot, ".mcp.json"), "copilot", "mcpServers", result, workspaceRoot);
        foreach (var file in new[] { "opencode.json", "opencode.jsonc" })
            ReadOpenCodeConfig(Path.Combine(workspaceRoot, file), result, workspaceRoot);

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var copilotHome = Environment.GetEnvironmentVariable("COPILOT_HOME");
        if (string.IsNullOrWhiteSpace(copilotHome)) copilotHome = Path.Combine(profile, ".copilot");
        ReadConfig(Path.Combine(copilotHome, "mcp-config.json"), "copilot-global", "mcpServers", result, workspaceRoot);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        ReadConfig(Path.Combine(appData, "Code", "User", "mcp.json"), "vscode-global", "servers", result, workspaceRoot);
        ReadConfig(Path.Combine(appData, "Code - Insiders", "User", "mcp.json"), "vscode-insiders", "servers", result, workspaceRoot);
        var openCodeConfig = Environment.GetEnvironmentVariable("OPENCODE_CONFIG");
        if (!string.IsNullOrWhiteSpace(openCodeConfig)) ReadOpenCodeConfig(openCodeConfig, result, workspaceRoot, "opencode-env-");
        foreach (var file in new[] { "opencode.json", "opencode.jsonc" })
            ReadOpenCodeConfig(Path.Combine(profile, ".config", "opencode", file), result, workspaceRoot, "opencode-global-");
        return result.GroupBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).Take(MaxServers).ToList();
    }

    private static void ReadOpenCodeConfig(string path, List<McpServerDefinition> result, string workspaceRoot,
        string namePrefix = "opencode-")
    {
        if (!File.Exists(path)) return;
        try
        {
            var root = ParseJsonc(path);
            var mcp = root["mcp"] as JObject;
            if (mcp is null) return;
            var servers = mcp["servers"] as JObject ?? mcp;
            foreach (var property in servers.Properties())
            {
                if (property.Name is "enabled" or "timeout" or "servers") continue;
                if (property.Value is not JObject config) continue;
                result.Add(ParseServer(namePrefix + property.Name, config, workspaceRoot, opencode: true));
            }
        }
        catch { }
    }

    private static void ReadConfig(string path, string prefix, string serverContainer,
        List<McpServerDefinition> result, string workspaceRoot)
    {
        if (!File.Exists(path)) return;
        try
        {
            var root = ParseJsonc(path);
            if (root[serverContainer] is not JObject servers) return;
            foreach (var property in servers.Properties())
                if (property.Value is JObject config)
                    result.Add(ParseServer(prefix + "-" + property.Name, config, workspaceRoot, opencode: false));
        }
        catch { }
    }

    private static JObject ParseJsonc(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path)))
        {
            DateParseHandling = DateParseHandling.None,
            SupportMultipleContent = false
        };
        return JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
    }

    private static McpServerDefinition ParseServer(string name, JObject config, string workspaceRoot, bool opencode)
    {
        if (config["enabled"]?.Value<bool?>() == false || config["disabled"]?.Value<bool?>() == true)
            return new McpServerDefinition(name, "disabled", "", [], new Dictionary<string, string>(), workspaceRoot, "", new Dictionary<string, string>(), 30_000);

        var type = config["type"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "";
        var timeout = Math.Clamp(config["timeout"]?.Value<int?>() ?? 30_000, 1_000, 120_000);
        var allowedTools = config["tools"] is JArray toolArray
            ? toolArray.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList()
            : null;
        if (type is "http" or "sse" or "remote" || config["url"] is not null && config["command"] is null)
        {
            var url = Expand(config["url"]?.Value<string>() ?? "", workspaceRoot);
            var headers = ReadStringMap(config["headers"] as JObject, workspaceRoot);
            return new McpServerDefinition(name, type == "sse" ? "sse" : "http", "", [], new Dictionary<string, string>(), workspaceRoot, url, headers, timeout, allowedTools);
        }

        if (type.Length > 0 && type is not ("stdio" or "local"))
            return new McpServerDefinition(name, "unsupported", "", [], new Dictionary<string, string>(), workspaceRoot, "", new Dictionary<string, string>(), timeout);

        var commandToken = config["command"];
        string command;
        var arguments = new List<string>();
        if (commandToken is JArray commandParts)
        {
            var values = commandParts.Values<string>().Where(value => value is not null).ToList();
            command = values.FirstOrDefault() ?? "";
            arguments.AddRange(values.Skip(1)!);
        }
        else command = commandToken?.Value<string>() ?? "";
        if (config["args"] is JArray args) arguments.AddRange(args.Values<string>().Where(value => value is not null)!);

        var envObject = config[opencode ? "environment" : "env"] as JObject;
        var environment = ReadStringMap(envObject, workspaceRoot);
        var cwdValue = config[opencode ? "cwd" : "cwd"]?.Value<string>() ?? config["workingDirectory"]?.Value<string>() ?? workspaceRoot;
        var cwd = Expand(cwdValue, workspaceRoot);
        if (!Path.IsPathRooted(cwd)) cwd = Path.GetFullPath(Path.Combine(workspaceRoot, cwd));
        if (!Directory.Exists(cwd)) cwd = workspaceRoot;
        command = Expand(command, workspaceRoot);
        arguments = arguments.Select(value => Expand(value, workspaceRoot)).ToList();
        if (string.IsNullOrWhiteSpace(command) || HasUnresolvedVariable(command) || arguments.Any(HasUnresolvedVariable) ||
            environment.Values.Any(HasUnresolvedVariable))
            return new McpServerDefinition(name, "unsupported", "", [], new Dictionary<string, string>(), workspaceRoot, "", new Dictionary<string, string>(), timeout);
        return new McpServerDefinition(name, "stdio", command, arguments, environment, cwd, "", new Dictionary<string, string>(), timeout, allowedTools);
    }

    private static Dictionary<string, string> ReadStringMap(JObject? values, string workspaceRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (values is null) return result;
        foreach (var property in values.Properties())
        {
            if (property.Value.Type != JTokenType.String) continue;
            result[property.Name] = Expand(property.Value.Value<string>() ?? "", workspaceRoot);
        }
        return result;
    }

    private static readonly Regex VariableRegex = new(@"\$\{(?:env:)?([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}|\{env:([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    private static string Expand(string value, string workspaceRoot) => VariableRegex.Replace(value, match =>
    {
        if (match.Value == "${workspaceFolder}" || match.Value == "${workspaceFolderBasename}")
            return match.Value.EndsWith("Basename}", StringComparison.Ordinal) ? Path.GetFileName(workspaceRoot) : workspaceRoot;
        var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
        return Environment.GetEnvironmentVariable(name) ?? (match.Groups[2].Success ? match.Groups[2].Value : match.Value);
    });

    private static bool HasUnresolvedVariable(string value) => value.Contains("${input:", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(value, @"\$\{(?:env:)?[A-Za-z_][A-Za-z0-9_]*(?::-[^}]*)?\}|\{env:[A-Za-z_][A-Za-z0-9_]*\}");

    private static string MakeFunctionName(string server, string tool)
    {
        var normalized = Regex.Replace("mcp_" + server + "_" + tool, "[^A-Za-z0-9_-]", "_");
        return normalized.Trim('_');
    }

    private static bool IsAllowedTool(IReadOnlyList<string>? patterns, string toolName)
    {
        if (patterns is null) return true;
        if (patterns.Count == 0) return false;
        var allowPatterns = patterns.Where(pattern => !pattern.StartsWith('!')).ToList();
        var denyPatterns = patterns.Where(pattern => pattern.StartsWith('!')).Select(pattern => pattern[1..]).ToList();
        var allowed = allowPatterns.Count == 0 || allowPatterns.Any(pattern => GlobMatches(pattern, toolName));
        return allowed && !denyPatterns.Any(pattern => GlobMatches(pattern, toolName));
    }

    private static bool GlobMatches(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
            await connection.DisposeAsync();
        _connections.Clear();
        _connectedServers.Clear();
        _tools.Clear();
    }
}

public sealed class McpStdioConnection : IMcpConnection
{
    private static readonly string[] InheritedEnvironmentAllowlist =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec",
        "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramFiles", "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
    ];

    private readonly Process _process;
    private readonly int _timeoutMilliseconds;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private int _requestId;
    public int ToolCount { get; private set; }

    private McpStdioConnection(Process process, int timeoutMilliseconds)
    {
        _process = process;
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public static async Task<McpStdioConnection> StartAsync(McpServerDefinition server, CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(server);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Proses server tidak dapat dimulai.");
        process.BeginErrorReadLine();
        var connection = new McpStdioConnection(process, server.TimeoutMilliseconds);
        try
        {
            await connection.RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "DepupCode", ["version"] = "2.5.0" }
            }, cancellationToken);
            await connection.NotifyAsync("notifications/initialized", new JObject(), cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<JObject>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/list", new JObject(), cancellationToken);
        var tools = result["tools"] is JArray array ? array.OfType<JObject>().ToList() : [];
        ToolCount = tools.Count;
        return tools;
    }

    public async Task<string> CallToolAsync(string toolName, JObject arguments, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/call", new JObject { ["name"] = toolName, ["arguments"] = arguments }, cancellationToken);
        if (result["isError"]?.Value<bool>() == true)
            return "MCP tool melaporkan error: " + ExtractContent(result);
        var text = ExtractContent(result);
        if (string.IsNullOrWhiteSpace(text) && result["structuredContent"] is JToken structured)
            text = structured.ToString(Formatting.None);
        if (text.Length > 8_000) text = text[..8_000] + "\n[Hasil MCP dipotong.]";
        return string.IsNullOrWhiteSpace(text) ? "MCP tool selesai tanpa output teks." : text;
    }

    private async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken)
    {
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_timeoutMilliseconds);
            var id = Interlocked.Increment(ref _requestId);
            var message = new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
            };
            await _process.StandardInput.WriteLineAsync(message.ToString(Formatting.None));
            await _process.StandardInput.FlushAsync(timeout.Token);
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new InvalidOperationException("Server menutup output MCP.");
                JObject response;
                try { response = JObject.Parse(line); }
                catch { continue; }
                if (response["id"]?.ToString() != id.ToString()) continue;
                if (response["error"] is JObject error)
                    throw new InvalidOperationException(error["message"]?.Value<string>() ?? "MCP JSON-RPC gagal.");
                return response["result"] as JObject ?? new JObject();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Server MCP melewati batas {_timeoutMilliseconds / 1000} detik.");
        }
        finally { _rpcLock.Release(); }
    }

    private async Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken)
    {
        var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        await _process.StandardInput.WriteLineAsync(message.ToString(Formatting.None));
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    private static string ExtractContent(JObject result)
    {
        if (result["content"] is not JArray items) return "";
        return string.Join("\n", items.OfType<JObject>()
            .Where(item => string.Equals(item["type"]?.Value<string>(), "text", StringComparison.OrdinalIgnoreCase))
            .Select(item => item["text"]?.Value<string>()).Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static ProcessStartInfo CreateStartInfo(McpServerDefinition server)
    {
        var command = server.Command;
        var args = server.Arguments.ToList();
        var extension = Path.GetExtension(command);
        var isBatch = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        if (OperatingSystem.IsWindows() && !Path.HasExtension(command))
        {
            foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var exe = Path.Combine(folder.Trim('"'), command + ".exe");
                var cmd = Path.Combine(folder.Trim('"'), command + ".cmd");
                var bat = Path.Combine(folder.Trim('"'), command + ".bat");
                if (File.Exists(exe)) { command = exe; break; }
                if (File.Exists(cmd)) { command = cmd; isBatch = true; break; }
                if (File.Exists(bat)) { command = bat; isBatch = true; break; }
            }
        }

        var start = new ProcessStartInfo
        {
            WorkingDirectory = server.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        // MCP children receive only basic Windows runtime paths plus values explicitly
        // declared for that server; they do not inherit provider/session credentials.
        start.Environment.Clear();
        foreach (var name in InheritedEnvironmentAllowlist)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null) start.Environment[name] = value;
        }
        foreach (var item in server.Environment) start.Environment[item.Key] = item.Value;
        if (OperatingSystem.IsWindows() && isBatch)
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.FileName = comSpec;
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
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

    public async ValueTask DisposeAsync()
    {
        try { _process.StandardInput.Close(); } catch { }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { _process.Kill(true); } catch { }
        }
        _process.Dispose();
        _rpcLock.Dispose();
    }
}

/// <summary>Client for the MCP Streamable HTTP request/response transport.</summary>
public sealed class McpHttpConnection : IMcpConnection
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly McpServerDefinition _server;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _rpcLock = new(1, 1);
    private string? _sessionId;
    private string _protocolVersion = "2025-03-26";
    private int _requestId;
    private bool _disposed;

    private McpHttpConnection(McpServerDefinition server, Uri endpoint)
    {
        _server = server;
        _endpoint = endpoint;
    }

    public int ToolCount { get; private set; }

    public static async Task<McpHttpConnection> StartAsync(McpServerDefinition server, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("URL MCP HTTP tidak valid; gunakan endpoint HTTP atau HTTPS.");
        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
            throw new InvalidOperationException("Server MCP remote harus memakai HTTPS. HTTP hanya diizinkan untuk loopback.");
        var connection = new McpHttpConnection(server, endpoint);
        try
        {
            var initialized = await connection.RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = connection._protocolVersion,
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "DepupCode", ["version"] = "2.5.0" }
            }, cancellationToken);
            var negotiated = initialized["protocolVersion"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(negotiated)) connection._protocolVersion = negotiated;
            await connection.NotifyAsync("notifications/initialized", new JObject(), cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task<IReadOnlyList<JObject>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/list", new JObject(), cancellationToken);
        var tools = result["tools"] is JArray array ? array.OfType<JObject>().ToList() : [];
        ToolCount = tools.Count;
        return tools;
    }

    public async Task<string> CallToolAsync(string toolName, JObject arguments, CancellationToken cancellationToken)
    {
        var result = await RequestAsync("tools/call", new JObject { ["name"] = toolName, ["arguments"] = arguments }, cancellationToken);
        var text = result["content"] is JArray items
            ? string.Join("\n", items.OfType<JObject>()
                .Where(item => string.Equals(item["type"]?.Value<string>(), "text", StringComparison.OrdinalIgnoreCase))
                .Select(item => item["text"]?.Value<string>()).Where(value => !string.IsNullOrWhiteSpace(value)))
            : "";
        if (string.IsNullOrWhiteSpace(text) && result["structuredContent"] is JToken structured)
            text = structured.ToString(Formatting.None);
        if (result["isError"]?.Value<bool>() == true) text = "MCP tool melaporkan error: " + text;
        if (text.Length > 8_000) text = text[..8_000] + "\n[Hasil MCP dipotong.]";
        return string.IsNullOrWhiteSpace(text) ? "MCP tool selesai tanpa output teks." : text;
    }

    private async Task<JObject> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var response = await PostAsync(new JObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters
        }, id, cancellationToken);
        if (response["error"] is JObject error)
            throw new InvalidOperationException(error["message"]?.Value<string>() ?? "MCP JSON-RPC gagal.");
        return response["result"] as JObject ?? new JObject();
    }

    private async Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken)
    {
        await PostAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, null, cancellationToken);
    }

    private async Task<JObject> PostAsync(JObject message, int? expectedId, CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McpHttpConnection));
        await _rpcLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_server.TimeoutMilliseconds);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", _protocolVersion);
            if (!string.IsNullOrWhiteSpace(_sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            foreach (var (name, value) in _server.Headers)
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Accept", StringComparison.OrdinalIgnoreCase)) continue;
                request.Headers.TryAddWithoutValidation(name, value);
            }
            request.Content = new StringContent(message.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                _sessionId = sessionValues.FirstOrDefault();
            if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent && expectedId is null)
                return new JObject();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Endpoint MCP mengembalikan HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");

            var body = await ReadBoundedBodyAsync(response.Content, timeout.Token);
            if (expectedId is null || string.IsNullOrWhiteSpace(body)) return new JObject();
            var rpcResponse = ParseRpcResponse(body, expectedId.Value);
            if (rpcResponse is null) throw new InvalidOperationException("Endpoint MCP tidak mengembalikan respons JSON-RPC yang cocok.");
            return rpcResponse;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Server MCP melewati batas {_server.TimeoutMilliseconds / 1000} detik.");
        }
        finally { _rpcLock.Release(); }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new InvalidOperationException("Respons MCP melebihi batas 4 MB.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static JObject? ParseRpcResponse(string body, int expectedId)
    {
        try
        {
            var direct = JObject.Parse(body);
            if (direct["id"]?.ToString() == expectedId.ToString()) return direct;
        }
        catch (JsonReaderException) { }

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var json = trimmed[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            try
            {
                var item = JObject.Parse(json);
                if (item["id"]?.ToString() == expectedId.ToString()) return item;
            }
            catch (JsonReaderException) { }
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _rpcLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
