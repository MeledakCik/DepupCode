using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record ExtensionHostCommand(string ExtensionId, string ExtensionName, string CommandId, string Title);
public sealed record ExtensionHostUiRequest(string Type, string Prompt, IReadOnlyList<string> Items, string? Path);

/// <summary>
/// Runs Node-based extension activation code in a separate process with an explicit user approval
/// gate. The vscode module is a deliberately small compatibility shim, not the full VS Code API.
/// </summary>
public sealed class VsCodeExtensionHostService : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RuntimeInstance> _active = new(StringComparer.OrdinalIgnoreCase);
    private int _requestCounter;

    public event Action<string>? Output;
    public event Action? CommandsChanged;
    public event Func<ExtensionHostUiRequest, Task<string?>>? UserInteractionRequested;

    public bool IsActive(string extensionId)
    {
        try
        {
            lock (_sync) return _active.TryGetValue(extensionId, out var instance) && !instance.Process.HasExited;
        }
        catch { return false; }
    }

    public IReadOnlyList<ExtensionHostCommand> GetCommands()
    {
        lock (_sync)
            return _active.Values.SelectMany(instance => instance.Commands)
                .OrderBy(command => command.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task ActivateAsync(
        ExtensionMarketplaceItem item,
        string extensionDirectory,
        string? workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!item.IsInstalled) throw new InvalidOperationException("Pasang extension sebelum mengaktifkannya.");
        extensionDirectory = Path.GetFullPath(extensionDirectory);
        var manifestPath = Path.Combine(extensionDirectory, "package.json");
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Manifest extension tidak ditemukan.", manifestPath);
        var manifest = JObject.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var main = ResolveEntryPoint(extensionDirectory, manifest.Value<string>("main"));
        if (main is null || !File.Exists(main))
            throw new NotSupportedException("Extension ini tidak memiliki entry point Node.js yang dapat dijalankan.");

        var root = Directory.Exists(workspaceRoot) ? Path.GetFullPath(workspaceRoot!) : extensionDirectory;
        var node = LanguageServerService.ResolveExecutable(root, "node");
        if (node is null) throw new InvalidOperationException("Node.js tidak ditemukan. Pasang Node.js lalu buka ulang Depup Code.");

        var hostScript = await EnsureHostScriptAsync(cancellationToken);
        var sandbox = WindowsExtensionSandbox.Start(node, hostScript, extensionDirectory, root, item.Identifier);
        var instance = new RuntimeInstance(item.Identifier, item.Title, sandbox);
        var process = sandbox.Process;
        lock (_sync)
        {
            if (_active.ContainsKey(item.Identifier))
            {
                sandbox.Dispose();
                throw new InvalidOperationException("Extension ini sudah aktif.");
            }
            _active[item.Identifier] = instance;
        }

        instance.StdoutPump = PumpStdoutAsync(instance);
        instance.StderrPump = PumpStderrAsync(instance);
        CommandsChanged?.Invoke();
        Output?.Invoke($"[Extensions] Mengaktifkan {item.Title} ({item.Identifier})…");
        try
        {
            var exitTask = process.WaitForExitAsync(cancellationToken);
            var winner = await Task.WhenAny(instance.Ready.Task, exitTask, Task.Delay(TimeSpan.FromSeconds(25), cancellationToken));
            if (winner == exitTask)
                throw new InvalidOperationException($"Extension berhenti sebelum aktif (exit code {process.ExitCode}).");
            if (winner != instance.Ready.Task)
                throw new TimeoutException("Extension tidak menyelesaikan aktivasi dalam 25 detik.");
            await instance.Ready.Task;
            if (process.HasExited) throw new InvalidOperationException("Proses extension berhenti saat aktivasi.");
            Output?.Invoke($"[Extensions] {item.Title} aktif dengan {instance.Commands.Count} command.");
            CommandsChanged?.Invoke();
        }
        catch
        {
            await RemoveAndStopAsync(instance);
            throw;
        }
    }

    public Task<JToken?> ExecuteCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
        ExecuteCommandWithArgumentsAsync(commandId, null, cancellationToken);

    public async Task<JToken?> ExecuteCommandWithArgumentsAsync(string commandId, JArray? arguments,
        CancellationToken cancellationToken = default)
    {
        RuntimeInstance? instance;
        lock (_sync)
            instance = _active.Values.FirstOrDefault(candidate => candidate.Commands.Any(command =>
                command.CommandId.Equals(commandId, StringComparison.Ordinal)));
        if (instance is null || instance.Process.HasExited)
            throw new InvalidOperationException("Command extension tidak aktif lagi.");

        var requestId = Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
        instance.Pending[requestId] = completion;
        await WriteRequestAsync(instance, new JObject
        {
            ["type"] = "execute", ["id"] = requestId, ["command"] = commandId,
            ["args"] = arguments?.DeepClone() ?? new JArray()
        }, cancellationToken);
        try
        {
            var winner = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));
            if (winner != completion.Task) throw new TimeoutException($"Command extension '{commandId}' tidak merespons dalam 30 detik.");
            return await completion.Task;
        }
        finally { instance.Pending.TryRemove(requestId, out _); }
    }

    public async Task<IReadOnlyList<JToken>> ProvideLanguageItemsAsync(string providerKind, string languageId,
        string filePath, string content, int line, int character, JObject? context = null, JObject? range = null,
        CancellationToken cancellationToken = default)
    {
        RuntimeInstance[] instances;
        lock (_sync) instances = _active.Values.Where(instance => !instance.Process.HasExited).Take(16).ToArray();
        if (instances.Length == 0) return [];
        var requests = instances.Select(async instance =>
        {
            var id = Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
            instance.Pending[id] = completion;
            try
            {
                await WriteRequestAsync(instance, new JObject
                {
                    ["type"] = "provide", ["id"] = id, ["provider"] = providerKind,
                    ["document"] = new JObject { ["path"] = filePath, ["languageId"] = languageId, ["content"] = content },
                    ["position"] = new JObject { ["line"] = line, ["character"] = character },
                    ["context"] = context?.DeepClone(), ["range"] = range?.DeepClone()
                }, cancellationToken);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            }
            catch (TimeoutException) { Output?.Invoke($"[Extensions:{instance.Name}] {providerKind} provider melewati batas 4 detik."); return null; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            catch (Exception ex) { Output?.Invoke($"[Extensions:{instance.Name}] {providerKind} provider gagal: {DiagnosticSecretRedactor.Redact(ex.Message)}"); return null; }
            finally { instance.Pending.TryRemove(id, out _); }
        });
        var results = await Task.WhenAll(requests);
        return results.Where(result => result is not null)
            .SelectMany(result => result is JArray array ? array.Children().Select(child => child.DeepClone()) : [result!.DeepClone()])
            .Take(200).ToArray();
    }

    public async Task DeactivateAsync(string extensionId)
    {
        RuntimeInstance? instance;
        lock (_sync) _active.TryGetValue(extensionId, out instance);
        if (instance is null) return;

        try
        {
            if (!instance.Process.HasExited)
            {
                var requestId = Interlocked.Increment(ref _requestCounter).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var completion = new TaskCompletionSource<JToken?>(TaskCreationOptions.RunContinuationsAsynchronously);
                instance.Pending[requestId] = completion;
                await WriteRequestAsync(instance, new JObject { ["type"] = "deactivate", ["id"] = requestId }, CancellationToken.None);
                await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            }
        }
        catch { }
        await RemoveAndStopAsync(instance);
        Output?.Invoke($"[Extensions] {instance.Name} dinonaktifkan.");
    }

    private async Task PumpStdoutAsync(RuntimeInstance instance)
    {
        try
        {
            using var reader = new StreamReader(instance.Sandbox.Output, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length > 1_000_000)
                {
                    Output?.Invoke($"[Extensions] Output {instance.Name} dibuang karena melebihi batas.");
                    continue;
                }
                JObject? message;
                try { message = JObject.Parse(line); }
                catch
                {
                    Output?.Invoke($"[Extensions] {instance.Name}: {DiagnosticSecretRedactor.Redact(line)}");
                    continue;
                }

                switch (message.Value<string>("type"))
                {
                    case "ready":
                        instance.Commands = ReadCommands(instance, message["commands"] as JArray);
                        instance.Ready.TrySetResult(true);
                        CommandsChanged?.Invoke();
                        break;
                    case "fatal":
                        instance.Ready.TrySetException(new InvalidOperationException(DiagnosticSecretRedactor.Redact(message.Value<string>("error") ?? "Aktivasi extension gagal.")));
                        Output?.Invoke($"[Extensions] {instance.Name}: {DiagnosticSecretRedactor.Redact(message.Value<string>("error") ?? "aktivasi gagal")}");
                        break;
                    case "message":
                    case "log":
                        Output?.Invoke($"[Extensions:{instance.Name}] {DiagnosticSecretRedactor.Redact(message.Value<string>("text") ?? "")}");
                        break;
                    case "result":
                        var id = message.Value<string>("id") ?? "";
                        if (instance.Pending.TryGetValue(id, out var pending))
                        {
                            if (message.Value<bool?>("ok") == false)
                                pending.TrySetException(new InvalidOperationException(DiagnosticSecretRedactor.Redact(message.Value<string>("error") ?? "Command extension gagal.")));
                            else pending.TrySetResult(message["value"]?.DeepClone());
                        }
                        break;
                    case "uiRequest":
                        _ = HandleUiRequestAsync(instance, message);
                        break;
                }
            }
        }
        catch (Exception ex) { Output?.Invoke($"[Extensions:{instance.Name}] Output host berhenti: {DiagnosticSecretRedactor.Redact(ex.Message)}"); }
        finally
        {
            instance.Ready.TrySetException(new InvalidOperationException("Proses extension berhenti sebelum siap."));
            foreach (var pending in instance.Pending.Values)
                pending.TrySetException(new InvalidOperationException($"Extension {instance.Name} berhenti."));
            try { instance.Sandbox.Dispose(); } catch { }
            lock (_sync) _active.Remove(instance.Id);
            CommandsChanged?.Invoke();
        }
    }

    private async Task PumpStderrAsync(RuntimeInstance instance)
    {
        try
        {
            using var reader = new StreamReader(instance.Sandbox.Error, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line)
                if (line.Length > 0) Output?.Invoke($"[Extensions:{instance.Name}] {DiagnosticSecretRedactor.Redact(line[..Math.Min(line.Length, 4000)])}");
        }
        catch { }
    }

    private async Task HandleUiRequestAsync(RuntimeInstance instance, JObject message)
    {
        var requestId = message.Value<string>("id") ?? "";
        string? value = null;
        try
        {
            var request = new ExtensionHostUiRequest(
                message.Value<string>("action") ?? "",
                DiagnosticSecretRedactor.Redact(message.Value<string>("prompt") ?? "Extension meminta input"),
                (message["items"] as JArray ?? []).Values<string>().Where(item => item is not null).Select(item => item!).Take(100).ToArray(),
                message.Value<string>("path"));
            if (UserInteractionRequested is { } handlers)
                foreach (Func<ExtensionHostUiRequest, Task<string?>> handler in handlers.GetInvocationList())
                    value = await handler(request);
        }
        catch (Exception ex) { Output?.Invoke($"[Extensions:{instance.Name}] Interaksi gagal: {DiagnosticSecretRedactor.Redact(ex.Message)}"); }
        try
        {
            await WriteRequestAsync(instance, new JObject { ["type"] = "uiResult", ["id"] = requestId, ["value"] = value }, CancellationToken.None);
        }
        catch { }
    }

    private static IReadOnlyList<ExtensionHostCommand> ReadCommands(RuntimeInstance instance, JArray? values) =>
        (values ?? []).OfType<JObject>()
            .Select(command => new ExtensionHostCommand(
                instance.Id, instance.Name,
                command.Value<string>("command") ?? "",
                command.Value<string>("title") ?? command.Value<string>("command") ?? "Extension Command"))
            .Where(command => command.CommandId.Length > 0)
            .DistinctBy(command => command.CommandId, StringComparer.Ordinal)
            .ToArray();

    private static async Task WriteRequestAsync(RuntimeInstance instance, JObject request, CancellationToken cancellationToken)
    {
        await instance.WriteLock.WaitAsync(cancellationToken);
        try
        {
            if (instance.Process.HasExited) throw new InvalidOperationException("Extension host sudah berhenti.");
            await instance.Sandbox.Input.WriteAsync(Encoding.UTF8.GetBytes(request.ToString(Formatting.None) + "\n"), cancellationToken);
            await instance.Sandbox.Input.FlushAsync(cancellationToken);
        }
        finally { instance.WriteLock.Release(); }
    }

    private async Task RemoveAndStopAsync(RuntimeInstance instance)
    {
        lock (_sync) _active.Remove(instance.Id);
        try { instance.Sandbox.Dispose(); } catch { }
        try { await instance.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        try { await Task.WhenAll(instance.StdoutPump, instance.StderrPump); } catch { }
        instance.WriteLock.Dispose();
        CommandsChanged?.Invoke();
    }

    private static string? ResolveEntryPoint(string extensionDirectory, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var normalized = raw.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (Path.IsPathRooted(raw) || normalized.Split('/').Any(segment => segment is ".." or "")) return null;
        var root = Path.GetFullPath(extensionDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(extensionDirectory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static async Task<string> EnsureHostScriptAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupCode", "runtime");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vscode-extension-host.cjs");
        await File.WriteAllTextAsync(path, HostScript, cancellationToken);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_sync) ids = _active.Keys.ToArray();
        foreach (var id in ids) await DeactivateAsync(id);
    }

    private sealed class RuntimeInstance(string id, string name, WindowsExtensionSandbox sandbox)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public WindowsExtensionSandbox Sandbox { get; } = sandbox;
        public Process Process { get; } = sandbox.Process;
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentDictionary<string, TaskCompletionSource<JToken?>> Pending { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public IReadOnlyList<ExtensionHostCommand> Commands { get; set; } = [];
        public Task StdoutPump { get; set; } = Task.CompletedTask;
        public Task StderrPump { get; set; } = Task.CompletedTask;
    }

    private const string HostScript = """
        'use strict';
        const fs = require('fs');
        const path = require('path');
        const { pathToFileURL, fileURLToPath } = require('url');
        const readline = require('readline');
        const Module = require('module');
        const protocolWrite = process.stdout.write.bind(process.stdout);
        process.stdout.write = function(chunk, encoding, callback) {
          process.stderr.write(Buffer.isBuffer(chunk) ? chunk : String(chunk), encoding, callback);
          return true;
        };
        const send = value => protocolWrite(JSON.stringify(value) + '\n');
        const extRoot = path.resolve(process.argv[2]);
        const workspaceRoot = path.resolve(process.argv[3] || extRoot);
        const extensionId = String(process.argv[4] || 'extension');
        const storageRoot = path.resolve(process.argv[5] || extRoot);
        const manifest = JSON.parse(fs.readFileSync(path.join(extRoot, 'package.json'), 'utf8'));
        const registered = new Map();
        const languageProviders = new Map();
        const disposables = [];
        const configs = new Map();
        const pendingUi = new Map();
        let activeEditor;
        let uiCounter = 0;
        let providerCounter = 0;
        const askUi = (action, prompt, items, filePath) => new Promise(resolve => {
          const id=String(++uiCounter); pendingUi.set(id, resolve);
          send({type:'uiRequest', id, action, prompt:String(prompt || ''), items:items || [], path:filePath});
          setTimeout(()=>{ if(pendingUi.has(id)){pendingUi.delete(id);resolve(undefined);} }, 300000);
        });
        const disposable = () => ({ dispose() {} });
        const unsupported = api => () => { throw new Error('Depup Code extension host API belum mendukung vscode.' + api); };
        const message = async (text, options, level) => {
          const choices=options.map(item=>typeof item==='string'?item:(item && item.title || item && item.label || String(item)));
          const selected=await askUi('message',String(text ?? ''),choices);
          return selected===undefined?undefined:options[choices.indexOf(selected)];
        };
        const notify = (level, text) => send({ type: 'message', level, text: String(text ?? '').slice(0, 4000) });
        class Position { constructor(line, character) { this.line=line; this.character=character; } }
        class Range { constructor(start, startChar, end, endChar) { this.start = start instanceof Position ? start : new Position(start, startChar); this.end = end instanceof Position ? end : new Position(end, endChar); } }
        class Selection extends Range { constructor(anchorLine, anchorCharacter, activeLine, activeCharacter) { super(anchorLine, anchorCharacter, activeLine, activeCharacter); this.anchor=this.start; this.active=this.end; } }
        class Uri {
          constructor(value) { this.scheme='file'; this.fsPath=path.resolve(value); this.path=this.fsPath.replace(/\\/g, '/'); this.toString=()=>pathToFileURL(this.fsPath).toString(); }
          static file(value) { return new Uri(path.resolve(value)); }
          static parse(value) { return new Uri(fileURLToPath(String(value))); }
          static joinPath(base, ...parts) { return Uri.file(path.join(base.fsPath, ...parts)); }
        }
        class WorkspaceEdit {
          constructor() { this._changes=new Map(); }
          _add(uri, edit) { const key=asUri(uri).toString(); const edits=this._changes.get(key)||[]; edits.push(edit); this._changes.set(key,edits); }
          set(uri, edits) { this._changes.set(asUri(uri).toString(),Array.from(edits||[])); }
          replace(uri, range, newText) { this._add(uri,{range,newText:String(newText)}); }
          insert(uri, position, newText) { this.replace(uri,new Range(position.line,position.character,position.line,position.character),newText); }
          delete(uri, range) { this.replace(uri,range,''); }
          entries() { return [...this._changes.entries()].map(([uri,edits])=>[Uri.parse(uri),edits]); }
          get(uri) { return this._changes.get(asUri(uri).toString()); }
          has(uri) { return this._changes.has(asUri(uri).toString()); }
          get size() { return this._changes.size; }
        }
        const serializeWorkspaceEdit = edit => {
          if (!(edit instanceof WorkspaceEdit)) return edit;
          const changes={};
          for (const [uri,edits] of edit._changes) changes[uri]=edits.map(item=>({range:item.range,newText:item.newText}));
          return {changes};
        };
        const register = (id, callback) => {
          if (typeof id !== 'string' || typeof callback !== 'function') return disposable();
          registered.set(id, callback);
          const item = { dispose: () => registered.delete(id) };
          disposables.push(item);
          return item;
        };
        const selectorMatches = (selector, languageId, filePath) => {
          const selectors=Array.isArray(selector)?selector:[selector];
          return selectors.some(entry=>{
            if(typeof entry==='string') return entry==='*'||entry===languageId;
            if(!entry||typeof entry!=='object') return false;
            if(entry.language&&entry.language!=='*'&&entry.language!==languageId) return false;
            if(entry.scheme&&entry.scheme!=='*'&&entry.scheme!=='file') return false;
            if(entry.pattern){
              const relative=path.relative(workspaceRoot,filePath).replace(/\\/g,'/');
              const pattern=String(entry.pattern).replace(/\\/g,'/');
              const escaped=pattern.replace(/\*\*/g,'@@DOUBLESTAR@@').replace(/\*/g,'@@SINGLESTAR@@').replace(/[.+?^${}()|[\]\\]/g,'\\$&').replace(/@@SINGLESTAR@@/g,'[^/]*').replace(/@@DOUBLESTAR@@/g,'.*');
              if(!new RegExp('^'+escaped+'$','i').test(relative)) return false;
            }
            return true;
          });
        };
        const registerLanguageProvider = (kind, selector, provider, metadata={}) => {
          const id=String(++providerCounter);
          languageProviders.set(id,{kind,selector,provider,metadata});
          const item={dispose:()=>languageProviders.delete(id)};
          disposables.push(item);
          return item;
        };
        const commandList = (manifest.contributes && manifest.contributes.commands || []).map(item => ({ command: item.command, title: item.title || item.command }));
        const outputChannel = name => ({
          name, append: text => send({type:'log', text:String(text).slice(0,4000)}),
          appendLine: text => send({type:'log', text:String(text).slice(0,4000)}),
          clear: () => {}, show: () => {}, hide: () => {}, dispose: () => {}
        });
        const asUri = value => value instanceof Uri ? value : (typeof value === 'string' ? Uri.file(value) : value);
        const insideWorkspace = value => {
          const full=path.resolve(asUri(value).fsPath);
          if (full !== workspaceRoot && !full.startsWith(workspaceRoot + path.sep)) throw new Error('Path harus berada di workspace yang disetujui.');
          return full;
        };
        const fileType = stat => stat.isDirectory() ? 2 : stat.isFile() ? 1 : 0;
        const createTextDocument = (uri, file, content, languageId) => {
          const getLines=()=>content.split(/\r?\n/);
          const offsetAt=pos=>getLines().slice(0,pos.line).reduce((n,s)=>n+s.length+1,0)+pos.character;
          const doc={uri,fileName:file,isUntitled:false,languageId,version:1,eol:content.includes('\r\n')?'\r\n':'\n',encoding:'utf8',
            get lineCount(){return getLines().length;},
            getText:range=>range?content.slice(offsetAt(range.start),offsetAt(range.end)):content,
            lineAt:line=>{const lines=getLines(),n=typeof line==='number'?line:line.line;return {lineNumber:n,text:lines[n]||'',range:new Range(n,0,n,(lines[n]||'').length),rangeIncludingLineBreak:new Range(n,0,Math.min(n+1,lines.length-1),0),firstNonWhitespaceCharacterIndex:(lines[n]||'').search(/\S|$/),isEmptyOrWhitespace:!(lines[n]||'').trim()}},
            offsetAt,
            positionAt:offset=>{const parts=content.slice(0,offset).split('\n');return new Position(parts.length-1,parts.at(-1).length)},save:async()=>true,
            validatePosition:pos=>{const lines=getLines(),line=Math.max(0,Math.min(lines.length-1,pos.line));return new Position(line,Math.max(0,Math.min(lines[line].length,pos.character)));},
            _setText:value=>{content=value;doc.version++;}};
          return doc;
        };
        const textDocument = async value => {
          const uri=asUri(value), file=insideWorkspace(uri);
          return createTextDocument(uri,file,await fs.promises.readFile(file,'utf8'),path.extname(file).slice(1));
        };
        const snapshotDocument = data => {
          const file=insideWorkspace(Uri.file(data.path));
          return createTextDocument(Uri.file(file),file,String(data.content ?? ''),String(data.languageId || path.extname(file).slice(1)));
        };
        fs.mkdirSync(path.join(storageRoot, 'global'), {recursive:true});
        fs.mkdirSync(path.join(storageRoot, 'logs'), {recursive:true});
        const vscode = {
            version: '1.85.0',
          Position, Range, Selection, Uri,
          Location: class Location { constructor(uri, range) { this.uri=uri; this.range=range; } },
          WorkspaceEdit,
          CompletionItem: class CompletionItem { constructor(label, kind) { this.label=label; this.kind=kind; } },
          CompletionList: class CompletionList { constructor(items=[], isIncomplete=false) { this.items=items; this.isIncomplete=isIncomplete; } },
          CompletionItemKind: { Text:0, Method:1, Function:2, Constructor:3, Field:4, Variable:5, Class:6, Interface:7, Module:8, Property:9, Unit:10, Value:11, Enum:12, Keyword:13, Snippet:14, Color:15, File:16, Reference:17, Folder:18, EnumMember:19, Constant:20, Struct:21, Event:22, Operator:23, TypeParameter:24 },
          SnippetString: class SnippetString { constructor(value='') { this.value=value; } appendText(value) { this.value+=String(value).replace(/[\\$}]/g,'\\$&'); return this; } appendPlaceholder(value) { this.value+='${1:'+value+'}'; return this; } toString() { return this.value; } },
          Hover: class Hover { constructor(contents, range) { this.contents=contents; this.range=range; } },
          TextEdit: class TextEdit { constructor(range, newText) { this.range=range; this.newText=newText; } static replace(range,text) { return new TextEdit(range,text); } static insert(position,text) { return new TextEdit(new Range(position,0,position,0),text); } static delete(range) { return new TextEdit(range,''); } },
          CodeAction: class CodeAction { constructor(title, kind) { this.title=title; this.kind=kind; } },
          CodeActionKind: { Empty:'', QuickFix:'quickfix', Refactor:'refactor', RefactorExtract:'refactor.extract', RefactorInline:'refactor.inline', RefactorRewrite:'refactor.rewrite', Source:'source', SourceOrganizeImports:'source.organizeImports' },
          CancellationToken: { None:{isCancellationRequested:false,onCancellationRequested:()=>disposable()} },
          MarkdownString: class MarkdownString { constructor(value='') { this.value=value; } appendText(value) { this.value+=value; return this; } appendMarkdown(value) { this.value+=value; return this; } },
          ThemeColor: class ThemeColor { constructor(id) { this.id=id; } },
          EventEmitter: class EventEmitter { constructor() { this.listeners=[]; this.event=listener => { this.listeners.push(listener); return disposable(); }; } fire(value) { for (const listener of this.listeners) listener(value); } dispose() { this.listeners=[]; } },
          Disposable: class Disposable { constructor(fn) { this.fn=fn; } dispose() { if (this.fn) this.fn(); } static from(...items) { return { dispose: () => items.forEach(x => x && x.dispose && x.dispose()) }; } },
          ExtensionMode: { Production: 1, Development: 2, Test: 3 },
          commands: {
            registerCommand: register,
            registerTextEditorCommand: (id, callback) => register(id, (...args) => {
              if (!activeEditor) return undefined;
              const edits=[];
              const edit={replace:(range,text)=>edits.push({range,text}),insert:(position,text)=>edits.push({range:new Range(position.line,position.character,position.line,position.character),text}),delete:range=>edits.push({range,text:''}),setEndOfLine:()=>{}};
              const value=callback(activeEditor,edit,...args);
              if (edits.length) return activeEditor.edit(builder=>edits.forEach(item=>builder.replace(item.range,item.text))).then(()=>value);
              return value;
            }),
            executeCommand: async (id, ...args) => {
              if(id==='vscode.open' && args[0]) return vscode.window.showTextDocument(args[0]);
              if(id==='vscode.executeCommand' && args[0]) return vscode.commands.executeCommand(args[0],...args.slice(1));
              const fn=registered.get(id); return fn ? await fn(...args) : undefined;
            },
            getCommands: async () => [...registered.keys()]
          },
          window: {
            showInformationMessage: async (text, ...items) => await message(text, items, 'info'),
            showWarningMessage: async (text, ...items) => await message(text, items, 'warning'),
            showErrorMessage: async (text, ...items) => await message(text, items, 'error'),
            showInputBox: async options => await askUi('input', options && options.prompt || options && options.title || 'Extension meminta input', []),
            showQuickPick: async (items, options) => {
              const values=Array.isArray(items)?items.map(item=>typeof item==='string'?item:(item.label||String(item))):[];
              const selected=await askUi('quickPick', options && options.placeHolder || 'Pilih item', values);
              return selected===undefined?undefined:items[values.indexOf(selected)];
            },
            createOutputChannel: outputChannel,
            withProgress: async (_options, task) => task({ report:()=>{} }, {onCancellationRequested:()=>disposable()}),
            get activeTextEditor() { return activeEditor; },
            get visibleTextEditors() { return activeEditor ? [activeEditor] : []; },
            onDidChangeActiveTextEditor: () => disposable(),
            onDidChangeTextEditorSelection: () => disposable(),
            createStatusBarItem: (alignment, priority) => ({text:'',tooltip:'',command:undefined,alignment,priority,show(){notify('info',this.text)},hide(){},dispose(){}}),
            showTextDocument: async document => {
              const doc=typeof document==='string'||document instanceof Uri?await textDocument(document):document;
              await askUi('openDocument','',[],doc && doc.uri && doc.uri.fsPath);
              activeEditor={document:doc, selection:new Selection(0,0,0,0), selections:[new Selection(0,0,0,0)], viewColumn:1,
                revealRange:()=>{}, edit:async builder=>{const changes=[];const b={replace:(range,text)=>changes.push({range,text}), insert:(position,text)=>changes.push({range:new Range(position.line,position.character,position.line,position.character),text}), delete:range=>changes.push({range,text:''})};builder(b);if(!changes.length)return true;let content=doc.getText();const offset=pos=>doc.offsetAt(pos);for(const change of changes.sort((a,b)=>offset(b.range.start)-offset(a.range.start)))content=content.slice(0,offset(change.range.start))+change.text+content.slice(offset(change.range.end));doc._setText(content);await vscode.workspace.fs.writeFile(doc.uri,Buffer.from(content,'utf8'));return true;}};
              return activeEditor;
            }
          },
          workspace: {
            name: path.basename(workspaceRoot), rootPath: workspaceRoot,
            workspaceFolders: [{ uri: Uri.file(workspaceRoot), name: path.basename(workspaceRoot), index: 0 }],
            isTrusted:true, textDocuments:[],
            fs: {
              stat:async uri=>{const s=await fs.promises.stat(insideWorkspace(uri));return {type:fileType(s),ctime:s.ctimeMs,mtime:s.mtimeMs,size:s.size};},
              readFile:async uri=>new Uint8Array(await fs.promises.readFile(insideWorkspace(uri))),
              writeFile:async (uri,data)=>{const p=insideWorkspace(uri);await fs.promises.mkdir(path.dirname(p),{recursive:true});await fs.promises.writeFile(p,Buffer.from(data));await askUi('fileChanged','',[],p);},
              readDirectory:async uri=>Promise.all((await fs.promises.readdir(insideWorkspace(uri),{withFileTypes:true})).map(async e=>[e.name,e.isDirectory()?2:e.isFile()?1:0])),
              createDirectory:async uri=>{const p=insideWorkspace(uri);await fs.promises.mkdir(p,{recursive:true});await askUi('refreshWorkspace','',[],p);},
              delete:async (uri,options={})=>{const p=insideWorkspace(uri);await fs.promises.rm(p,{recursive:!!options.recursive,force:true});await askUi('refreshWorkspace','',[],p);},
              rename:async (from,to,options={})=>{const a=insideWorkspace(from),b=insideWorkspace(to);if(!options.overwrite&&fs.existsSync(b))throw new Error('Tujuan sudah ada.');await fs.promises.mkdir(path.dirname(b),{recursive:true});await fs.promises.rename(a,b);await askUi('refreshWorkspace','',[],b);},
              copy:async (from,to,options={})=>{const a=insideWorkspace(from),b=insideWorkspace(to);await fs.promises.mkdir(path.dirname(b),{recursive:true});await fs.promises.cp(a,b,{recursive:true,force:!!options.overwrite});await askUi('refreshWorkspace','',[],b);}
            },
            openTextDocument:async value=>textDocument(value),
            asRelativePath:value=>path.relative(workspaceRoot,asUri(value).fsPath),
            getWorkspaceFolder:uri=>({uri:Uri.file(workspaceRoot),name:path.basename(workspaceRoot),index:0}),
            getConfiguration: section => {
              const prefix=section ? String(section)+'.' : '';
              return { get:(key, fallback) => configs.has(prefix+key) ? configs.get(prefix+key) : fallback,
                has:key => configs.has(prefix+key), inspect:key => ({key, defaultValue:undefined, globalValue:configs.get(prefix+key), workspaceValue:undefined}),
                update:async(key,value) => { configs.set(prefix+key,value); }, keys:() => [...configs.keys()].filter(key=>key.startsWith(prefix)).map(key=>key.slice(prefix.length)) };
            },
            onDidOpenTextDocument: unsupported('workspace.onDidOpenTextDocument'), onDidCloseTextDocument: unsupported('workspace.onDidCloseTextDocument'),
            onDidSaveTextDocument: unsupported('workspace.onDidSaveTextDocument'), onDidChangeTextDocument: unsupported('workspace.onDidChangeTextDocument'),
            onDidChangeConfiguration: unsupported('workspace.onDidChangeConfiguration'), onDidChangeWorkspaceFolders: unsupported('workspace.onDidChangeWorkspaceFolders')
          },
          env: { appName:'Depup Code', appHost:'desktop', language:process.env.LANG || 'en', uriScheme:'depup', openExternal:async()=>false },
          languages: {
            getLanguages: async()=>['plaintext','javascript','typescript','python','csharp','json','html','css','markdown'],
            match:(selector,document)=>selectorMatches(selector,document && document.languageId,document && document.uri && document.uri.fsPath)?10:0,
            registerCompletionItemProvider:(selector,provider,...triggerCharacters)=>registerLanguageProvider('completion',selector,provider,{triggerCharacters}),
            registerHoverProvider:(selector,provider)=>registerLanguageProvider('hover',selector,provider),
            registerDefinitionProvider:(selector,provider)=>registerLanguageProvider('definition',selector,provider),
            registerReferenceProvider:(selector,provider)=>registerLanguageProvider('references',selector,provider),
            registerRenameProvider:(selector,provider)=>registerLanguageProvider('rename',selector,provider),
            registerCodeActionsProvider:(selector,provider,metadata={})=>registerLanguageProvider('codeActions',selector,provider,metadata),
            registerDocumentFormattingEditProvider:(selector,provider)=>registerLanguageProvider('formatting',selector,provider),
            registerDocumentSymbolProvider:(selector,provider)=>registerLanguageProvider('symbols',selector,provider)
          },
          extensions: { all:[], getExtension:id => id===extensionId ? {id, isActive:true, packageJSON:manifest, exports:{}, activate:async()=>({})} : undefined }
        };
        const originalLoad = Module._load;
        Module._load = function(request, parent, isMain) { return request === 'vscode' ? vscode : originalLoad.call(this, request, parent, isMain); };
        const context = {
          subscriptions: disposables, extensionPath: extRoot, extensionUri: Uri.file(extRoot),
          storagePath: storageRoot, globalStoragePath:path.join(storageRoot,'global'),
          logPath:path.join(storageRoot,'logs'), extensionMode:2, extension:{id:extensionId, packageJSON:manifest},
          globalState:{get:(key,fallback)=>fallback, update:async()=>{}, keys:()=>[]},
          workspaceState:{get:(key,fallback)=>fallback, update:async()=>{}, keys:()=>[]},
          asAbsolutePath:relative=>path.resolve(extRoot,relative)
        };
        let extension;
        let active = false;
        const respond = (id, ok, value, error) => send({type:'result', id, ok, value: value === undefined ? null : value, error});
        (async () => {
          try {
            const entry = path.resolve(extRoot, manifest.main);
            if (!entry.startsWith(extRoot + path.sep) && entry !== extRoot) throw new Error('Entry point keluar dari folder extension.');
            extension = require(entry);
            if (typeof extension.activate === 'function') await extension.activate(context);
            active = true;
            send({type:'ready', commands:commandList.filter(item => registered.has(item.command))});
          } catch (error) {
            send({type:'fatal', error:error && (error.stack || error.message) || String(error)});
            process.exitCode = 1;
          }
        })();
        const input = readline.createInterface({input:process.stdin, crlfDelay:Infinity});
        input.on('line', async line => {
          if (line.length > 1000000) return;
          let request;
          try { request=JSON.parse(line); } catch { return; }
          if (request.type === 'execute') {
            try {
              if (!active) throw new Error('Extension belum aktif.');
              const fn=registered.get(request.command);
              if (!fn) throw new Error('Command belum didaftarkan: ' + request.command);
              const value=await fn(...(Array.isArray(request.args) ? request.args : []));
              respond(request.id,true,value);
            } catch(error) { respond(request.id,false,null,error && (error.stack || error.message) || String(error)); }
          } else if (request.type === 'provide') {
            try {
              if (!active) throw new Error('Extension belum aktif.');
              const document=snapshotDocument(request.document || {});
              const position=new Position(request.position && request.position.line || 0,request.position && request.position.character || 0);
              const token={isCancellationRequested:false,onCancellationRequested:()=>disposable()};
              const values=[];
              for(const entry of languageProviders.values()){
                if(entry.kind!==request.provider||!selectorMatches(entry.selector,document.languageId,document.fileName)) continue;
                const provider=entry.provider;
                let value;
                if(request.provider==='completion'&&provider.provideCompletionItems) value=await provider.provideCompletionItems(document,position,token,{triggerKind:1,triggerCharacter:undefined});
                else if(request.provider==='hover'&&provider.provideHover) value=await provider.provideHover(document,position,token);
                else if(request.provider==='definition'&&provider.provideDefinition) value=await provider.provideDefinition(document,position,token);
                else if(request.provider==='references'&&provider.provideReferences) value=await provider.provideReferences(document,position,request.context||{includeDeclaration:true},token);
                else if(request.provider==='rename'&&provider.provideRenameEdits) value=await provider.provideRenameEdits(document,position,String(request.context&&request.context.newName||''),token);
                else if(request.provider==='codeActions'&&provider.provideCodeActions){const r=request.range||{};const range=new Range(r.start&&r.start.line||0,r.start&&r.start.character||0,r.end&&r.end.line||0,r.end&&r.end.character||0);value=await provider.provideCodeActions(document,range,request.context||{diagnostics:[]},token);}
                else if(request.provider==='formatting'&&provider.provideDocumentFormattingEdits) value=await provider.provideDocumentFormattingEdits(document,request.context||{},token);
                else if(request.provider==='symbols'&&provider.provideDocumentSymbols) value=await provider.provideDocumentSymbols(document,token);
                if(value==null) continue;
                if(request.provider==='rename') value=serializeWorkspaceEdit(value);
                else if(request.provider==='codeActions') {
                  const actions=Array.isArray(value)?value:[value];
                  value=actions.map(action=>action&&typeof action==='object'?{...action,edit:serializeWorkspaceEdit(action.edit)}:action);
                }
                if(request.provider==='completion'&&Array.isArray(value.items)) values.push(...value.items);
                else if(Array.isArray(value)) values.push(...value);
                else values.push(value);
              }
              respond(request.id,true,values.slice(0,200));
            } catch(error) { respond(request.id,false,null,error && (error.stack || error.message) || String(error)); }
          } else if (request.type === 'deactivate') {
            try { if (active && extension && typeof extension.deactivate === 'function') await extension.deactivate(); respond(request.id,true,null); }
            catch(error) { respond(request.id,false,null,error && (error.stack || error.message) || String(error)); }
            finally { input.close(); process.exit(0); }
          } else if (request.type === 'uiResult') {
            const resolve=pendingUi.get(String(request.id));
            if(resolve){pendingUi.delete(String(request.id));resolve(request.value);}
          }
        });
        """;
}
