using System.Text.RegularExpressions;
using DepupCode.Models;
using Newtonsoft.Json.Linq;
using Octokit;

namespace DepupCode.Services;

public sealed class DeployService
{
    private static readonly string[] UserBuildConfigs =
        ["Dockerfile", "Procfile", "railway.json", "railway.toml", "railpack.json", "nixpacks.toml"];
    private static readonly string[] StaticBuildFrameworks =
        ["Vite", "Vue", "Create React App", "Svelte", "SolidJS", "Parcel", "Angular", "Docusaurus", "VitePress", "Eleventy", "Gatsby", "Astro", "Preact"];
    private static readonly string[] EntryCandidates =
        ["index.js", "server.js", "app.js", "main.js", "index.mjs", "server.mjs", "app.mjs", "src/index.js", "src/server.js", "src/app.js", "src/main.js"];
    private static readonly string[] PyCandidates =
        ["main.py", "app.py", "streamlit_app.py", "server.py", "api.py", "application.py", "wsgi.py", "Home.py", "dashboard.py", "app/main.py", "app/app.py", "src/main.py", "src/app.py"];
    private const string DockerIgnore = """
# Dibuat otomatis oleh Depup — mencegah rahasia & file sampah ikut ke image Docker.
.git
.gitignore
node_modules
npm-debug.log
.DS_Store
.env
.env.*
!.env.example
__pycache__
*.pyc
.venv
venv
""";

    public async Task<RepoAnalysis> AnalyzeGitHubAsync(string repositoryUrl, string? token,
        CancellationToken cancellationToken = default)
    {
        new SettingsService().EnsureOnlineMode();
        var github = new GitHubService();
        var (repo, root) = await github.GetRepositoryAsync(repositoryUrl, token, cancellationToken);
        var rootFiles = root.Where(x => x.Type == ContentType.File).Select(x => x.Name).ToList();
        var rootDirs = root.Where(x => x.Type == ContentType.Dir).Select(x => x.Name).ToList();
        var contents = root.Where(x => x.Type == ContentType.File)
            .ToDictionary(x => x.Path, x => x.Content, StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(rootDirs, StringComparer.OrdinalIgnoreCase);
        var scanDirectories = new[] { "docs", "dist", "public", "build", "site", "out", "src", "app", "frontend", "backend", "client", "server", "web", "website" }
            .Concat(rootDirs).Where(d => rootDirs.Contains(d, StringComparer.OrdinalIgnoreCase) &&
                d is not (".git" or "node_modules" or ".next" or "vendor"))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(20);
        foreach (var directory in scanDirectories)
        {
            try
            {
                var items = await github.GetDirectoryContentsAsync(repo.Owner.Login, repo.Name, directory, token, cancellationToken);
                foreach (var item in items)
                {
                    if (item.Type == ContentType.File) contents[item.Path] = item.Content;
                    else if (item.Type == ContentType.Dir) directories.Add(item.Path);
                }
            }
            catch (NotFoundException) { }
        }
        IReadOnlyCollection<string> ListFiles(string path) => contents.Keys
            .Where(p => (path.Length == 0 ? !p.Contains('/') :
                p.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) &&
                p[(path.TrimEnd('/').Length + 1)..].IndexOf('/') < 0))
            .Select(p => path.Length == 0 ? p : p[(path.TrimEnd('/').Length + 1)..]).ToList();
        IReadOnlyCollection<string> ListDirectories(string path) => directories
            .Where(p => path.Length == 0 ? !p.Contains('/') :
                p.StartsWith(path.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) &&
                p[(path.TrimEnd('/').Length + 1)..].IndexOf('/') < 0)
            .Select(p => path.Length == 0 ? p : p[(path.TrimEnd('/').Length + 1)..]).ToList();
        string? Read(string path) => contents.TryGetValue(path.Trim('/'), out var value) ? value : null;
        bool HasDir(string path) => directories.Contains(path.Trim('/'));
        var analysis = Analyze(rootFiles, rootDirs, Read, HasDir, ListFiles, ListDirectories, true,
            repo.Owner.Login, repo.Name, repo.DefaultBranch, repo.HtmlUrl);
        analysis.GitHubRepositoryId = repo.Id;
        return analysis;
    }

    public RepoAnalysis AnalyzeDirectory(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var rootFiles = Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList();
        var rootDirs = Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList();
        string? Read(string relative)
        {
            var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllText(full) : null;
        }
        bool HasDir(string path) => Directory.Exists(Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar)));
        IReadOnlyCollection<string> ListFiles(string path)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(full) ? Directory.EnumerateFiles(full).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList() : [];
        }
        IReadOnlyCollection<string> ListDirectories(string path)
        {
            var full = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            return Directory.Exists(full) ? Directory.EnumerateDirectories(full).Select(Path.GetFileName).Where(x => x is not null).Cast<string>().ToList() : [];
        }
        var origin = GitService.ReadOriginUrl(folder) ?? new DirectoryInfo(folder).FullName;
        var branch = GitService.ReadStatus(folder).Branch;
        if (branch == "No Git") branch = "main";
        var analysis = Analyze(rootFiles, rootDirs, Read, HasDir, ListFiles, ListDirectories, true,
            "local", Path.GetFileName(folder.TrimEnd('\\', '/')), branch, origin);
        analysis.LocalSourcePath = Path.GetFullPath(folder);
        if (!string.IsNullOrWhiteSpace(analysis.RootDirectory))
            analysis.LocalSourcePath = Path.Combine(analysis.LocalSourcePath, analysis.RootDirectory.Replace('/', Path.DirectorySeparatorChar));
        return analysis;
    }

    public void ApplyFixesLocally(RepoAnalysis analysis, string folder)
    {
        foreach (var fix in analysis.Fixes)
        {
            var target = Path.Combine(folder, fix.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (fix.Action == "create" && File.Exists(target)) continue;
            File.WriteAllText(target, fix.Content);
        }
    }

    private static RepoAnalysis Analyze(IReadOnlyCollection<string> rootFiles, IReadOnlyCollection<string> rootDirs,
        Func<string, string?> readText, Func<string, bool> hasDirectory,
        Func<string, IReadOnlyCollection<string>> listFiles, Func<string, IReadOnlyCollection<string>> listDirectories,
        bool allowSubdirectory, string owner, string repository, string branch, string url)
    {
        if (allowSubdirectory && !HasProjectAtRoot(rootFiles, readText))
        {
            var candidate = rootDirs.Where(d => d is not (".git" or "node_modules" or "public" or "dist" or "build" or ".next" or "vendor"))
                .FirstOrDefault(d => HasProjectAtRoot(listFiles(d), path => readText(d + "/" + path)));
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var nestedFiles = listFiles(candidate);
                var nestedDirs = listDirectories(candidate);
                var nested = Analyze(nestedFiles, nestedDirs, path => readText(candidate + "/" + path),
                    path => hasDirectory(candidate + "/" + path),
                    path => listFiles(candidate + "/" + path), path => listDirectories(candidate + "/" + path),
                    false, owner, repository, branch, url);
                nested.RootDirectory = candidate;
                return nested;
            }
        }
        var files = rootFiles.Select(f => f).ToList();
        var nodeText = readText("package.json");
        JObject? package = null;
        try { if (!string.IsNullOrWhiteSpace(nodeText)) package = JObject.Parse(nodeText); } catch { }
        var dependencies = string.Join(" ", (package?["dependencies"] as JObject ?? new JObject()).Properties()
                .Select(p => p.Name).Concat((package?["devDependencies"] as JObject ?? new JObject()).Properties().Select(p => p.Name)))
            .ToLowerInvariant();
        var pythonFiles = rootFiles.Contains("requirements.txt", StringComparer.OrdinalIgnoreCase) ||
                          rootFiles.Contains("pyproject.toml", StringComparer.OrdinalIgnoreCase) ||
                          rootFiles.Contains("Pipfile", StringComparer.OrdinalIgnoreCase) ||
                          rootFiles.Contains("manage.py", StringComparer.OrdinalIgnoreCase);
        var htmlFiles = rootFiles.Where(f => Regex.IsMatch(f, @"\.html?$", RegexOptions.IgnoreCase)).ToList();
        var hasIndex = rootFiles.Any(f => f.Equals("index.html", StringComparison.OrdinalIgnoreCase));
        var staticDir = hasIndex ? "" : FindStaticDirectory(rootDirs, hasDirectory, readText);
        var framework = DetectFramework(package, dependencies, pythonFiles, rootFiles, rootDirs);
        var kind = rootFiles.Contains("Dockerfile", StringComparer.OrdinalIgnoreCase) ? ProjectKind.Docker
            : package is not null ? ProjectKind.Node
            : pythonFiles ? ProjectKind.Python
            : hasIndex || staticDir is not null || htmlFiles.Count > 0 ? ProjectKind.Static
            : ProjectKind.Unknown;

        var projectRoot = "";

        var supports = kind switch
        {
            ProjectKind.Static => new List<string> { "Depup Edge CDN", "Vercel", "Cloudflare Pages", "Railway" },
            ProjectKind.Node when framework is "Express" or "Fastify" or "Hono" or "Koa" or "NestJS" => new List<string> { "Depup Edge CDN", "Vercel", "Railway" },
            ProjectKind.Node => new List<string> { "Depup Edge CDN", "Vercel", "Cloudflare Pages", "Railway" },
            ProjectKind.Python or ProjectKind.Docker => new List<string> { "Railway", "Depup Edge CDN" },
            _ => new List<string> { "Depup Edge CDN", "Railway" }
        };
        var analysis = new RepoAnalysis
        {
            Owner = owner,
            Repository = repository,
            Branch = string.IsNullOrWhiteSpace(branch) ? "main" : branch,
            RepositoryUrl = url,
            Kind = kind,
            Framework = framework,
            RootDirectory = projectRoot,
            StaticDirectory = staticDir ?? "",
            Files = files,
            SupportedPlatforms = supports,
            Summary = kind switch
            {
                ProjectKind.Static => $"Situs statis {framework} — Depup Edge CDN, Vercel, Cloudflare Pages, dan Railway.",
                ProjectKind.Node => $"Project Node.js {framework} — mendukung Depup Cloud, Railway, & Vercel.",
                ProjectKind.Python => "Server Python — Railway & Depup Cloud mendukung Procfile dan port dinamis.",
                ProjectKind.Docker => "Project Docker — Railway dapat membangun Dockerfile.",
                _ => "Project terdeteksi — siap dideploy via Depup Cloud atau Railway."
            }
        };
        if (kind != ProjectKind.Docker && !UserBuildConfigs.Any(c => rootFiles.Contains(c, StringComparer.OrdinalIgnoreCase)))
        {
            switch (kind)
            {
                case ProjectKind.Static:
                    PlanStatic(analysis, rootFiles, rootDirs, staticDir, htmlFiles);
                    break;
                case ProjectKind.Node:
                    PlanNode(analysis, package, nodeText, rootFiles, hasDirectory, framework);
                    break;
                case ProjectKind.Python:
                    PlanPython(analysis, rootFiles, readText, hasDirectory);
                    break;
            }
        }
        if (rootFiles.Contains("Dockerfile", StringComparer.OrdinalIgnoreCase) &&
            !rootFiles.Contains(".dockerignore", StringComparer.OrdinalIgnoreCase))
            analysis.Fixes.Add(new PlannedFile
            {
                Path = ".dockerignore",
                Action = "create",
                Reason = "Ada Dockerfile tapi belum ada .dockerignore, jadi .env, .git, dan node_modules bisa ikut ke image.",
                Content = DockerIgnore
            });
        return analysis;
    }

    private static string DetectFramework(JObject? package, string dependencies, bool python, IReadOnlyCollection<string> files,
        IReadOnlyCollection<string> directories)
    {
        if (package is not null)
        {
            if (dependencies.Contains("next") && dependencies.Contains("capacitor")) return "Next.js 14 + Capacitor";
            if (dependencies.Contains("next")) return "Next.js";
            if (dependencies.Contains("nuxt")) return "Nuxt";
            if (dependencies.Contains("@nestjs")) return "NestJS";
            if (dependencies.Contains("fastify") && dependencies.Contains("supabase")) return "Fastify + Supabase";
            if (dependencies.Contains("express")) return "Express";
            if (dependencies.Contains("fastify")) return "Fastify";
            if (dependencies.Contains("hono")) return "Hono";
            if (dependencies.Contains("koa")) return "Koa";
            if (dependencies.Contains("vite")) return "Vite";
            if (dependencies.Contains("astro")) return "Astro";
            if (dependencies.Contains("react-scripts")) return "Create React App";
            if (dependencies.Contains("react") && dependencies.Contains("tailwind")) return "React 18 + Tailwind";
            if (dependencies.Contains("vue")) return "Vue";
            if (dependencies.Contains("angular")) return "Angular";
            if (dependencies.Contains("svelte")) return "Svelte";
            return "Node.js";
        }
        if (python) return "Python";
        if (files.Contains("pubspec.yaml", StringComparer.OrdinalIgnoreCase)) return "Flutter / Dart";
        if (files.Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))) return ".NET 8 / C#";
        if (files.Any(f => f.Equals("index.html", StringComparison.OrdinalIgnoreCase)) ||
            directories.Any(d => d is "docs" or "dist" or "public")) return "HTML statis";
        return "Web Project";
    }

    private static bool HasProjectAtRoot(IReadOnlyCollection<string> files, Func<string, string?> readText)
    {
        if (files.Contains("package.json", StringComparer.OrdinalIgnoreCase) ||
            files.Contains("Dockerfile", StringComparer.OrdinalIgnoreCase) ||
            files.Contains("requirements.txt", StringComparer.OrdinalIgnoreCase) ||
            files.Contains("pyproject.toml", StringComparer.OrdinalIgnoreCase) ||
            files.Contains("Pipfile", StringComparer.OrdinalIgnoreCase) ||
            files.Contains("manage.py", StringComparer.OrdinalIgnoreCase)) return true;
        return readText("package.json") is not null;
    }

    private static string? FindStaticDirectory(IReadOnlyCollection<string> rootDirs, Func<string, bool> hasDirectory, Func<string, string?> readText)
    {
        var preferred = new[] { "docs", "dist", "public", "build", "site", "out" };
        foreach (var dir in preferred.Concat(rootDirs))
        {
            if (rootDirs.Contains(dir, StringComparer.OrdinalIgnoreCase) &&
                hasDirectory(dir) && readText(dir.TrimEnd('/') + "/index.html") is not null)
                return dir;
        }
        return null;
    }

    private static void PlanStatic(RepoAnalysis analysis, IReadOnlyCollection<string> rootFiles, IReadOnlyCollection<string> rootDirs,
        string? staticDir, List<string> htmlFiles)
    {
        if (rootFiles.Contains("Staticfile", StringComparer.OrdinalIgnoreCase)) return;
        if (!string.IsNullOrEmpty(staticDir))
        {
            if (Regex.IsMatch(staticDir, @"^[A-Za-z0-9._\-/]+$"))
                analysis.Fixes.Add(new PlannedFile { Path = "Staticfile", Action = "create",
                    Reason = $"index.html ada di folder {staticDir}/. Staticfile mengarahkan Railway ke folder itu.",
                    Content = $"root: {staticDir}\n" });
            return;
        }
        if (rootFiles.Contains("index.html", StringComparer.OrdinalIgnoreCase) &&
            rootDirs.Contains("public", StringComparer.OrdinalIgnoreCase))
        {
            analysis.Fixes.Add(new PlannedFile { Path = "Staticfile", Action = "create",
                Reason = "Repo punya index.html di root sekaligus public/. Staticfile menjaga Railway tetap menyajikan root.",
                Content = "root: .\n" });
            return;
        }
        if (htmlFiles.Count == 0) return;
        var sorted = htmlFiles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var preferred = sorted.FirstOrDefault(f => Regex.IsMatch(f, @"^index\.html?$", RegexOptions.IgnoreCase)) ??
                        sorted.FirstOrDefault(f => Regex.IsMatch(f, @"^(home|beranda|main|default)\.html?$", RegexOptions.IgnoreCase)) ??
                        (sorted.Count == 1 ? sorted[0] : null);
        var content = "<!doctype html>\n<html lang=\"id\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n";
        string reason;
        if (preferred is not null)
        {
            var href = Uri.EscapeDataString(preferred);
            reason = $"Tidak ada index.html di root. Dibuat pengalihan ke {preferred}.";
            content += $"<title>{System.Net.WebUtility.HtmlEncode(preferred)}</title>\n<meta http-equiv=\"refresh\" content=\"0; url={href}\">\n</head>\n<body>\n<p>Mengalihkan ke <a href=\"{href}\">{System.Net.WebUtility.HtmlEncode(preferred)}</a>…</p>\n</body>\n</html>\n";
        }
        else
        {
            var links = string.Join("\n", sorted.Select(f => $"<li><a href=\"{Uri.EscapeDataString(f)}\">{System.Net.WebUtility.HtmlEncode(f)}</a></li>"));
            reason = $"Tidak ada index.html. Dibuat index.html berisi daftar {sorted.Count} halaman.";
            content += "<title>Daftar halaman</title>\n<style>body{font-family:system-ui,sans-serif;max-width:40rem;margin:3rem auto;padding:0 1rem;line-height:1.7}</style>\n</head>\n<body>\n<h1>Daftar halaman</h1>\n<ul>\n" + links + "\n</ul>\n</body>\n</html>\n";
        }
        analysis.Fixes.Add(new PlannedFile { Path = "index.html", Action = "create", Reason = reason, Content = content });
    }

    private static void PlanNode(RepoAnalysis analysis, JObject? package, string? raw, IReadOnlyCollection<string> files,
        Func<string, bool> hasDirectory, string framework)
    {
        if (package is null || raw is null) return;
        var scripts = package["scripts"] as JObject ?? new JObject();
        if (scripts["start"] is not null) return;
        var start = framework switch
        {
            "Next.js" or "Next.js 14 + Capacitor" => "next start",
            "Nuxt" => NuxtStart(package),
            "NestJS" => scripts["build"] is not null ? "node dist/main" : null,
            _ => PickNodeStart(package, scripts, files, hasDirectory, framework)
        };
        if (start is null) return;
        var indentation = Regex.Match(raw, @"(?m)^([ \t]+)""").Groups[1].Value;
        if (string.IsNullOrEmpty(indentation)) indentation = "  ";
        scripts["start"] = start;
        var updated = JsonWithIndent(package, indentation);
        analysis.Fixes.Add(new PlannedFile { Path = "package.json", Action = "update",
            Reason = $"Tidak ada script start di package.json. Ditambahkan start: {start}.", Content = updated + (raw.EndsWith('\n') ? "\n" : "") });
    }

    private static string? NuxtStart(JObject package)
    {
        var version = package["dependencies"]?["nuxt"]?.ToString() ?? package["devDependencies"]?["nuxt"]?.ToString() ?? "";
        var match = Regex.Match(version, @"(\d+)");
        return int.TryParse(match.Value, out var major) && major >= 3 ? "node .output/server/index.mjs" : "nuxt start";
    }

    private static string? PickNodeStart(JObject package, JObject scripts, IReadOnlyCollection<string> files,
        Func<string, bool> hasDirectory, string framework)
    {
        if (StaticBuildFrameworks.Any(name => framework.StartsWith(name, StringComparison.OrdinalIgnoreCase))) return null;
        if (framework is not ("Express" or "Fastify" or "Fastify + Supabase" or "Hono" or "Koa" or "Node.js")) return null;
        var main = package["main"]?.ToString()?.TrimStart('.', '/') ?? "";
        if (main.Length > 0 && Regex.IsMatch(main, @"^(dist|build|out)/.*\.(c|m)?js$") && scripts["build"] is not null)
            return "node " + main;
        var candidates = new[] { main }.Concat(EntryCandidates).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct();
        foreach (var candidate in candidates)
        {
            if (files.Contains(candidate, StringComparer.OrdinalIgnoreCase)) return "node " + candidate;
            var parent = Path.GetDirectoryName(candidate)?.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(parent) && hasDirectory(parent)) return "node " + candidate;
        }
        return null;
    }

    private static string JsonWithIndent(JObject obj, string indent)
    {
        var formatted = obj.ToString(Newtonsoft.Json.Formatting.Indented);
        if (indent == "  ") return formatted;
        return Regex.Replace(formatted, @"(?m)^( +)", m => string.Concat(Enumerable.Repeat(indent, m.Value.Length / 2)));
    }

    private static void PlanPython(RepoAnalysis analysis, IReadOnlyCollection<string> rootFiles,
        Func<string, string?> readText, Func<string, bool> hasDirectory)
    {
        var requirements = readText("requirements.txt");
        var pyproject = readText("pyproject.toml");
        var pipfile = readText("Pipfile");
        if (requirements is null && pyproject is null && pipfile is null) return;
        bool Declared(string pkg) => (requirements is not null && Regex.IsMatch(requirements, $@"(?im)^\s*{Regex.Escape(pkg)}(\[[^\]]*\])?\s*([<>=!~;#@]|$)")) ||
            (pyproject is not null && Regex.IsMatch(pyproject, $@"\b{Regex.Escape(pkg)}\b", RegexOptions.IgnoreCase)) ||
            (pipfile is not null && Regex.IsMatch(pipfile, $@"\b{Regex.Escape(pkg)}\b", RegexOptions.IgnoreCase));
        if (rootFiles.Contains("manage.py", StringComparer.OrdinalIgnoreCase) && Declared("django") && requirements is not null && !Declared("gunicorn"))
            analysis.Fixes.Add(new PlannedFile { Path = "requirements.txt", Action = "update",
                Reason = "Django menggunakan gunicorn di Railway; paket ditambahkan ke requirements.txt.",
                Content = AppendLine(requirements, "gunicorn") });

        if (rootFiles.Contains("manage.py", StringComparer.OrdinalIgnoreCase)) return;
        foreach (var file in PyCandidates)
        {
            var exists = rootFiles.Contains(file, StringComparer.OrdinalIgnoreCase) ||
                         (Path.GetDirectoryName(file) is { Length: > 0 } dir && hasDirectory(dir));
            if (!exists) continue;
            var source = readText(file);
            if (string.IsNullOrEmpty(source)) continue;
            var match = Regex.Match(source, @"(?m)^\s*(\w+)\s*(?::\s*[\w.[\]]+\s*)?=\s*(?:fastapi\.)?FastAPI\s*\(");
            var server = "uvicorn";
            var label = "FastAPI";
            if (!match.Success)
            {
                match = Regex.Match(source, @"(?m)^\s*(\w+)\s*(?::\s*[\w.[\]]+\s*)?=\s*(?:starlette\.)?Starlette\s*\(");
                if (match.Success) label = "Starlette";
            }
            if (!match.Success)
            {
                match = Regex.Match(source, @"(?m)^\s*(\w+)\s*(?::\s*[\w.[\]]+\s*)?=\s*(?:litestar\.)?Litestar\s*\(");
                if (match.Success) label = "Litestar";
            }
            var command = match.Success ? $"uvicorn {file.Replace(".py", "").Replace('/', '.') }:{match.Groups[1].Value} --host 0.0.0.0 --port $PORT" : "";
            if (!match.Success)
            {
                match = Regex.Match(source, @"(?m)^\s*(\w+)\s*=\s*(?:flask\.)?Flask\s*\(");
                if (match.Success)
                {
                    server = "gunicorn"; label = "Flask";
                    command = $"gunicorn {file.Replace(".py", "").Replace('/', '.') }:{match.Groups[1].Value} --bind 0.0.0.0:$PORT";
                }
            }
            if (!match.Success)
            {
                match = Regex.Match(source, @"(?m)^\s*(\w+)\s*=\s*(?:quart\.)?Quart\s*\(");
                if (match.Success)
                {
                    label = "Quart";
                    command = $"uvicorn {file.Replace(".py", "").Replace('/', '.') }:{match.Groups[1].Value} --host 0.0.0.0 --port $PORT";
                }
            }
            if (!match.Success && Regex.IsMatch(source, @"(?m)^\s*(?:import|from)\s+streamlit\b"))
            {
                server = "streamlit"; label = "Streamlit";
                command = $"streamlit run {file} --server.port $PORT --server.address 0.0.0.0 --server.headless true";
                match = Match.Empty;
            }
            if (string.IsNullOrEmpty(command)) continue;
            if (!Declared(server) && requirements is null) return;
            analysis.Fixes.Add(new PlannedFile { Path = "Procfile", Action = "create",
                Reason = $"Terdeteksi {label} di {file}. Procfile menjalankan aplikasi pada port Railway.",
                Content = $"web: {command}\n" });
            if (requirements is not null && !Declared(server))
                analysis.Fixes.Add(new PlannedFile { Path = "requirements.txt", Action = "update",
                    Reason = $"{server} belum ada di requirements.txt, padahal dibutuhkan oleh Procfile.",
                    Content = AppendLine(requirements, server) });
            return;
        }
    }

    private static string AppendLine(string text, string line) =>
        (text.EndsWith('\n') || text.Length == 0 ? text : text + "\n") + line + "\n";
}
