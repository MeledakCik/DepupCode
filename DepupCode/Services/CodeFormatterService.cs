using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Text;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed record CodeFormatResult(string Text, string Formatter);

/// <summary>Routes each supported source format to the formatter shipped with Depup Code.</summary>
public static class CodeFormatterService
{
    private static readonly HashSet<string> PrettierExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".json", ".jsonc", ".css", ".scss", ".less",
        ".html", ".htm", ".md", ".mdx", ".yaml", ".yml", ".graphql", ".gql", ".vue", ".svelte", ".astro"
    };

    public static async Task<CodeFormatResult> FormatAsync(string filePath, string source, int indentSize, bool useTabs)
    {
        var extension = Path.GetExtension(filePath);
        indentSize = Math.Clamp(indentSize, 1, 8);

        if (PrettierExtensions.Contains(extension) && TryGetPrettier(filePath, out var node, out var prettierScript))
        {
            var args = new List<string> { prettierScript, "--stdin-filepath", filePath, "--tab-width", indentSize.ToString() };
            if (useTabs) args.Add("--use-tabs");
            return new CodeFormatResult(await RunAsync(node, args, source), "Prettier");
        }
        if (PrettierExtensions.Contains(extension) && FindNodeExecutable() is null &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "Assets", "Formatters", "Prettier", "node_modules", "prettier", "bin", "prettier.cjs")))
            throw new NotSupportedException("Prettier sudah disertakan, tetapi runtime Node.js tidak ditemukan. Pasang Node.js untuk memformat file ini.");

        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase) || extension.Equals(".pyi", StringComparison.OrdinalIgnoreCase))
        {
            var ruff = FindBundledFormatter("Ruff", "ruff.exe") ?? FindExecutable("ruff");
            if (ruff is not null)
            {
                var ruffArguments = new List<string>
                {
                    "format", "--stdin-filename", Path.GetFullPath(filePath),
                    "--config", $"indent-width = {indentSize}",
                    "--config", $"format.indent-style = '{(useTabs ? "tab" : "space")}'",
                    "-"
                };
                var output = await RunAsync(ruff, ruffArguments, source, Path.GetDirectoryName(Path.GetFullPath(filePath)));
                return new CodeFormatResult(output, "Ruff");
            }

            if (FindExecutable("black") is { } black)
                return new CodeFormatResult(await RunAsync(black, ["--quiet", "-"], source), "Black");
            throw new NotSupportedException("Formatter Python bawaan belum tersedia. Pastikan paket Depup Code terpasang lengkap.");
        }

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || extension.Equals(".csx", StringComparison.OrdinalIgnoreCase))
            return await FormatCSharpAsync(filePath, source, indentSize, useTabs);

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return new CodeFormatResult(FormatJson(source, indentSize, useTabs), "JSON formatter");

        if (extension.ToLowerInvariant() is ".xml" or ".xaml" or ".csproj" or ".props" or ".targets")
            return new CodeFormatResult(FormatXml(source, indentSize, useTabs), "XML formatter");

        var externalFormatter = GetExternalFormatter(extension, filePath, indentSize);
        if (externalFormatter is { } formatter && FindExecutable(formatter.Command) is { } executable)
            return new CodeFormatResult(await RunAsync(executable, formatter.Arguments, source, Path.GetDirectoryName(Path.GetFullPath(filePath))), formatter.Name);

        var expected = extension.ToLowerInvariant() switch
        {
            ".go" => "gofmt",
            ".rs" => "rustfmt",
            ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx" => "clang-format",
            ".sh" or ".bash" => "shfmt",
            ".tf" or ".tfvars" => "Terraform fmt",
            _ when PrettierExtensions.Contains(extension) => "Prettier",
            _ => "formatter untuk tipe file ini"
        };
        throw new NotSupportedException($"Formatter bawaan {expected} belum tersedia untuk {Path.GetFileName(filePath)}.");
    }

    private static async Task<CodeFormatResult> FormatCSharpAsync(string filePath, string source, int indentSize, bool useTabs)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("DepupCodeFormatter", LanguageNames.CSharp)
            .WithParseOptions(new CSharpParseOptions(LanguageVersion.Latest));
        var document = workspace.AddDocument(project.Id, Path.GetFileName(filePath), SourceText.From(source, Encoding.UTF8));
        var options = workspace.Options
            .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, indentSize)
            .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, useTabs);
        var formattedDocument = await Formatter.FormatAsync(document, options);
        var formattedText = (await formattedDocument.GetTextAsync()).ToString();
        if (source.EndsWith('\n') && !formattedText.EndsWith('\n')) formattedText += Environment.NewLine;
        return new CodeFormatResult(formattedText, "C# formatter");
    }

    private static string FormatJson(string source, int indentSize, bool useTabs)
    {
        var token = JToken.Parse(source);
        var builder = new StringBuilder();
        using var textWriter = new StringWriter(builder);
        using (var writer = new Newtonsoft.Json.JsonTextWriter(textWriter)
        {
            Formatting = Newtonsoft.Json.Formatting.Indented,
            Indentation = indentSize,
            IndentChar = useTabs ? '\t' : ' ',
            CloseOutput = false
        })
        {
            token.WriteTo(writer);
            writer.Flush();
        }
        return builder.ToString() + Environment.NewLine;
    }

    private static string FormatXml(string source, int indentSize, bool useTabs)
    {
        var document = XDocument.Parse(source);
        var declaration = document.Declaration?.ToString();
        document.Declaration = null;
        var builder = new StringBuilder();
        var settings = new System.Xml.XmlWriterSettings
        {
            Indent = true,
            IndentChars = useTabs ? "\t" : new string(' ', indentSize),
            NewLineChars = Environment.NewLine,
            NewLineHandling = System.Xml.NewLineHandling.Replace,
            OmitXmlDeclaration = true,
            CloseOutput = false
        };
        using (var writer = System.Xml.XmlWriter.Create(new StringWriter(builder), settings))
            document.Save(writer);
        var formatted = builder.ToString();
        return (string.IsNullOrWhiteSpace(declaration) ? "" : declaration + Environment.NewLine) + formatted + Environment.NewLine;
    }

    private static (string Command, IReadOnlyList<string> Arguments, string Name)? GetExternalFormatter(string extension, string filePath, int indentSize)
    {
        var pathArgument = Path.GetFullPath(filePath);
        return extension.ToLowerInvariant() switch
        {
            ".go" => ("gofmt", [], "gofmt"),
            ".rs" => ("rustfmt", ["--emit", "stdout", "--edition", "2021", "--config", $"tab_spaces={indentSize}"], "rustfmt"),
            ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hh" or ".hpp" or ".hxx" or ".java"
                => ("clang-format", ["--assume-filename", pathArgument, "--style", $"{{BasedOnStyle: LLVM, IndentWidth: {indentSize}}}"], "clang-format"),
            ".sh" or ".bash" => ("shfmt", ["-i", indentSize.ToString()], "shfmt"),
            ".tf" or ".tfvars" => ("terraform", ["fmt", "-"], "Terraform fmt"),
            _ => null
        };
    }

    private static bool TryGetPrettier(string filePath, out string node, out string prettierScript)
    {
        node = FindNodeExecutable() ?? "";
        prettierScript = "";
        if (node.Length == 0) return false;

        var current = Path.GetDirectoryName(Path.GetFullPath(filePath));
        while (!string.IsNullOrWhiteSpace(current))
        {
            var packageDirectory = Path.Combine(current, "node_modules", "prettier");
            if (TryFindPrettierScript(packageDirectory, out prettierScript)) return true;

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }

        var bundledPrettier = Path.Combine(AppContext.BaseDirectory, "Assets", "Formatters", "Prettier", "node_modules", "prettier");
        if (TryFindPrettierScript(bundledPrettier, out prettierScript)) return true;

        var globalShim = FindExecutable("prettier.cmd");
        var shimDirectory = globalShim is null ? null : Path.GetDirectoryName(globalShim);
        var globalPackage = string.IsNullOrWhiteSpace(shimDirectory) ? null : Path.Combine(shimDirectory, "node_modules", "prettier");
        if (globalPackage is not null && TryFindPrettierScript(globalPackage, out prettierScript)) return true;

        return false;
    }

    private static string? FindNodeExecutable()
    {
        var fromPath = FindExecutable("node.exe") ?? FindExecutable("node");
        if (fromPath is not null) return fromPath;
        var programFilesNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(programFilesNode) ? programFilesNode : null;
    }

    private static bool TryFindPrettierScript(string packageDirectory, out string script)
    {
        foreach (var entry in new[] { "bin\\prettier.cjs", "bin\\prettier.js", "bin\\prettier.mjs" })
        {
            var candidate = Path.Combine(packageDirectory, entry);
            if (!File.Exists(candidate)) continue;
            script = candidate;
            return true;
        }
        script = "";
        return false;
    }

    private static string? FindBundledFormatter(params string[] parts)
    {
        var path = Path.Combine([AppContext.BaseDirectory, "Assets", "Formatters", .. parts]);
        return File.Exists(path) ? path : null;
    }

    private static string? FindExecutable(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleanDirectory = directory.Trim().Trim('"');
            if (!Directory.Exists(cleanDirectory)) continue;
            foreach (var suffix in new[] { "", ".exe", ".cmd", ".bat" })
            {
                var candidate = Path.Combine(cleanDirectory, name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? name : name + suffix);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    private static async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments, string source, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Gagal memulai formatter {Path.GetFileName(executable)}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.StandardInput.WriteAsync(source);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Formatter {Path.GetFileName(executable)} melebihi batas waktu 20 detik.");
        }

        var output = await stdoutTask;
        var error = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Formatter gagal (kode {process.ExitCode})." : error.Trim());
        return output;
    }
}
