using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record WorkspaceTaskDefinition(string Label, string Type, string Command, IReadOnlyList<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment, IReadOnlyList<string> Dependencies,
    bool ParallelDependencies, IReadOnlyList<WorkspaceTaskProblemPattern> ProblemPatterns);
public sealed record WorkspaceTaskProblemPattern(Regex Expression, int FileGroup, int LineGroup, int ColumnGroup,
    int SeverityGroup, int CodeGroup, int MessageGroup, string FileBaseDirectory);
public sealed record WorkspaceTaskDiagnostic(string FilePath, int Line, int Column, string Severity, string Message, string WorkingDirectory);

/// <summary>Runs simple VS Code tasks.json shell/process tasks after an explicit user approval.</summary>
public sealed class WorkspaceTaskService
{
    private const int MaxConfigBytes = 128 * 1024;
    private const int MaxTasks = 100;
    private readonly string _root;
    private readonly Dictionary<string, WorkspaceTaskDefinition> _tasks;
    private readonly string? _defaultBuildTask;

    public IReadOnlyList<WorkspaceTaskDefinition> Tasks => _tasks.Values.OrderBy(task => task.Label, StringComparer.OrdinalIgnoreCase).ToArray();
    public string? DefaultBuildTaskLabel => _defaultBuildTask;

    public WorkspaceTaskService(string workspaceRoot)
    {
        _root = Path.GetFullPath(workspaceRoot);
        (_tasks, _defaultBuildTask) = LoadTasks(_root);
    }

    public static string CreateStarterConfig() => new JObject
    {
        ["version"] = "2.0.0",
        ["tasks"] = new JArray
        {
            new JObject
            {
                ["label"] = "Build workspace with .NET",
                ["type"] = "process",
                ["command"] = "dotnet",
                ["args"] = new JArray("build"),
                ["group"] = new JObject { ["kind"] = "build", ["isDefault"] = true },
                ["problemMatcher"] = "$msCompile"
            },
            new JObject
            {
                ["label"] = "Build workspace with npm",
                ["type"] = "shell",
                ["command"] = "npm",
                ["args"] = new JArray("run", "build")
            }
        }
    }.ToString(Formatting.Indented);

    public async Task RunAsync(string requestedLabel, string? activeFile,
        Func<string, string, Task<bool>> approve, Action<string, string> output, CancellationToken cancellationToken = default,
        Action<WorkspaceTaskDiagnostic>? problem = null)
    {
        var plan = BuildPlan(requestedLabel, activeFile);
        var details = new StringBuilder("Task dari .vscode/tasks.json akan dijalankan di Windows AppContainer dengan persetujuan Anda.\n\n");
        foreach (var (task, command, args, cwd, environment) in plan)
        {
            details.AppendLine($"• {task.Label}");
            details.AppendLine("  " + DiagnosticSecretRedactor.Redact(FormatCommand(task.Type, command, args)));
            details.AppendLine("  cwd: " + DiagnosticSecretRedactor.Redact(cwd));
            details.AppendLine("  env keys: " + (environment.Count == 0 ? "(none)" : string.Join(", ", environment.Keys.Take(30))));
        }
        details.Append("\nTask dapat membaca/menulis workspace, membaca toolchain di PATH, memakai internet/private network, dan memakai storage temporary terisolasi. Akses folder lain pada profil Windows dibatasi. Proses task dan turunannya dibatasi ke 64 proses, 1 GB per proses, 3 GB total. Working directory harus berada di dalam workspace. Dependensi parallel berjalan serentak. Environment bawaan yang tampak berisi kredensial dihapus; variabel yang tercantum pada tasks.json tetap tersedia.");
        if (!await approve("workspace task", details.ToString()))
            throw new OperationCanceledException("Pengguna membatalkan workspace task.");

        var plannedByLabel = plan.ToDictionary(item => item.Task.Label, StringComparer.OrdinalIgnoreCase);
        var executions = new ConcurrentDictionary<string, Lazy<Task>>(StringComparer.OrdinalIgnoreCase);
        Task RunPlannedTaskAsync(string label)
        {
            if (!plannedByLabel.TryGetValue(label, out var planned))
                throw new InvalidOperationException($"Task dependensi '{label}' tidak ditemukan dalam rencana eksekusi.");
            return executions.GetOrAdd(label, _ => new Lazy<Task>(
                () => RunPlannedTaskCoreAsync(planned), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        async Task RunPlannedTaskCoreAsync((WorkspaceTaskDefinition Task, string Command, IReadOnlyList<string> Arguments, string Cwd, IReadOnlyDictionary<string, string?> Environment) planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dependencies = planned.Task.Dependencies;
            if (planned.Task.ParallelDependencies)
            {
                await Task.WhenAll(dependencies.Select(RunPlannedTaskAsync));
            }
            else
            {
                foreach (var dependency in dependencies)
                    await RunPlannedTaskAsync(dependency);
            }

            output($"[task] {planned.Task.Label}: {DiagnosticSecretRedactor.Redact(FormatCommand(planned.Task.Type, planned.Command, planned.Arguments))}{Environment.NewLine}", planned.Cwd);
            await RunOneAsync(planned.Task, planned.Command, planned.Arguments, planned.Cwd, planned.Environment, output, problem, cancellationToken);
        }

        await RunPlannedTaskAsync(plan[^1].Task.Label);
    }

    private List<(WorkspaceTaskDefinition Task, string Command, IReadOnlyList<string> Arguments, string Cwd, IReadOnlyDictionary<string, string?> Environment)> BuildPlan(string requestedLabel, string? activeFile)
    {
        var root = requestedLabel.Equals("${defaultBuildTask}", StringComparison.OrdinalIgnoreCase)
            ? _defaultBuildTask
            : requestedLabel;
        if (string.IsNullOrWhiteSpace(root) || !_tasks.ContainsKey(root))
            throw new InvalidOperationException($"Task '{requestedLabel}' tidak ditemukan di .vscode/tasks.json.");
        var output = new List<(WorkspaceTaskDefinition, string, IReadOnlyList<string>, string, IReadOnlyDictionary<string, string?>)>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string label, int depth)
        {
            if (depth > 20) throw new InvalidOperationException("Rantai dependensi task melewati batas 20 tingkat.");
            if (completed.Contains(label)) return;
            if (!visiting.Add(label)) throw new InvalidOperationException("Dependensi task mengandung siklus: " + label);
            if (!_tasks.TryGetValue(label, out var task)) throw new InvalidOperationException($"Task dependensi '{label}' tidak ditemukan.");
            foreach (var dependency in task.Dependencies) Visit(dependency, depth + 1);
            visiting.Remove(label);
            completed.Add(label);
            var command = ExpandVariables(task.Command, _root, activeFile);
            var args = task.Arguments.Select(arg => ExpandVariables(arg, _root, activeFile)).ToArray();
            var cwd = ExpandVariables(task.WorkingDirectory, _root, activeFile);
            if (!Path.IsPathRooted(cwd)) cwd = Path.GetFullPath(Path.Combine(_root, cwd));
            var env = task.Environment.ToDictionary(pair => pair.Key,
                pair => pair.Value is null ? null : ExpandVariables(pair.Value, _root, activeFile), StringComparer.OrdinalIgnoreCase);
            var unresolvedValues = new List<string> { command, cwd };
            unresolvedValues.AddRange(args);
            unresolvedValues.AddRange(env.Values.OfType<string>());
            if (unresolvedValues.Any(value => value.Contains("${", StringComparison.Ordinal)))
                throw new NotSupportedException($"Task '{label}' memakai variable substitution yang belum didukung.");
            output.Add((task, command, args, Path.GetFullPath(cwd), env));
        }
        Visit(root!, 0);
        return output;
    }

    private async Task RunOneAsync(WorkspaceTaskDefinition task, string command, IReadOnlyList<string> args,
        string cwd, IReadOnlyDictionary<string, string?> environment, Action<string, string> output,
        Action<WorkspaceTaskDiagnostic>? problem, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException($"Working directory task tidak ditemukan: {cwd}");
        var startInfo = CreateStartInfo(task.Type, command, args, cwd, environment);
        var environmentValues = startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value ?? "", StringComparer.OrdinalIgnoreCase);
        var readOnlyPaths = new List<string>();
        if (Path.GetDirectoryName(startInfo.FileName) is { Length: > 0 } executableDirectory) readOnlyPaths.Add(executableDirectory);
        if (startInfo.Environment.TryGetValue("PATH", out var pathValue) && pathValue is not null)
            readOnlyPaths.AddRange(pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var sandbox = WindowsExtensionSandbox.StartCommand(startInfo.FileName, startInfo.ArgumentList.ToArray(), _root,
            cwd, $"task:{_root}:{task.Label}", environmentValues, readOnlyPaths, allowInternet: true, allowPrivateNetwork: true);
        var process = sandbox.Process;
        long outputCharacters = 0;
        void HandleOutput(string? line)
        {
            if (line is null) return;
            var safe = DiagnosticSecretRedactor.Redact(TerminalService.SanitizeTerminalOutput(line));
            var current = Interlocked.Add(ref outputCharacters, safe.Length);
            if (current <= 2_000_000) output(safe + Environment.NewLine, cwd);
            if (problem is null || current > 2_000_000) return;
            foreach (var pattern in task.ProblemPatterns)
            {
                try
                {
                    var match = pattern.Expression.Match(safe);
                    if (!match.Success) continue;
                    var file = match.Groups[pattern.FileGroup].Value.Trim().Trim('"', '\'');
                    if (string.IsNullOrWhiteSpace(file)) continue;
                    var fullPath = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(pattern.FileBaseDirectory, file));
                    var relative = Path.GetRelativePath(_root, fullPath);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                        !File.Exists(fullPath)) continue;
                    if (!int.TryParse(match.Groups[pattern.LineGroup].Value, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var lineNumber) || lineNumber < 1) continue;
                    var column = pattern.ColumnGroup > 0 && int.TryParse(match.Groups[pattern.ColumnGroup].Value,
                        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedColumn)
                        ? Math.Max(1, parsedColumn) : 1;
                    var severity = pattern.SeverityGroup > 0 && match.Groups[pattern.SeverityGroup].Value.Equals("warning", StringComparison.OrdinalIgnoreCase)
                        ? "WARN" : "ERROR";
                    var message = match.Groups[pattern.MessageGroup].Value.Trim();
                    if (pattern.CodeGroup > 0 && !string.IsNullOrWhiteSpace(match.Groups[pattern.CodeGroup].Value))
                        message = match.Groups[pattern.CodeGroup].Value.Trim() + ": " + message;
                    if (message.Length > 600) message = message[..600] + "…";
                    problem(new WorkspaceTaskDiagnostic(fullPath, lineNumber, column, severity,
                        DiagnosticSecretRedactor.Redact(message), cwd));
                    break;
                }
                catch (RegexMatchTimeoutException) { break; }
                catch { }
            }
        }
        async Task PumpAsync(Stream stream)
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line) HandleOutput(line);
        }
        var stdout = PumpAsync(sandbox.Output);
        var stderr = PumpAsync(sandbox.Error);
        var exitCode = -1;
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            exitCode = process.ExitCode;
            sandbox.Dispose();
            await Task.WhenAll(stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            sandbox.Dispose();
            throw;
        }
        finally { sandbox.Dispose(); }
        if (exitCode != 0) throw new InvalidOperationException($"Task '{task.Label}' gagal dengan exit code {exitCode}.");
        output($"[task] {task.Label} selesai.{Environment.NewLine}", cwd);
    }

    private ProcessStartInfo CreateStartInfo(string type, string command, IReadOnlyList<string> args, string cwd,
        IReadOnlyDictionary<string, string?> environment)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment.Clear();
        foreach (var name in SafeEnvironmentNames)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var (key, value) in environment)
        {
            if (!WindowsExtensionSandbox.IsValidEnvironmentName(key))
                throw new InvalidDataException("Nama environment variable pada task tidak valid.");
            if (value is null) start.Environment.Remove(key);
            else if (!WindowsExtensionSandbox.IsValidEnvironmentValue(value)) throw new InvalidDataException($"Nilai environment task '{key}' terlalu panjang atau mengandung karakter NUL.");
            else start.Environment[key] = value;
        }

        if (type.Equals("process", StringComparison.OrdinalIgnoreCase))
        {
            var executable = LanguageServerService.ResolveExecutable(_root, command)
                ?? throw new FileNotFoundException($"Executable task tidak ditemukan: {command}");
            if (OperatingSystem.IsWindows() && Path.GetExtension(executable) is var extension &&
                (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                start.ArgumentList.Add("/d");
                start.ArgumentList.Add("/s");
                start.ArgumentList.Add("/c");
                var line = "\"\"" + executable + "\"" + (args.Count == 0 ? "" : " " + string.Join(" ", args.Select(QuoteCommandArgument))) + "\"";
                start.ArgumentList.Add(line);
                return start;
            }
            start.FileName = executable;
            foreach (var arg in args) start.ArgumentList.Add(arg);
            return start;
        }

        if (!type.Equals("shell", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Jenis task '{type}' belum didukung; gunakan shell atau process.");
        var shellCommand = args.Count == 0
            ? command
            : "& '" + command.Replace("'", "''", StringComparison.Ordinal) + "' " + string.Join(" ", args.Select(arg => "'" + arg.Replace("'", "''", StringComparison.Ordinal) + "'"));
        start.FileName = Environment.GetEnvironmentVariable("SystemRoot") is { Length: > 0 } systemRoot
            ? Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
            : "powershell.exe";
        start.RedirectStandardInput = false;
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(shellCommand);
        return start;
    }

    private static (Dictionary<string, WorkspaceTaskDefinition>, string?) LoadTasks(string root)
    {
        var tasks = new Dictionary<string, WorkspaceTaskDefinition>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, ".vscode", "tasks.json");
        string? defaultBuildTask = null;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxConfigBytes) return (tasks, null);
            using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
            var json = JObject.Load(reader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore, LineInfoHandling = LineInfoHandling.Ignore });
            foreach (var item in (json["tasks"] as JArray ?? []).OfType<JObject>().Take(MaxTasks))
            {
                var label = item["label"]?.Value<string>()?.Trim();
                var command = item["command"]?.Value<string>();
                var type = item["type"]?.Value<string>()?.Trim().ToLowerInvariant() ?? "shell";
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(command)) continue;
                var options = item["options"] as JObject;
                var cwd = options?["cwd"]?.Value<string>() ?? "${workspaceFolder}";
                var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var source in new[] { item["options"]?["env"] as JObject, item["env"] as JObject })
                    if (source is not null)
                        foreach (var property in source.Properties()) env[property.Name] = property.Value.Type == JTokenType.Null ? null : property.Value.Value<string>();
                var args = (item["args"] as JArray ?? []).Values<string>().Where(value => value is not null).Take(100).Select(value => value!).ToArray();
                var dependencies = item["dependsOn"] switch
                {
                    JValue value when value.Type == JTokenType.String && !string.IsNullOrWhiteSpace(value.Value<string>()) => new[] { value.Value<string>()! },
                    JArray array => array.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Take(20).Select(value => value!).ToArray(),
                    _ => []
                };
                var problemPatterns = ParseProblemPatterns(item["problemMatcher"], root);
                tasks[label] = new WorkspaceTaskDefinition(label, type, command, args, cwd, env, dependencies,
                    item["dependsOrder"]?.Value<string>()?.Equals("parallel", StringComparison.OrdinalIgnoreCase) == true,
                    problemPatterns);
                var group = item["group"];
                var isBuild = group?.Type == JTokenType.String && group.Value<string>()?.Equals("build", StringComparison.OrdinalIgnoreCase) == true;
                isBuild |= group is JObject groupObject && groupObject["kind"]?.Value<string>()?.Equals("build", StringComparison.OrdinalIgnoreCase) == true && groupObject["isDefault"]?.Value<bool>() == true;
                if (isBuild && (item["group"]?.Type == JTokenType.String || item["group"]?["isDefault"]?.Value<bool>() == true))
                    defaultBuildTask ??= label;
            }
        }
        catch (Exception ex) { Debug.WriteLine("tasks.json error: " + DiagnosticSecretRedactor.Redact(ex.Message)); }
        return (tasks, defaultBuildTask);
    }

    private static IReadOnlyList<WorkspaceTaskProblemPattern> ParseProblemPatterns(JToken? matcherToken, string workspaceRoot)
    {
        var matchers = matcherToken switch
        {
            JObject matcher => new[] { matcher },
            JArray array => array.OfType<JObject>().Take(16).ToArray(),
            _ => []
        };
        var result = new List<WorkspaceTaskProblemPattern>();
        foreach (var matcher in matchers)
        {
            var pattern = matcher["pattern"] switch
            {
                JObject oneLine => oneLine,
                JArray { Count: 1 } oneLineArray => oneLineArray[0] as JObject,
                _ => null
            };
            var configuredPattern = pattern;
            if (configuredPattern is null) continue;
            var expressionText = configuredPattern["regexp"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(expressionText) || expressionText.Length > 4096) continue;
            try
            {
                var expression = new Regex(expressionText, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
                int Group(string name)
                {
                    var token = configuredPattern[name];
                    var value = token is { Type: JTokenType.Integer } ? token.Value<int>() : 0;
                    return value > 0 && expression.GetGroupNumbers().Contains(value) ? value : 0;
                }
                var fileGroup = Group("file");
                var lineGroup = Group("line");
                var messageGroup = Group("message");
                if (fileGroup == 0 || lineGroup == 0 || messageGroup == 0) continue;

                var baseDirectory = Path.GetFullPath(workspaceRoot);
                var fileLocation = matcher["fileLocation"];
                string? basePath = fileLocation?.Type == JTokenType.Array
                    ? fileLocation.ElementAtOrDefault(1)?.Value<string>()
                    : null;
                if (!string.IsNullOrWhiteSpace(basePath))
                {
                    var workspaceName = Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    basePath = basePath.Replace("${workspaceFolder}", workspaceRoot, StringComparison.OrdinalIgnoreCase)
                        .Replace("${workspaceFolderBasename}", workspaceName, StringComparison.OrdinalIgnoreCase);
                    baseDirectory = Path.GetFullPath(Path.IsPathRooted(basePath) ? basePath : Path.Combine(workspaceRoot, basePath));
                    var relative = Path.GetRelativePath(workspaceRoot, baseDirectory);
                    if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                }
                result.Add(new WorkspaceTaskProblemPattern(expression, fileGroup, lineGroup, Group("column"),
                    Group("severity"), Group("code"), messageGroup, baseDirectory));
            }
            catch (ArgumentException) { }
        }
        return result;
    }

    private static readonly string[] SafeEnvironmentNames =
    [
        "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramFiles", "ProgramFiles(x86)",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "OS", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS"
    ];

    private static string ExpandVariables(string value, string root, string? activeFile)
    {
        var workspaceName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        value = value.Replace("${workspaceFolder}", root, StringComparison.OrdinalIgnoreCase)
            .Replace("${workspaceFolderBasename}", workspaceName, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(activeFile) &&
            new[] { "${file}", "${fileDirname}", "${fileBasename}", "${fileBasenameNoExtension}", "${fileExtname}", "${relativeFile}", "${relativeFileDirname}" }
                .Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.GetFullPath(activeFile);
            var relativeFile = Path.GetRelativePath(root, file);
            if (Path.IsPathRooted(relativeFile) || relativeFile == ".." ||
                relativeFile.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDataException("File aktif untuk substitusi task harus berada di dalam workspace.");
            value = value.Replace("${file}", file, StringComparison.OrdinalIgnoreCase)
                .Replace("${fileDirname}", Path.GetDirectoryName(file) ?? root, StringComparison.OrdinalIgnoreCase)
                .Replace("${fileBasename}", Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)
                .Replace("${fileBasenameNoExtension}", Path.GetFileNameWithoutExtension(file), StringComparison.OrdinalIgnoreCase)
                .Replace("${relativeFile}", relativeFile, StringComparison.OrdinalIgnoreCase)
                .Replace("${relativeFileDirname}", Path.GetDirectoryName(relativeFile) ?? ".", StringComparison.OrdinalIgnoreCase)
                .Replace("${fileExtname}", Path.GetExtension(file), StringComparison.OrdinalIgnoreCase);
        }
        value = value.Replace("${userHome}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
            .Replace("${pathSeparator}", Path.DirectorySeparatorChar.ToString(), StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(value, @"\$\{env:([^}]+)\}", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? "");
    }

    private static string FormatCommand(string type, string command, IReadOnlyList<string> args) =>
        type.Equals("process", StringComparison.OrdinalIgnoreCase)
            ? string.Join(" ", new[] { command }.Concat(args).Select(QuoteDisplayArgument))
            : args.Count == 0 ? command : command + " " + string.Join(" ", args.Select(QuoteDisplayArgument));

    private static string QuoteDisplayArgument(string value) => "\"" + DiagnosticSecretRedactor.Redact(value).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string QuoteCommandArgument(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
