using System.Text;
using System.Text.RegularExpressions;

namespace DepupCode.Services;

/// <summary>Bounded, offline security heuristics used as evidence for the read-only AI audit mode.</summary>
public static class SecurityAuditService
{
    private const int MaxFiles = 2_000;
    private const int MaxFileBytes = 256 * 1024;
    private const long MaxTotalBytes = 24 * 1024 * 1024;
    private const int MaxFindings = 50;
    private const int MaxReportCharacters = 12_000;

    private sealed record Rule(string Id, string Severity, Regex Pattern, string Summary);

    private static readonly Rule[] Rules =
    [
        new("DEPUP-SEC-001", "High", new Regex(
            @"(?i)\b(?:api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|private[_-]?key)\b\s*[:=]\s*[""'](?<value>[^""'\r\n]{8,})[""']",
            RegexOptions.Compiled), "Possible hard-coded credential; confirm it is not a placeholder or test value."),
        new("DEPUP-SEC-002", "High", new Regex(
            @"(?i)(rejectUnauthorized\s*:\s*false|verify\s*=\s*False|CERT_NONE|ssl[_-]?verify\s*[:=]\s*false)",
            RegexOptions.Compiled), "TLS certificate validation appears disabled."),
        new("DEPUP-SEC-003", "Medium", new Regex(
            @"(?i)(\beval\s*\(|\bnew\s+Function\s*\(|\bInvoke-Expression\b|\bIEX\s+)",
            RegexOptions.Compiled), "Dynamic code evaluation; check whether attacker-controlled input can reach it."),
        new("DEPUP-SEC-004", "Medium", new Regex(
            @"(?i)(\.innerHTML\s*=|dangerouslySetInnerHTML\s*=)", RegexOptions.Compiled),
            "HTML is inserted through an unsafe sink; verify that untrusted data is sanitized."),
        new("DEPUP-SEC-005", "Medium", new Regex(
            @"(?i)(Access-Control-Allow-Origin\s*[:=]\s*[""']?\*|cors\s*\(\s*\{[^}]*origin\s*:\s*[""']?\*)",
            RegexOptions.Compiled), "Wildcard CORS origin; verify that sensitive endpoints do not rely on this policy."),
        new("DEPUP-SEC-006", "Medium", new Regex(
            @"(?i)(child_process\.)?exec(?:Sync)?\s*\([^\r\n]*(\+\s*[A-Za-z_$]|\$\{|\.format\s*\(|f[""'])",
            RegexOptions.Compiled), "Shell command construction may include dynamic input; verify argument separation and validation.")
    ];

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", "packages", "dist", "build", "out", "coverage", ".next", ".nuxt", "vendor" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".cs", ".xaml", ".csproj", ".sln", ".json", ".js", ".jsx", ".ts", ".tsx", ".html", ".css", ".scss", ".xml", ".md", ".txt", ".config", ".yaml", ".yml", ".toml", ".py", ".go", ".rs", ".java", ".kt", ".php", ".rb", ".sh", ".ps1", ".bat", ".sql", ".vue", ".svelte", ".astro", ".ex", ".exs", ".dart", ".swift", ".c", ".h", ".cpp", ".hpp" };

    public static string Scan(string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot)) return "Workspace tidak tersedia untuk Security Audit.";
        var root = Path.GetFullPath(workspaceRoot);
        var exclusions = new AiWorkspaceExclusionService(root);
        var findings = new List<string>();
        var filesScanned = 0;
        var entriesVisited = 0;
        long bytesScanned = 0;
        var directoriesVisited = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && directoriesVisited < MaxFiles && entriesVisited < MaxFiles &&
               filesScanned < MaxFiles && bytesScanned < MaxTotalBytes && findings.Count < MaxFindings)
        {
            var directory = pending.Pop();
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(directory); }
            catch { continue; }
            directoriesVisited++;

            foreach (var entry in entries)
            {
                if (entriesVisited++ >= MaxFiles || filesScanned >= MaxFiles || bytesScanned >= MaxTotalBytes || findings.Count >= MaxFindings) break;
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!ExcludedDirectories.Contains(Path.GetFileName(entry)) &&
                            !exclusions.IsExcluded(Path.GetRelativePath(root, entry))) pending.Push(entry);
                        continue;
                    }

                    if (exclusions.IsExcluded(Path.GetRelativePath(root, entry)) || !TextExtensions.Contains(Path.GetExtension(entry))) continue;
                    var info = new FileInfo(entry);
                    if (info.Length > MaxFileBytes) continue;
                    if (bytesScanned + info.Length > MaxTotalBytes) continue;
                    bytesScanned += info.Length;
                    filesScanned++;
                    ScanFile(root, entry, findings);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        var report = new StringBuilder();
        report.AppendLine("Offline heuristic scan (no commands, network, or file changes). These are candidates for review, not proof of a vulnerability.");
        report.AppendLine($"Text files scanned: {filesScanned} ({bytesScanned:N0} bytes); findings capped at {MaxFindings}; file traversal capped at {MaxFiles} entries.");
        report.AppendLine("Excluded: common build/dependency folders and secret-path names such as .env, credential/secret files, .pem, and .key. No dependency CVE database or runtime testing was used.");
        if (findings.Count == 0) report.AppendLine("No matches for the included heuristic rules. This does not establish that the project is secure.");
        else
        {
            report.AppendLine("Candidates:");
            foreach (var finding in findings) report.AppendLine(finding);
        }
        return report.Length > MaxReportCharacters ? report.ToString()[..MaxReportCharacters] + "\n[Report truncated.]" : report.ToString();
    }

    private static void ScanFile(string root, string path, List<string> findings)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNumber++;
            foreach (var rule in Rules)
            {
                var match = rule.Pattern.Match(line);
                if (!match.Success) continue;
                if (rule.Id == "DEPUP-SEC-001" && IsPlaceholder(match.Groups["value"].Value)) continue;
                var evidence = line.Trim();
                if (evidence.Length > 240) evidence = evidence[..240] + "…";
                evidence = DiagnosticSecretRedactor.Redact(evidence);
                findings.Add($"[{rule.Id}] {rule.Severity} · {relative}:{lineNumber} · {rule.Summary}\n  Evidence: {evidence}");
                if (findings.Count >= MaxFindings) return;
            }
        }
    }

    private static bool IsPlaceholder(string value) =>
        value.Contains("example", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("placeholder", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("changeme", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("your_", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("dummy", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("redacted", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("process.env", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("getenvironmentvariable", StringComparison.OrdinalIgnoreCase);

}
