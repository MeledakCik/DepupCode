using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record LanguageServerDefinition(
    string Id,
    string LanguageId,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Extensions,
    bool FromWorkspaceConfiguration);

/// <summary>Small LSP client for stdio language servers, with explicit approval and bounded protocol handling.</summary>
public sealed class LanguageServerService : IAsyncDisposable
{
    private const int MaxConfigBytes = 32 * 1024;
    private readonly string _root;
    private readonly Func<LanguageServerDefinition, Task<bool>> _approve;
    private readonly Func<JObject, Task<bool>>? _applyWorkspaceEdit;
    private readonly List<LanguageServerDefinition> _definitions;
    private readonly Dictionary<string, LanguageServerClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _openedDocuments = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _documentVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public event Action<string, JArray>? DiagnosticsReceived;
    public event Action<string>? StatusChanged;

    public LanguageServerService(string workspaceRoot, Func<LanguageServerDefinition, Task<bool>> approve,
        Func<JObject, Task<bool>>? applyWorkspaceEdit = null)
    {
        _root = Path.GetFullPath(workspaceRoot);
        _approve = approve;
        _applyWorkspaceEdit = applyWorkspaceEdit;
        _definitions = LoadDefinitions(_root);
    }

    public bool HasDefinitionFor(string path) => FindDefinition(path) is not null;

    public string DescribeSupport(string path)
    {
        var definition = FindDefinition(path);
        if (definition is null) return "Language server tidak terpasang atau belum dikonfigurasi. Klik untuk panduan.";
        return $"Language Server Protocol · {definition.LanguageId} · {definition.Command}";
    }

    public static string CreateStarterConfig() => new JObject
    {
        ["servers"] = new JArray
        {
            new JObject
            {
                ["id"] = "typescript", ["languageId"] = "typescript", ["command"] = "typescript-language-server",
                ["args"] = new JArray("--stdio"), ["extensions"] = new JArray(".ts", ".tsx", ".js", ".jsx")
            },
            new JObject
            {
                ["id"] = "python", ["languageId"] = "python", ["command"] = "pyright-langserver",
                ["args"] = new JArray("--stdio"), ["extensions"] = new JArray(".py", ".pyi")
            },
            new JObject
            {
                ["id"] = "csharp", ["languageId"] = "csharp", ["command"] = "csharp-ls",
                ["args"] = new JArray(), ["extensions"] = new JArray(".cs", ".csx")
            }
        }
    }.ToString(Formatting.Indented);

    public async Task<bool> OpenDocumentAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsUnderWorkspace(fullPath)) return false;
        var definition = FindDefinition(fullPath);
        if (definition is null) return false;
        var key = definition.Id;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_clients.TryGetValue(key, out var client) || client.HasExited)
            {
                if (!await _approve(definition))
                {
                    StatusChanged?.Invoke($"Language server {definition.LanguageId} belum diizinkan.");
                    return false;
                }
                client = await LanguageServerClient.StartAsync(definition, _root, HandleNotification,
                    message => StatusChanged?.Invoke(message), cancellationToken, _applyWorkspaceEdit);
                _clients[key] = client;
                _openedDocuments[key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                StatusChanged?.Invoke($"Language server aktif: {definition.LanguageId}");
            }

            if (!_openedDocuments.TryGetValue(key, out var opened))
                _openedDocuments[key] = opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uri = new Uri(fullPath).AbsoluteUri;
            if (opened.Add(uri))
            {
                _documentVersions[uri] = 1;
                await client.NotifyAsync("textDocument/didOpen", new JObject
                {
                    ["textDocument"] = new JObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = definition.LanguageId,
                        ["version"] = 1,
                        ["text"] = content
                    }
                }, cancellationToken);
            }
            else
            {
                await ChangeDocumentCoreAsync(client, uri, content, cancellationToken);
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Language server gagal: {DiagnosticSecretRedactor.Redact(ex.Message)}");
            return false;
        }
        finally { _gate.Release(); }
    }

    public async Task ChangeDocumentAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_disposed) return;
            var pair = _openedDocuments.FirstOrDefault(item => item.Value.Contains(uri));
            if (pair.Key is null || !_clients.TryGetValue(pair.Key, out var client) || client.HasExited) return;
            await ChangeDocumentCoreAsync(client, uri, content, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Sinkronisasi language server gagal: {DiagnosticSecretRedactor.Redact(ex.Message)}");
        }
        finally { _gate.Release(); }
    }

    public async Task SaveDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pair = _openedDocuments.FirstOrDefault(item => item.Value.Contains(uri));
            if (pair.Key is not null && _clients.TryGetValue(pair.Key, out var client) && !client.HasExited)
                await client.NotifyAsync("textDocument/didSave", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri }
                }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Language server tidak menerima notifikasi simpan: {DiagnosticSecretRedactor.Redact(ex.Message)}");
        }
        finally { _gate.Release(); }
    }

    public async Task CloseDocumentAsync(string path, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pair = _openedDocuments.FirstOrDefault(item => item.Value.Remove(uri));
            if (pair.Key is null) return;
            _documentVersions.Remove(uri);
            if (_clients.TryGetValue(pair.Key, out var client) && !client.HasExited)
                await client.NotifyAsync("textDocument/didClose", new JObject
                {
                    ["textDocument"] = new JObject { ["uri"] = uri }
                }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Language server tidak menutup dokumen: {DiagnosticSecretRedactor.Redact(ex.Message)}");
        }
        finally { _gate.Release(); }
    }

    public async Task<JToken?> RequestForDocumentAsync(string path, string method, JObject parameters,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var pair = _openedDocuments.FirstOrDefault(item => item.Value.Contains(uri));
            if (pair.Key is null || !_clients.TryGetValue(pair.Key, out var client) || client.HasExited)
                return null;
            return await client.RequestAsync(method, parameters, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StatusChanged?.Invoke($"Language server tidak dapat menyelesaikan {method}: {DiagnosticSecretRedactor.Redact(ex.Message)}");
            return null;
        }
        finally { _gate.Release(); }
    }

    private async Task ChangeDocumentCoreAsync(LanguageServerClient client, string uri, string content,
        CancellationToken cancellationToken)
    {
        var version = _documentVersions.GetValueOrDefault(uri) + 1;
        _documentVersions[uri] = version;
        await client.NotifyAsync("textDocument/didChange", new JObject
        {
            ["textDocument"] = new JObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JArray(new JObject { ["text"] = content })
        }, cancellationToken);
    }

    private void HandleNotification(JObject message)
    {
        var method = message["method"]?.Value<string>();
        var parameters = message["params"] as JObject;
        if (method == "textDocument/publishDiagnostics" && parameters is not null)
        {
            var uri = parameters["uri"]?.Value<string>();
            var diagnostics = parameters["diagnostics"] as JArray;
            if (uri is not null && diagnostics is not null && Uri.TryCreate(uri, UriKind.Absolute, out var documentUri) && documentUri.IsFile)
            {
                try
                {
                    var path = Path.GetFullPath(documentUri.LocalPath);
                    if (IsUnderWorkspace(path)) DiagnosticsReceived?.Invoke(path, (JArray)diagnostics.DeepClone());
                }
                catch { }
            }
        }
        else if (method == "window/showMessage" && parameters is not null)
        {
            var messageText = parameters["message"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(messageText))
                StatusChanged?.Invoke(DiagnosticSecretRedactor.Redact(messageText));
        }
    }

    private LanguageServerDefinition? FindDefinition(string path)
    {
        var extension = Path.GetExtension(path);
        return _definitions.FirstOrDefault(definition => definition.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsUnderWorkspace(string path)
    {
        try
        {
            var relative = Path.GetRelativePath(_root, path);
            return !Path.IsPathRooted(relative) && relative != ".." &&
                   !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static List<LanguageServerDefinition> LoadDefinitions(string root)
    {
        var definitions = new List<LanguageServerDefinition>();
        var configPath = Path.Combine(root, ".depup", "language-servers.json");
        try
        {
            if (File.Exists(configPath) && new FileInfo(configPath).Length <= MaxConfigBytes)
            {
                var config = JObject.Parse(File.ReadAllText(configPath));
                foreach (var server in (config["servers"] as JArray ?? []).OfType<JObject>().Take(20))
                {
                    var id = server["id"]?.Value<string>()?.Trim();
                    var languageId = server["languageId"]?.Value<string>()?.Trim();
                    var command = server["command"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(languageId) || string.IsNullOrWhiteSpace(command)) continue;
                    var extensions = (server["extensions"] as JArray ?? []).Values<string>()
                        .Where(value => !string.IsNullOrWhiteSpace(value) && value.StartsWith('.'))
                        .Take(24).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (extensions.Count == 0) continue;
                    var arguments = (server["args"] as JArray ?? []).Values<string>()
                        .Where(value => value is not null).Take(40).Select(value => value!.Length > 2_000 ? value[..2_000] : value).ToList();
                    definitions.Add(new LanguageServerDefinition(id, languageId, command,
                        arguments, extensions, FromWorkspaceConfiguration: true));
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Language server config error: " + ex.Message);
        }

        AddDetected("typescript", "typescript", "typescript-language-server", ["--stdio"], [".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs"]);
        AddDetected("python", "python", "pyright-langserver", ["--stdio"], [".py", ".pyi"]);
        AddDetected("csharp", "csharp", "csharp-ls", [], [".cs", ".csx"]);
        AddDetected("rust", "rust", "rust-analyzer", [], [".rs"]);
        AddDetected("go", "go", "gopls", ["serve"], [".go"]);
        return definitions;

        void AddDetected(string id, string languageId, string command, IReadOnlyList<string> args, IReadOnlyList<string> extensions)
        {
            if (definitions.Any(existing => existing.Extensions.Intersect(extensions, StringComparer.OrdinalIgnoreCase).Any())) return;
            if (ResolveExecutable(root, command) is null) return;
            definitions.Add(new LanguageServerDefinition(id, languageId, command, args, extensions, FromWorkspaceConfiguration: false));
        }
    }

    public static string? ResolveExecutable(string root, string command)
    {
        var expanded = command.Replace("${workspaceFolder}", root, StringComparison.OrdinalIgnoreCase).Trim();
        if (Path.IsPathRooted(expanded)) return File.Exists(expanded) ? Path.GetFullPath(expanded) : null;
        if (expanded.Contains(Path.DirectorySeparatorChar) || expanded.Contains(Path.AltDirectorySeparatorChar))
        {
            var workspacePath = Path.GetFullPath(Path.Combine(root, expanded));
            return File.Exists(workspacePath) ? workspacePath : null;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = directory.Trim().Trim('"');
            foreach (var suffix in new[] { "", ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(clean, expanded.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? expanded : expanded + suffix);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
        _openedDocuments.Clear();
        _gate.Dispose();
    }

    private sealed class LanguageServerClient : IAsyncDisposable
    {
        private static readonly string[] SafeEnvironmentNames =
        [
            "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP",
            "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles",
            "ProgramFiles(x86)", "CommonProgramFiles", "CommonProgramFiles(x86)", "OS",
            "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
        ];

        private readonly Process _process;
        private readonly string _root;
        private readonly Func<JObject, CancellationToken, Task> _send;
        private readonly Action<JObject> _notification;
        private readonly Action<string> _status;
        private readonly Func<JObject, Task<bool>>? _applyWorkspaceEdit;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pending = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _lifetime = new();
        private Task? _readerTask;
        private int _requestId;
        private bool _disposed;

        public bool HasExited => _process.HasExited;

        private LanguageServerClient(Process process, string root, Action<JObject> notification, Action<string> status,
            Func<JObject, Task<bool>>? applyWorkspaceEdit)
        {
            _process = process;
            _root = root;
            _notification = notification;
            _status = status;
            _applyWorkspaceEdit = applyWorkspaceEdit;
            _send = SendMessageAsync;
        }

        public static async Task<LanguageServerClient> StartAsync(LanguageServerDefinition definition, string root,
            Action<JObject> notification, Action<string> status, CancellationToken cancellationToken,
            Func<JObject, Task<bool>>? applyWorkspaceEdit)
        {
            var executable = ResolveExecutable(root, definition.Command)
                ?? throw new FileNotFoundException($"Executable language server tidak ditemukan: {definition.Command}");
            var startInfo = CreateStartInfo(executable, definition.Arguments, root);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new InvalidOperationException("Language server tidak dapat dimulai.");
            var client = new LanguageServerClient(process, root, notification, status, applyWorkspaceEdit);
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) status("LSP: " + DiagnosticSecretRedactor.Redact(args.Data));
            };
            process.BeginErrorReadLine();
            client._readerTask = Task.Run(client.ReadLoopAsync);
            try
            {
                var rootUri = new Uri(Path.GetFullPath(root) + Path.DirectorySeparatorChar).AbsoluteUri;
                await client.RequestAsync("initialize", new JObject
                {
                    ["processId"] = Environment.ProcessId,
                    ["clientInfo"] = new JObject { ["name"] = "DepupCode", ["version"] = "2.5.0" },
                    ["rootUri"] = rootUri,
                    ["workspaceFolders"] = new JArray(new JObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(root) }),
                    ["capabilities"] = new JObject
                    {
                        ["workspace"] = new JObject
                        {
                            ["configuration"] = true,
                            ["workspaceFolders"] = true,
                            ["workspaceEdit"] = new JObject { ["documentChanges"] = true }
                        },
                        ["textDocument"] = new JObject
                        {
                            ["synchronization"] = new JObject { ["dynamicRegistration"] = false, ["didSave"] = true },
                            ["completion"] = new JObject { ["completionItem"] = new JObject { ["snippetSupport"] = true } },
                            ["definition"] = new JObject { ["dynamicRegistration"] = false },
                            ["hover"] = new JObject { ["dynamicRegistration"] = false },
                            ["references"] = new JObject { ["dynamicRegistration"] = false },
                            ["rename"] = new JObject
                            {
                                ["dynamicRegistration"] = false,
                                ["prepareSupport"] = true,
                                ["honorsChangeAnnotations"] = false
                            },
                            ["codeAction"] = new JObject
                            {
                                ["dynamicRegistration"] = false,
                                ["codeActionLiteralSupport"] = new JObject
                                {
                                    ["codeActionKind"] = new JObject { ["valueSet"] = new JArray("", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports") }
                                },
                                ["resolveSupport"] = new JObject { ["properties"] = new JArray("edit", "command") }
                            },
                            ["publishDiagnostics"] = new JObject { ["relatedInformation"] = true }
                        }
                    },
                    ["trace"] = "off"
                }, cancellationToken);
                await client.NotifyAsync("initialized", new JObject(), cancellationToken);
                return client;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }

        public async Task<JToken?> RequestAsync(string method, JObject parameters, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _requestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("LSP request ID collision.");
            try
            {
                await _send(new JObject { ["jsonrpc"] = "2.0", ["id"] = int.Parse(id), ["method"] = method, ["params"] = parameters }, cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var response = await completion.Task.WaitAsync(timeout.Token);
                if (response["error"] is JObject error)
                    throw new InvalidOperationException(error["message"]?.Value<string>() ?? "Language server request failed.");
                return response["result"];
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                throw new TimeoutException($"Language server request '{method}' melewati batas 20 detik.");
            }
            finally { _pending.TryRemove(id, out _); }
        }

        public Task NotifyAsync(string method, JObject parameters, CancellationToken cancellationToken) =>
            _send(new JObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters }, cancellationToken);

        private async Task SendMessageAsync(JObject message, CancellationToken cancellationToken = default)
        {
            var body = Encoding.UTF8.GetBytes(message.ToString(Formatting.None));
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(header, cancellationToken);
                await _process.StandardInput.BaseStream.WriteAsync(body, cancellationToken);
                await _process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            }
            finally { _writeGate.Release(); }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var json = await ReadMessageAsync(_process.StandardOutput.BaseStream, _lifetime.Token);
                    if (json is null) break;
                    JObject message;
                    try { message = JObject.Parse(json); }
                    catch { continue; }

                    if (message["method"]?.Value<string>() is { } method)
                    {
                        if (message["id"] is JToken requestId)
                        {
                            JToken result;
                            if (method == "workspace/applyEdit")
                            {
                                var applied = false;
                                var failureReason = "Workspace edit ditolak atau tidak dapat diterapkan.";
                                if (_applyWorkspaceEdit is not null && message["params"]?["edit"] is JObject edit)
                                {
                                    try
                                    {
                                        applied = await _applyWorkspaceEdit((JObject)edit.DeepClone());
                                        failureReason = applied ? "" : "Workspace edit dibatalkan.";
                                    }
                                    catch (Exception ex) { failureReason = DiagnosticSecretRedactor.Redact(ex.Message); }
                                }
                                result = new JObject { ["applied"] = applied, ["failureReason"] = applied ? null : failureReason };
                            }
                            else
                            {
                                result = method switch
                                {
                                    "workspace/configuration" => new JArray((message["params"]?["items"] as JArray ?? []).Select(_ => JValue.CreateNull())),
                                    "workspace/workspaceFolders" => new JArray(new JObject
                                    {
                                        ["uri"] = new Uri(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).AbsoluteUri,
                                        ["name"] = Path.GetFileName(_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                    }),
                                    _ => JValue.CreateNull()
                                };
                            }
                            await _send(new JObject { ["jsonrpc"] = "2.0", ["id"] = requestId.DeepClone(), ["result"] = result }, _lifetime.Token);
                        }
                        else _notification(message);
                    }
                    else if (message["id"] is JToken id && _pending.TryGetValue(id.ToString(), out var completion))
                    {
                        completion.TrySetResult(message);
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (!_lifetime.IsCancellationRequested) _status("Language server berhenti: " + DiagnosticSecretRedactor.Redact(ex.Message));
            }
            finally
            {
                foreach (var completion in _pending.Values)
                    completion.TrySetException(new InvalidOperationException("Language server menutup koneksi."));
            }
        }

        private static async Task<string?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
        {
            const int maxHeaderBytes = 8_192;
            var header = new List<byte>(128);
            var one = new byte[1];
            var matched = 0;
            var delimiter = new byte[] { 13, 10, 13, 10 };
            while (header.Count < maxHeaderBytes)
            {
                var read = await stream.ReadAsync(one.AsMemory(), cancellationToken);
                if (read == 0) return header.Count == 0 ? null : throw new EndOfStreamException("LSP header terpotong.");
                header.Add(one[0]);
                matched = one[0] == delimiter[matched] ? matched + 1 : one[0] == delimiter[0] ? 1 : 0;
                if (matched == delimiter.Length) break;
            }
            if (matched != delimiter.Length) throw new InvalidDataException("Header LSP melewati batas 8 KB.");

            var headerText = Encoding.ASCII.GetString(header.ToArray());
            var contentLength = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(':', 2))
                .FirstOrDefault(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))?[1];
            if (!int.TryParse(contentLength?.Trim(), out var length) || length < 0 || length > 8 * 1024 * 1024)
                throw new InvalidDataException("Panjang pesan LSP tidak valid atau melebihi 8 MB.");
            var body = new byte[length];
            var total = 0;
            while (total < body.Length)
            {
                var read = await stream.ReadAsync(body.AsMemory(total), cancellationToken);
                if (read == 0) throw new EndOfStreamException("LSP message body terpotong.");
                total += read;
            }
            return Encoding.UTF8.GetString(body);
        }

        private static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> args, string root)
        {
            var isBatch = Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase);
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
            foreach (var name in SafeEnvironmentNames)
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;

            if (OperatingSystem.IsWindows() && isBatch)
            {
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                var command = "\"\"" + executable + "\"" +
                              (args.Count == 0 ? "" : " " + string.Join(" ", args.Select(arg => QuoteCommandArgument(arg.Replace("${workspaceFolder}", root, StringComparison.OrdinalIgnoreCase))))) + "\"";
                start.ArgumentList.Add(command);
            }
            else
            {
                start.FileName = executable;
                foreach (var arg in args)
                    start.ArgumentList.Add(arg.Replace("${workspaceFolder}", root, StringComparison.OrdinalIgnoreCase));
            }
            return start;
        }

        private static string QuoteCommandArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch { }
            try { if (_readerTask is not null) await _readerTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            _process.Dispose();
            _writeGate.Dispose();
            _lifetime.Dispose();
        }
    }
}
