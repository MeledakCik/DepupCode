using System.Diagnostics;
using System.ComponentModel;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DepupCode.Models;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed class AiAgentService(SettingsService settings, string? workspaceRoot,
    IReadOnlyList<AiExternalTool>? externalTools = null)
{
    private const int MaxToolRounds = 5;
    private const int MaxFileBytes = 256 * 1024;
    private const int MaxTerminalCommandCharacters = 12_000;
    private const int MaxTerminalOutputCharactersPerStream = 8_000;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly AiPromptService _promptService = new();
    private readonly string _root = string.IsNullOrWhiteSpace(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot);
    private readonly AiWorkspaceExclusionService _exclusions = new(
        string.IsNullOrWhiteSpace(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot));
    private readonly IReadOnlyList<AiExternalTool> _externalTools = externalTools ?? [];
    private AiChatSession? _activeSession;

    public event Action<string, string>? FileWritten;
    public event Action<string>? FileDeleted;

    public async Task<string> SendAsync(AiChatSession session, string mode,
        Func<AiToolRequest, Task<bool>> approveAction,
        Action<string>? toolStarted = null,
        string? customInstructions = null,
        CancellationToken cancellationToken = default)
    {
        _activeSession = session;
        var securityAudit = string.Equals(mode, "SecurityAudit", StringComparison.OrdinalIgnoreCase);
        var activeExternalTools = securityAudit ? [] : _externalTools.Where(tool => tool.IsAvailable()).ToList();
        var configuration = settings.Load();
        var key = settings.GetToken(string.Equals(configuration.AiProvider, "Gemini", StringComparison.OrdinalIgnoreCase) ? "gemini" : "ai");
        var allowChanges = mode is not ("Ask" or "Plan" or "SecurityAudit");
        var skills = new OpenCodeSkillCatalog(_root);
        if (securityAudit && !Directory.Exists(_root))
            throw new InvalidOperationException("Buka folder workspace sebelum menjalankan Security Audit.");
        if (string.Equals(configuration.AiProvider, "Copilot", StringComparison.OrdinalIgnoreCase))
        {
            if (allowChanges && !Directory.Exists(_root))
                throw new InvalidOperationException("Buka folder workspace sebelum menggunakan AI Agent.");
            return await SendWithCopilotAsync(session, mode, allowChanges, securityAudit, approveAction, toolStarted,
                customInstructions, configuration.AiLanguage, skills, activeExternalTools, cancellationToken);
        }

        var endpoint = BuildEndpoint(configuration.AiApiBaseUrl);
        var endpointUri = new Uri(endpoint);
        var localEndpoint = endpointUri.IsLoopback;
        if (string.IsNullOrWhiteSpace(key) && !localEndpoint)
            throw new InvalidOperationException("Tambahkan API key AI lewat tombol ⚙ di panel AI Agent terlebih dahulu.");
        if (allowChanges && !Directory.Exists(_root))
            throw new InvalidOperationException("Buka folder workspace sebelum menggunakan AI Agent.");

        var model = string.IsNullOrWhiteSpace(configuration.AiModel) ? "gpt-4.1-mini" : configuration.AiModel.Trim();
        var systemPrompt = _promptService.CreateSystemPrompt(mode, customInstructions, _root,
            configuration.AiLanguage, skills);
        var conversation = _promptService.BuildConversation(session, systemPrompt);
        if (securityAudit)
            conversation.Add(new JObject
            {
                ["role"] = "user",
                ["content"] = "Local offline heuristic scan report (untrusted evidence; verify each candidate against source):\n" + SecurityAuditService.Scan(_root)
            });
        await using var mcpTools = new McpWorkspaceService();
        if (!securityAudit && Directory.Exists(_root))
            await mcpTools.ConnectAsync(_root, toolStarted,
                server => approveAction(CreateMcpApprovalRequest(server)), cancellationToken);
        var userPrompt = session.Turns.LastOrDefault(turn => turn.Role == "user")?.Content ?? "";
        var selectedMcpTools = mcpTools.SelectTools(userPrompt, allowChanges);

        for (var round = 0; round <= MaxToolRounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestBody = new JObject
            {
                ["model"] = model,
                ["messages"] = conversation
            };
            if (string.Equals(configuration.AiProvider, "Gemini", StringComparison.OrdinalIgnoreCase))
                requestBody["reasoning_effort"] = "low";
            requestBody["tools"] = CreateTools(allowChanges, selectedMcpTools, activeExternalTools);

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(requestBody.ToString(Formatting.None), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail;
                try { detail = JObject.Parse(responseText)["error"]?["message"]?.ToString() ?? response.ReasonPhrase ?? "Unknown error"; }
                catch { detail = response.ReasonPhrase ?? "Unknown error"; }
                throw new InvalidOperationException($"AI API ({(int)response.StatusCode}): {detail}");
            }

            var responseObject = JObject.Parse(responseText);
            TrackUsage(session, responseObject["usage"] as JObject);
            var result = responseObject["choices"]?.FirstOrDefault()?["message"] as JObject
                ?? throw new InvalidOperationException("Respons AI tidak berisi pesan yang bisa dibaca.");
            var content = result["content"]?.Type == JTokenType.String ? result["content"]!.Value<string>() ?? "" : "";
            var toolCalls = result["tool_calls"] as JArray;
            if (toolCalls is null || toolCalls.Count == 0)
            {
                session.Turns.Add(new AiChatTurn { Role = "assistant", Content = content });
                return content;
            }

            if (round == MaxToolRounds)
                throw new InvalidOperationException("Agent mencapai batas 5 putaran tindakan. Kirim pesan lagi untuk melanjutkan.");

            conversation.Add(new JObject
            {
                ["role"] = "assistant",
                ["content"] = string.IsNullOrEmpty(content) ? JValue.CreateNull() : content,
                ["tool_calls"] = toolCalls.DeepClone()
            });
            session.Turns.Add(new AiChatTurn
            {
                Role = "assistant",
                Content = content,
                ToolCalls = (JArray)toolCalls.DeepClone()
            });

            var visualArtifacts = new List<AiChatArtifact>();
            foreach (var call in toolCalls.OfType<JObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = call["id"]?.Value<string>() ?? Guid.NewGuid().ToString("N");
                var function = call["function"] as JObject;
                var name = function?["name"]?.Value<string>() ?? "";
                var argsText = function?["arguments"]?.Value<string>() ?? "{}";
                JObject args;
                try { args = JObject.Parse(argsText); }
                catch { args = new JObject(); }
                toolStarted?.Invoke(DescribeTool(name, args));
                string toolResult;
                try
                {
                    if (name == "load_skill")
                        toolResult = skills.Load(args["name"]?.Value<string>() ?? "");
                    else if (mcpTools.TryGetTool(name, out var mcpTool))
                    {
                        if (!allowChanges && !mcpTool.ReadOnly)
                            toolResult = "Tool MCP ini tidak ditandai read-only dan tidak tersedia di mode Ask/Plan.";
                        else
                        {
                            var details = DiagnosticSecretRedactor.Redact(args.ToString(Formatting.None));
                            if (details.Length > 3_000) details = details[..3_000] + "…";
                            var approved = await approveAction(new AiToolRequest(
                                "Jalankan MCP tool", mcpTool.ServerName + "/" + mcpTool.ToolName, details));
                            toolResult = approved
                                ? DiagnosticSecretRedactor.Redact(await mcpTool.Connection.CallToolAsync(mcpTool.ToolName, args, cancellationToken))
                                : "Pengguna menolak tindakan MCP.";
                            if (approved) RecordArtifact("MCP", $"{mcpTool.ServerName}/{mcpTool.ToolName}", toolResult);
                        }
                    }
                    else if (activeExternalTools.FirstOrDefault(tool => tool.Name == name) is { } externalTool)
                    {
                        if (!allowChanges && !externalTool.ReadOnly)
                            toolResult = "Tool ini tidak tersedia di mode Ask/Plan.";
                        else
                        {
                            var artifactCount = session.Artifacts?.Count ?? 0;
                            toolResult = await externalTool.Execute(session, args, approveAction, cancellationToken);
                            if (externalTool.Name == "preview_page" && args["action"]?.Value<string>() == "screenshot")
                                visualArtifacts.AddRange((session.Artifacts ?? []).Skip(artifactCount)
                                    .Where(artifact => artifact.Type == "Screenshot" && !string.IsNullOrWhiteSpace(artifact.FilePath)));
                        }
                    }
                    else
                        toolResult = await ExecuteToolAsync(name, args, allowChanges, approveAction, cancellationToken);
                }
                catch (Exception ex) { toolResult = "Tindakan gagal: " + DiagnosticSecretRedactor.Redact(ex.Message); }
                conversation.Add(new JObject
                {
                    ["role"] = "tool", ["tool_call_id"] = id, ["content"] = toolResult
                });
                session.Turns.Add(new AiChatTurn { Role = "tool", ToolCallId = id, Content = toolResult });
            }
            var latestScreenshot = visualArtifacts.LastOrDefault();
            if (latestScreenshot is not null)
            {
                var dataUrl = ReadArtifactDataUrl(latestScreenshot);
                if (dataUrl is not null)
                {
                    conversation.Add(new JObject
                    {
                        ["role"] = "user",
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = "Screenshot terbaru Preview. Periksa hasil visual dan lanjutkan perbaikan bila ada masalah." },
                            new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = dataUrl } }
                        }
                    });
                }
            }
        }
        throw new InvalidOperationException("AI tidak menyelesaikan respons.");
    }

    public async Task<bool> RestorePreviousVersionAsync(AiChatArtifact artifact,
        Func<AiToolRequest, Task<bool>> approveAction, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifact.WorkspaceRelativePath)) return false;
        var path = ResolvePath(artifact.WorkspaceRelativePath, allowRoot: false);
        EnsureWritableTextFile(path);
        if (artifact.CreatedByAgent)
        {
            if (!File.Exists(path)) return false;
            var createdCurrent = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.IsNullOrWhiteSpace(artifact.ExpectedFileHash) ||
                !string.Equals(ComputeContentHash(createdCurrent), artifact.ExpectedFileHash, StringComparison.Ordinal))
                throw new InvalidOperationException("File berubah sejak dibuat oleh agent. Tidak dihapus agar perubahan baru tidak hilang.");
            var deleteRequest = new AiToolRequest("Hapus file yang dibuat AI", artifact.WorkspaceRelativePath,
                "File ini dibuat oleh AI dan isinya masih cocok dengan versi awal.\n\n" + BuildWritePreview(createdCurrent, "[File akan dihapus]"));
            if (!await approveAction(deleteRequest)) return false;
            File.Delete(path);
            artifact.CreatedByAgent = false;
            artifact.ExpectedFileHash = null;
            artifact.Title = "File buatan AI dihapus";
            artifact.Detail = artifact.WorkspaceRelativePath;
            artifact.CreatedUtc = DateTime.UtcNow;
            FileDeleted?.Invoke(path);
            return true;
        }
        if (string.IsNullOrWhiteSpace(artifact.RollbackBackupPath)) return false;
        var backupRoot = Path.GetFullPath(GetRollbackRoot());
        var backupPath = Path.GetFullPath(artifact.RollbackBackupPath);
        var relativeBackupPath = Path.GetRelativePath(backupRoot, backupPath);
        if (Path.IsPathRooted(relativeBackupPath) || relativeBackupPath == ".." ||
            relativeBackupPath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Snapshot pemulihan berada di luar folder aman DepupCode.");
        if (!File.Exists(backupPath)) return false;

        var previous = SecretStore.Unprotect(await File.ReadAllTextAsync(backupPath, cancellationToken));
        if (Encoding.UTF8.GetByteCount(previous) > MaxFileBytes)
            throw new InvalidOperationException("Snapshot pemulihan melebihi batas ukuran file.");
        var current = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "";
        var request = new AiToolRequest("Pulihkan versi sebelumnya", artifact.WorkspaceRelativePath,
            "Isi file akan dikembalikan ke snapshot terenkripsi yang dibuat sebelum perubahan agent.\n\n" + BuildWritePreview(current, previous));
        if (!await approveAction(request)) return false;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await WriteAtomicallyAsync(path, previous, cancellationToken);
        var restored = await File.ReadAllTextAsync(path, cancellationToken);
        if (!string.Equals(restored, previous, StringComparison.Ordinal))
            throw new InvalidOperationException("Pemulihan selesai, tetapi isi file tidak cocok saat dibaca ulang.");
        FileWritten?.Invoke(path, restored);
        artifact.RollbackBackupPath = null;
        artifact.Title = "File dipulihkan";
        artifact.Detail = artifact.WorkspaceRelativePath;
        artifact.CreatedUtc = DateTime.UtcNow;
        DeleteRollbackSnapshot(backupPath);
        return true;
    }

    private static string BuildEndpoint(string? baseUrl)
    {
        var value = (string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl.Trim()).TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("URL API AI tidak valid. Periksa URL pada pengaturan AI Agent.");
        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? value
            : value + "/chat/completions";
    }

    private async Task<string> SendWithCopilotAsync(AiChatSession chat, string mode, bool allowChanges, bool securityAudit,
        Func<AiToolRequest, Task<bool>> approveAction, Action<string>? toolStarted,
        string? customInstructions, string? language, OpenCodeSkillCatalog skills,
        IReadOnlyList<AiExternalTool> externalTools, CancellationToken cancellationToken)
    {
        var systemPrompt = _promptService.CreateSystemPrompt(mode, customInstructions, _root, language, skills);
        var providerTools = new List<string> { "list_workspace_files", "read_file", "search_workspace", "load_skill" };
        if (allowChanges) providerTools.AddRange(["write_file", "run_terminal"]);
        if (allowChanges) providerTools.AddRange(externalTools.Where(tool => !tool.ReadOnly).Select(tool => tool.Name));
        var workspaceMcp = Directory.Exists(_root) ? McpWorkspaceService.LoadWorkspaceServers(_root) : [];
        if (workspaceMcp.Count > 0)
            toolStarted?.Invoke("Koneksi MCP dinonaktifkan untuk provider Copilot sampai peluncuran server dapat memakai environment terisolasi. Pilih provider API kompatibel untuk MCP.");
        var mcpServers = new Dictionary<string, McpServerConfig>();
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            WorkingDirectory = Directory.Exists(_root) ? _root : AppContext.BaseDirectory,
            UseLoggedInUser = true,
            Environment = new Dictionary<string, string> { ["COPILOT_TELEMETRY_DISABLED"] = "true" }
        });

        var configuration = settings.Load();
        var copilotSession = await client.CreateSessionAsync(new SessionConfig
        {
            Model = string.IsNullOrWhiteSpace(configuration.AiModel) ? "gpt-5" : configuration.AiModel.Trim(),
            WorkingDirectory = Directory.Exists(_root) ? _root : AppContext.BaseDirectory,
            AvailableTools = providerTools,
            EnableSessionStore = false,
            InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
            SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = systemPrompt },
            McpServers = mcpServers,
#pragma warning disable GHCP001 // The SDK exposes its approval result through this evaluation-marked permission API.
            OnPermissionRequest = async (request, _) =>
            {
                if (request.ManagedApprovalRequired is true)
                    return PermissionDecision.Reject("Tindakan diblokir oleh kebijakan approval terkelola.");
                var details = DiagnosticSecretRedactor.Redact(JsonConvert.SerializeObject(request));
                if (details.Length > 3_000) details = details[..3_000] + "…";
                var approved = await approveAction(new AiToolRequest(
                    "Setujui tool Copilot/MCP", request.GetType().Name, details));
                return approved
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject("Pengguna menolak tindakan ini.");
            },
#pragma warning restore GHCP001
            Tools = CreateCopilotTools(chat, allowChanges, approveAction, cancellationToken, skills, externalTools)
        });

        copilotSession.On<SessionEvent>(evt =>
        {
            if (evt is ToolExecutionStartEvent) toolStarted?.Invoke("Copilot sedang memakai tool workspace…");
        });
        try
        {
            using var abort = cancellationToken.Register(() => _ = copilotSession.AbortAsync());
            var prompt = _promptService.BuildCopilotPrompt(chat);
            if (securityAudit)
                prompt += "\n\nLocal offline heuristic scan report (untrusted evidence; verify each candidate against source):\n" + SecurityAuditService.Scan(_root);
            var initialArtifactCount = chat.Artifacts?.Count ?? 0;
            var latestImage = chat.Turns.LastOrDefault(turn => turn.Role == "user")?.EphemeralImageDataUrl;
            var attachments = new List<Attachment>();
            if (TryReadImageDataUrl(latestImage, out var imageData, out var mimeType))
                attachments.Add(new AttachmentBlob { Data = imageData, MimeType = mimeType });
            var response = await copilotSession.SendAndWaitAsync(new MessageOptions
            {
                Prompt = prompt,
                Attachments = attachments
            });
            cancellationToken.ThrowIfCancellationRequested();
            var content = response?.Data.Content ?? "";
            if (string.IsNullOrWhiteSpace(content))
                throw new InvalidOperationException("Copilot tidak mengembalikan jawaban. Pastikan Copilot CLI sudah terpasang dan autentikasi Copilot CLI atau `gh auth login` sudah aktif.");
            var screenshot = (chat.Artifacts ?? []).Skip(initialArtifactCount)
                .LastOrDefault(artifact => artifact.Type == "Screenshot" && !string.IsNullOrWhiteSpace(artifact.FilePath));
            if (screenshot is not null && ReadArtifactDataUrl(screenshot) is { } previewImage &&
                TryReadImageDataUrl(previewImage, out var previewImageData, out var previewMimeType))
            {
                toolStarted?.Invoke("Copilot sedang meninjau screenshot Preview…");
                var visualReview = await copilotSession.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = "Screenshot Preview terbaru dilampirkan. Tinjau tampilan terhadap permintaan pengguna dan perubahan yang sudah dibuat. Jika ada masalah visual yang jelas, perbaiki dengan tool yang tersedia dan verifikasi. Jika sudah sesuai, jelaskan hasil yang terlihat. Jangan mengambil screenshot tambahan kecuali diperlukan untuk verifikasi perbaikan.",
                    Attachments = [new AttachmentBlob { Data = previewImageData, MimeType = previewMimeType }]
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(visualReview?.Data.Content)) content = visualReview.Data.Content;
            }
            chat.Turns.Add(new AiChatTurn { Role = "assistant", Content = content });
            return content;
        }
        finally
        {
            await copilotSession.DisposeAsync();
            try { await client.DeleteSessionAsync(copilotSession.SessionId); } catch { }
        }
    }

    private List<AIFunctionDeclaration> CreateCopilotTools(AiChatSession chat, bool allowChanges,
        Func<AiToolRequest, Task<bool>> approveAction, CancellationToken cancellationToken,
        OpenCodeSkillCatalog skills, IReadOnlyList<AiExternalTool> externalTools)
    {
        var tools = new List<AIFunctionDeclaration>
        {
                CopilotTool.DefineTool(async ([Description("Workspace-relative folder. Leave blank for the workspace root.")] string path) =>
                    await ExecuteToolAsync("list_workspace_files", new JObject { ["path"] = path }, allowChanges, approveAction, cancellationToken),
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "list_workspace_files", Description = "List text files in the active workspace." }),
                CopilotTool.DefineTool(async ([Description("Workspace-relative file path.")] string path,
                    [Description("Include source line numbers for citations.")] bool line_numbers = false) =>
                    await ExecuteToolAsync("read_file", new JObject { ["path"] = path, ["line_numbers"] = line_numbers }, allowChanges, approveAction, cancellationToken),
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "read_file", Description = "Read a text file in the active workspace. Secrets are blocked." }),
                CopilotTool.DefineTool(async ([Description("Text to find.")] string query, [Description("Optional workspace-relative folder.")] string path = "") =>
                    await ExecuteToolAsync("search_workspace", new JObject { ["query"] = query, ["path"] = path }, allowChanges, approveAction, cancellationToken),
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "search_workspace", Description = "Search text in workspace files." }),
                CopilotTool.DefineTool(([Description("Name from the available skill list.")] string name) => Task.FromResult(skills.Load(name)),
                    toolOptions: new CopilotToolOptions { SkipPermission = true },
                    factoryOptions: new AIFunctionFactoryOptions { Name = "load_skill", Description = "Load one OpenCode-compatible SKILL.md instruction on demand." })
        };
        if (allowChanges)
        {
            tools.Add(CopilotTool.DefineTool(async ([Description("Workspace-relative destination path.")] string path, [Description("Complete new file content.")] string content) =>
                await ExecuteToolAsync("write_file", new JObject { ["path"] = path, ["content"] = content }, allowChanges, approveAction, cancellationToken),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions { Name = "write_file", Description = "Create or update a workspace text file after explicit UI approval." }));
            tools.Add(CopilotTool.DefineTool(async ([Description("PowerShell command to run in the workspace.")] string command) =>
                await ExecuteToolAsync("run_terminal", new JObject { ["command"] = command }, allowChanges, approveAction, cancellationToken),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions { Name = "run_terminal", Description = "Run a PowerShell command after explicit UI approval." }));
        }
        if (allowChanges && externalTools.FirstOrDefault(tool => tool.Name == "preview_page") is { } preview)
            tools.Add(CopilotTool.DefineTool(async ([Description("inspect, screenshot, click, fill, or refresh")] string action,
                [Description("Visible element label or css:<selector>")] string target = "",
                [Description("Value to enter when action is fill")] string value = "") =>
                    await preview.Execute(chat, new JObject { ["action"] = action, ["target"] = target, ["value"] = value }, approveAction, cancellationToken),
                toolOptions: new CopilotToolOptions { SkipPermission = true },
                factoryOptions: new AIFunctionFactoryOptions { Name = preview.Name, Description = preview.Description }));
        return tools;
    }

    private static AiToolRequest CreateMcpApprovalRequest(McpServerDefinition server)
    {
        string details;
        if (server.Transport == "stdio")
        {
            var commandLine = DiagnosticSecretRedactor.Redact(
                server.Command + (server.Arguments.Count == 0 ? "" : " " + string.Join(" ", server.Arguments)));
            var environmentNames = server.Environment.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            details = $"Transport: local stdio\nCommand: {commandLine}\nWorking directory: {server.WorkingDirectory}\n" +
                      $"Configured environment variables (values hidden): {string.Join(", ", environmentNames.DefaultIfEmpty("none"))}\n\n" +
                      "Proses berjalan dengan hak akun Windows kamu. Launcher langsung membatasi environment warisan ke variabel runtime dasar dan nilai yang dinyatakan server; secret values tidak ditampilkan.";
        }
        else
        {
            var endpoint = Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
                ? $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}"
                : "URL tidak valid";
            details = $"Transport: Streamable HTTP\nEndpoint: {endpoint}\nConfigured header names (values hidden): {string.Join(", ", server.Headers.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("none"))}\n\n" +
                      "DepupCode akan terhubung dan meminta daftar tool. Setiap pemanggilan tool tetap meminta persetujuan terpisah.";
        }
        return new AiToolRequest(server.Transport == "stdio" ? "Jalankan server MCP lokal" : "Hubungkan server MCP remote",
            server.Name, details.Length > 3_000 ? details[..3_000] + "…" : details);
    }

    private static bool TryReadImageDataUrl(string? dataUrl, out string base64, out string mimeType)
    {
        base64 = "";
        mimeType = "image/png";
        if (string.IsNullOrWhiteSpace(dataUrl) || !dataUrl.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || !dataUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
        mimeType = dataUrl[5..dataUrl.IndexOf(';')];
        base64 = dataUrl[(comma + 1)..];
        return base64.Length > 0;
    }

    private static string? ReadArtifactDataUrl(AiChatArtifact artifact)
    {
        if (string.IsNullOrWhiteSpace(artifact.FilePath) || !File.Exists(artifact.FilePath)) return null;
        try
        {
            BitmapImage bitmap;
            using (var input = File.OpenRead(artifact.FilePath))
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1280;
                bitmap.StreamSource = input;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            static byte[] Encode(BitmapSource source, int quality)
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = quality };
                encoder.Frames.Add(BitmapFrame.Create(source));
                using var output = new MemoryStream();
                encoder.Save(output);
                return output.ToArray();
            }

            var bytes = Encode(bitmap, 68);
            if (bytes.Length > 2 * 1024 * 1024)
            {
                var scaled = new TransformedBitmap(bitmap, new ScaleTransform(.72, .72));
                scaled.Freeze();
                bytes = Encode(scaled, 55);
            }
            if (bytes.Length > 2 * 1024 * 1024) return null;
            return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static JArray CreateTools(bool allowChanges, IEnumerable<McpToolBinding> mcpTools,
        IEnumerable<AiExternalTool> externalTools)
    {
        var tools = JArray.Parse("""
    [
      {"type":"function","function":{"name":"list_workspace_files","description":"Daftar file teks di workspace, termasuk subfolder. Folder build dan dependency diabaikan.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Folder relatif dari workspace; kosong berarti root"}},"required":[]}}},
      {"type":"function","function":{"name":"read_file","description":"Baca file teks workspace. File rahasia dan file besar ditolak; line_numbers memberi nomor baris untuk temuan audit.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Path relatif dari workspace"},"line_numbers":{"type":"boolean","description":"Tambahkan nomor baris pada keluaran"}},"required":["path"]}}},
      {"type":"function","function":{"name":"search_workspace","description":"Cari teks di file teks workspace.","parameters":{"type":"object","properties":{"query":{"type":"string"},"path":{"type":"string","description":"Subfolder relatif opsional"}},"required":["query"]}}},
      {"type":"function","function":{"name":"write_file","description":"Buat atau ubah file teks workspace. Pengguna harus menyetujui penulisan melalui dialog.","parameters":{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}}},
      {"type":"function","function":{"name":"run_terminal","description":"Jalankan perintah PowerShell di root workspace. Pengguna harus menyetujui perintah terlebih dahulu.","parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}},
      {"type":"function","function":{"name":"load_skill","description":"Muat instruksi satu OpenCode-compatible skill yang sesuai kebutuhan. Jangan muat semua skill.","parameters":{"type":"object","properties":{"name":{"type":"string","description":"Nama skill persis dari daftar skill"}},"required":["name"]}}}
    ]
    """);
        if (!allowChanges)
            foreach (var tool in tools.OfType<JObject>().Where(tool =>
                         tool["function"]?["name"]?.Value<string>() is "write_file" or "run_terminal").ToList())
                tools.Remove(tool);
        foreach (var mcpTool in mcpTools.Where(tool => allowChanges || tool.ReadOnly))
        {
            tools.Add(new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = mcpTool.FunctionName,
                    ["description"] = $"{mcpTool.ServerName}: {mcpTool.Description}",
                    ["parameters"] = mcpTool.InputSchema.DeepClone()
                }
            });
        }
        foreach (var externalTool in externalTools.Where(tool => allowChanges || tool.ReadOnly))
        {
            tools.Add(new JObject
            {
                ["type"] = "function",
                ["function"] = new JObject
                {
                    ["name"] = externalTool.Name,
                    ["description"] = externalTool.Description,
                    ["parameters"] = externalTool.Parameters.DeepClone()
                }
            });
        }
        return tools;
    }

    private async Task<string> ExecuteToolAsync(string name, JObject args,
        bool allowChanges, Func<AiToolRequest, Task<bool>> approveAction, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_root)) return "Buka workspace sebelum menggunakan tool untuk membaca kode.";
        if (!allowChanges && (name is "write_file" or "run_terminal"))
            return "Mode read-only tidak dapat mengubah file atau menjalankan perintah.";
        switch (name)
        {
            case "list_workspace_files":
            {
                var dir = ResolvePath(args["path"]?.Value<string>() ?? "", allowRoot: true);
                if (!Directory.Exists(dir)) return "Folder tidak ditemukan.";
                var files = EnumerateTextFiles(dir).Take(120).Select(file => Path.GetRelativePath(_root, file).Replace('\\', '/')).ToList();
                return files.Count == 0 ? "Tidak ada file teks yang ditemukan." : string.Join("\n", files) + (files.Count == 120 ? "\n[Daftar dibatasi 120 file.]" : "");
            }
            case "read_file":
            {
                var path = ResolvePath(args["path"]?.Value<string>() ?? "", allowRoot: false);
                EnsureReadableTextFile(path);
                var content = DiagnosticSecretRedactor.Redact(await File.ReadAllTextAsync(path, cancellationToken));
                if (args["line_numbers"]?.Value<bool>() == true)
                    content = string.Join("\n", content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
                        .Select((line, index) => $"{index + 1,5}: {line}"));
                const int limit = 8_000;
                return content.Length > limit ? content[..limit] + "\n[File dipotong setelah 8.000 karakter.]" : content;
            }
            case "search_workspace":
            {
                var query = args["query"]?.Value<string>() ?? "";
                if (query.Length == 0) return "Kueri pencarian kosong.";
                var start = ResolvePath(args["path"]?.Value<string>() ?? "", allowRoot: true);
                if (!Directory.Exists(start)) return "Folder tidak ditemukan.";
                var matches = new List<string>();
                foreach (var file in EnumerateTextFiles(start))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var lines = await File.ReadAllLinesAsync(file, cancellationToken);
                        for (var i = 0; i < lines.Length; i++)
                            if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                var line = lines[i].Trim();
                                if (line.Length > 200) line = line[..200] + "…";
                                matches.Add(DiagnosticSecretRedactor.Redact($"{Path.GetRelativePath(_root, file).Replace('\\', '/') }:{i + 1}: {line}"));
                                if (matches.Count >= 30) return string.Join("\n", matches) + "\n[Hasil dibatasi 30 baris.]";
                            }
                    }
                    catch { }
                }
                return matches.Count == 0 ? "Tidak ditemukan." : string.Join("\n", matches);
            }
            case "write_file":
            {
                var path = ResolvePath(args["path"]?.Value<string>() ?? "", allowRoot: false);
                EnsureWritableTextFile(path);
                var content = args["content"]?.Value<string>() ?? "";
                if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
                    return "Perubahan ditolak: ukuran file melebihi 256 KB.";
                var exists = File.Exists(path);
                var existing = exists ? await File.ReadAllTextAsync(path, cancellationToken) : "";
                var preview = BuildWritePreview(existing, content);
                var relative = Path.GetRelativePath(_root, path).Replace('\\', '/');
                var request = new AiToolRequest(exists ? "Ubah file" : "Buat file", relative,
                    (exists ? $"Path : {relative}\nUkuran saat ini: {existing.Length:N0} karakter\nUkuran baru: {content.Length:N0} karakter\n\n" : $"File baru: {relative}\n\n") + preview);
                if (!await approveAction(request)) return "Pengguna menolak perubahan file.";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var rollbackPath = exists ? SaveRollbackSnapshot(_activeSession?.Id ?? "session", existing) : null;
                try { await WriteAtomicallyAsync(path, content, cancellationToken); }
                catch
                {
                    DeleteRollbackSnapshot(rollbackPath);
                    throw;
                }
                var writtenContent = await File.ReadAllTextAsync(path, cancellationToken);
                if (!string.Equals(writtenContent, content, StringComparison.Ordinal))
                {
                    RecordArtifact("File", "Pemeriksaan tulis tidak cocok", relative,
                        rollbackBackupPath: rollbackPath, workspaceRelativePath: relative,
                        createdByAgent: !exists, expectedFileHash: exists ? null : ComputeContentHash(content));
                    return $"File {relative} sudah ditulis, tetapi pemeriksaan baca ulang menemukan isi berbeda. Periksa ulang sebelum melanjutkan.";
                }
                FileWritten?.Invoke(path, content);
                RecordArtifact("File", exists ? "File diperbarui" : "File dibuat", relative,
                    rollbackBackupPath: rollbackPath, workspaceRelativePath: relative,
                    createdByAgent: !exists, expectedFileHash: exists ? null : ComputeContentHash(content));
                return $"File {relative} berhasil {(exists ? "diperbarui" : "dibuat")} setelah persetujuan pengguna; isi sudah dibaca ulang dan cocok.";
            }
            case "run_terminal":
            {
                var command = args["command"]?.Value<string>()?.Trim() ?? "";
                if (command.Length == 0) return "Perintah kosong.";
                if (command.Length > MaxTerminalCommandCharacters)
                    return $"Perintah terlalu panjang. Batasnya {MaxTerminalCommandCharacters:N0} karakter.";
                var safeCommand = DiagnosticSecretRedactor.Redact(command);
                var approvalDetails = "Perintah berjalan dengan hak akun Windows saat ini dan belum diisolasi oleh sandbox. " +
                                      "Perintah dapat mengakses jaringan dan file di luar workspace. Variabel environment yang namanya menunjukkan kredensial akan dihapus dari proses anak. " +
                                      "Keluaran dikirim ke provider AI aktif setelah penyamaran pola secret umum; secret yang tidak dikenali masih mungkin lolos.\n\n" + safeCommand;
                if (!await approveAction(new AiToolRequest("Jalankan PowerShell (tanpa sandbox)", _root, approvalDetails)))
                    return "Pengguna menolak menjalankan perintah.";
                var startInfo = new ProcessStartInfo("powershell.exe")
                {
                    WorkingDirectory = _root,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                var credentialEnvironmentNames = startInfo.Environment.Keys
                    .Where(IsCredentialEnvironmentVariable)
                    .ToArray();
                foreach (var environmentName in credentialEnvironmentNames) startInfo.Environment.Remove(environmentName);
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(command);
                using var process = new Process { StartInfo = startInfo };
                process.Start();
                var stdout = ReadBoundedOutputAsync(process.StandardOutput, MaxTerminalOutputCharactersPerStream);
                var stderr = ReadBoundedOutputAsync(process.StandardError, MaxTerminalOutputCharactersPerStream);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(90));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException)
                {
                    try { process.Kill(true); } catch { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    return "Perintah dihentikan karena melewati batas 90 detik.";
                }
                var output = (await stdout) + (await stderr);
                output = DiagnosticSecretRedactor.Redact(output);
                if (output.Length > 8_000) output = output[..8_000] + "\n[Output dipotong.]";
                RecordArtifact("Verification", $"Exit {process.ExitCode}: {command}", DiagnosticSecretRedactor.Redact(output));
                return $"Exit code: {process.ExitCode}\n{output}";
            }
            default:
                return "Tool tidak dikenal.";
        }
    }

    private string ResolvePath(string relative, bool allowRoot)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, relative));
        var relativePath = Path.GetRelativePath(_root, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Path harus berada di dalam workspace aktif.");
        if (!allowRoot && relativePath == ".") throw new InvalidOperationException("Pilih file di dalam workspace.");
        if (IsSecretPath(relativePath)) throw new InvalidOperationException("Akses ke file rahasia diblokir.");
        var current = _root;
        foreach (var part in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Akses melalui symbolic link diblokir.");
        }
        return fullPath;
    }

    private IEnumerable<string> EnumerateTextFiles(string startDirectory)
    {
        var pending = new Stack<string>();
        pending.Push(startDirectory);
        var visited = 0;
        while (pending.Count > 0 && visited < 3000)
        {
            var directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch { continue; }
            foreach (var entry in entries)
            {
                visited++;
                var includeFile = false;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(entry)) &&
                            !IsSecretPath(Path.GetRelativePath(_root, entry))) pending.Push(entry);
                    }
                    else if (!IsSecretPath(Path.GetRelativePath(_root, entry)) && IsTextExtension(Path.GetExtension(entry)) &&
                             new FileInfo(entry).Length <= MaxFileBytes)
                        includeFile = true;
                }
                catch { }
                if (includeFile) yield return entry;
            }
        }
    }

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "coverage", ".next", ".nuxt", "vendor" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".csproj", ".sln", ".json", ".js", ".jsx", ".ts", ".tsx", ".html", ".css", ".scss", ".sass", ".less", ".md", ".txt", ".xml", ".yaml", ".yml", ".toml", ".py", ".go", ".rs", ".java", ".kt", ".php", ".rb", ".sh", ".ps1", ".bat", ".sql", ".svg", ".gitignore", ".editorconfig", ".config", ".props", ".targets", ".razor", ".vue", ".svelte", ".astro", ".ex", ".exs", ".dart", ".swift", ".c", ".h", ".cpp", ".hpp" };

    private static bool IsTextExtension(string extension) => TextExtensions.Contains(extension) || extension.Length == 0;

    private static bool IsCredentialEnvironmentVariable(string name)
    {
        var normalized = name.Replace('-', '_').Replace('.', '_').ToUpperInvariant();
        return normalized.Contains("TOKEN", StringComparison.Ordinal) ||
               normalized.Contains("SECRET", StringComparison.Ordinal) ||
               normalized.Contains("PASSWORD", StringComparison.Ordinal) ||
               normalized.Contains("CREDENTIAL", StringComparison.Ordinal) ||
               normalized.Contains("AUTHORIZATION", StringComparison.Ordinal) ||
               normalized.Contains("API_KEY", StringComparison.Ordinal) ||
               normalized.Contains("ACCESS_KEY", StringComparison.Ordinal) ||
               normalized.Contains("PRIVATE_KEY", StringComparison.Ordinal);
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, int maxCharacters)
    {
        var output = new StringBuilder(Math.Min(maxCharacters, 4_096));
        var buffer = new char[4_096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            var remaining = maxCharacters - output.Length;
            var keep = Math.Min(read, remaining);
            if (keep > 0) output.Append(buffer, 0, keep);
            if (keep < read) truncated = true;
        }
        if (truncated) output.Append("\n[Output channel truncated.] ");
        return output.ToString();
    }

    private bool IsSecretPath(string path)
    {
        var relative = Path.IsPathRooted(path) ? Path.GetRelativePath(_root, path) : path;
        return _exclusions.IsExcluded(relative);
    }

    private void EnsureReadableTextFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File tidak ditemukan.");
        if (IsSecretPath(path)) throw new InvalidOperationException("Akses ke file rahasia diblokir.");
        if (!IsTextExtension(Path.GetExtension(path))) throw new InvalidOperationException("File biner atau tipe file ini tidak dapat dibaca agent.");
        if (new FileInfo(path).Length > MaxFileBytes) throw new InvalidOperationException("Ukuran file melebihi 256 KB.");
    }

    private void EnsureWritableTextFile(string path)
    {
        if (IsSecretPath(path)) throw new InvalidOperationException("Penulisan ke file rahasia diblokir.");
        if (!IsTextExtension(Path.GetExtension(path))) throw new InvalidOperationException("Agent hanya dapat menulis file teks.");
        if (File.Exists(path) && new FileInfo(path).Length > MaxFileBytes) throw new InvalidOperationException("File melebihi batas 256 KB.");
    }

    private static string BuildWritePreview(string current, string proposed)
    {
        var before = current.Length > 2200 ? current[..2200] + "\n…" : current;
        var after = proposed.Length > 2200 ? proposed[..2200] + "\n…" : proposed;
        return current.Length == 0
            ? "Isi file yang akan dibuat:\n\n" + after
            : "Isi saat ini:\n" + before + "\n\nIsi baru:\n" + after;
    }

    private static string ComputeContentHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string GetRollbackRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupCode", "rollback");

    private static string SaveRollbackSnapshot(string sessionId, string content)
    {
        var root = GetRollbackRoot();
        var safeSessionId = string.Concat(sessionId.Where(char.IsLetterOrDigit).Take(64));
        if (safeSessionId.Length == 0) safeSessionId = "session";
        var folder = Path.Combine(root, safeSessionId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".dpapi");
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, SecretStore.Protect(content), new UTF8Encoding(false));
            File.Move(temp, path);
            TrimRollbackSnapshots(root, maxFiles: 120);
            return path;
        }
        finally { DeleteRollbackSnapshot(temp); }
    }

    private static void TrimRollbackSnapshots(string root, int maxFiles)
    {
        try
        {
            if (!Directory.Exists(root)) return;
            var oldFiles = Directory.EnumerateFiles(root, "*.dpapi", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path)).OrderBy(info => info.LastWriteTimeUtc).ToList();
            foreach (var old in oldFiles.Take(Math.Max(0, oldFiles.Count - maxFiles)))
                DeleteRollbackSnapshot(old.FullName);
        }
        catch { }
    }

    private static void DeleteRollbackSnapshot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Folder tujuan file tidak valid.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally { DeleteRollbackSnapshot(temp); }
    }

    private static void TrackUsage(AiChatSession session, JObject? usage)
    {
        if (usage is null) return;
        var input = Math.Max(0, (long?)usage["prompt_tokens"] ?? (long?)usage["input_tokens"] ?? 0);
        var output = Math.Max(0, (long?)usage["completion_tokens"] ?? (long?)usage["output_tokens"] ?? 0);
        session.InputTokens = session.InputTokens > long.MaxValue - input ? long.MaxValue : session.InputTokens + input;
        session.OutputTokens = session.OutputTokens > long.MaxValue - output ? long.MaxValue : session.OutputTokens + output;
    }

    private void RecordArtifact(string type, string title, string detail, string? filePath = null,
        string? rollbackBackupPath = null, string? workspaceRelativePath = null,
        bool createdByAgent = false, string? expectedFileHash = null)
    {
        if (_activeSession is null) return;
        _activeSession.Artifacts ??= [];
        _activeSession.Artifacts.Add(new AiChatArtifact
        {
            Type = type,
            Title = DiagnosticSecretRedactor.Redact(title),
            Detail = DiagnosticSecretRedactor.Redact(detail.Length > 900 ? detail[..900] + "…" : detail),
            FilePath = filePath,
            RollbackBackupPath = rollbackBackupPath,
            WorkspaceRelativePath = workspaceRelativePath,
            CreatedByAgent = createdByAgent,
            ExpectedFileHash = expectedFileHash,
            CreatedUtc = DateTime.UtcNow
        });
        if (_activeSession.Artifacts.Count > 80)
        {
            DeleteRollbackSnapshot(_activeSession.Artifacts[0].RollbackBackupPath);
            _activeSession.Artifacts.RemoveAt(0);
        }
    }

    private static string DescribeTool(string name, JObject args) => name switch
    {
        "list_workspace_files" => "Memeriksa daftar file workspace…",
        "read_file" => "Membaca " + (args["path"]?.Value<string>() ?? "file") + "…",
        "search_workspace" => "Mencari “" + (args["query"]?.Value<string>() ?? "") + "”…",
        "write_file" => "Menyiapkan perubahan pada " + (args["path"]?.Value<string>() ?? "file") + "…",
        "run_terminal" => "Menyiapkan perintah terminal…",
        "preview_page" => "Memeriksa atau mengoperasikan Preview…",
        _ => "Menjalankan tool…"
    };
}
