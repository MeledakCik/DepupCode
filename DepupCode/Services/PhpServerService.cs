using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DepupCode.Models;

namespace DepupCode.Services;

public enum PhpProjectKind { None, Php, Laravel }

public sealed record PhpProjectInfo(
    PhpProjectKind Kind,
    string Root,
    string DocumentRoot,
    string DisplayName,
    bool HasVendor,
    bool HasEnvFile,
    string? Warning)
{
    public bool IsLaravel => Kind == PhpProjectKind.Laravel;
    public bool IsRunnable => Kind != PhpProjectKind.None;
}

public sealed record PhpServerSession(
    string WorkspaceRoot,
    string ProjectName,
    string DocumentRoot,
    string Url,
    int Port,
    bool IsLaravel,
    string Mode,
    DateTime StartedAtUtc,
    int ProcessId,
    string StateDirectory,
    string ErrorLogPath,
    string PhpErrorLogPath);

/// <summary>
/// Menjalankan Apache + PHP milik Depup Code pada loopback. Konfigurasi selalu
/// dihasilkan aplikasi di %LOCALAPPDATA%; folder workspace tidak pernah ditulisi
/// konfigurasi server. Bila runtime Apache tidak tersedia, PHP built-in server
/// dipakai sebagai fallback yang diberi label jelas.
/// </summary>
public sealed class PhpServerService : IAsyncDisposable
{
    private const string HealthPath = "/__depup_health.php";
    private const string HealthBody = "DEPUP-PHP-OK";
    private static readonly HttpClient HealthClient = new() { Timeout = TimeSpan.FromSeconds(2) };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _logLock = new();
    private readonly List<string> _recentLog = [];
    private Process? _process;
    private ProcessJobObject? _job;
    private CancellationTokenSource? _logPumpCancellation;
    private Task? _logPump;
    private PhpServerSession? _current;
    private bool _disposed;

    public event EventHandler<PhpServerSession?>? StateChanged;
    public event EventHandler<string>? LogReceived;

    public PhpServerSession? Current
    {
        get { lock (_logLock) return _current; }
    }

    public bool IsRunning => _process is { HasExited: false };

    public string? StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupCode", "servers");

    public static PhpProjectInfo DetectProject(string root, string? documentRootOverride = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Folder workspace wajib diisi.", nameof(root));
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new DirectoryNotFoundException(fullRoot);

        var composer = ReadComposer(fullRoot);
        var hasArtisan = File.Exists(Path.Combine(fullRoot, "artisan"));
        var hasBootstrapApp = File.Exists(Path.Combine(fullRoot, "bootstrap", "app.php"));
        var hasPublicIndex = File.Exists(Path.Combine(fullRoot, "public", "index.php"));
        var composerHasLaravel = composer.Contains("laravel/framework", StringComparison.OrdinalIgnoreCase);
        var isLaravel = hasArtisan || (hasBootstrapApp && hasPublicIndex) || composerHasLaravel;

        var hasVendor = Directory.Exists(Path.Combine(fullRoot, "vendor"));
        var hasEnv = File.Exists(Path.Combine(fullRoot, ".env"));
        var displayName = Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(displayName)) displayName = fullRoot;

        string documentRoot;
        var warning = "";
        if (isLaravel)
        {
            documentRoot = Path.Combine(fullRoot, "public");
            if (!Directory.Exists(documentRoot))
                warning = "Folder public Laravel tidak ditemukan; document root tetap diarahkan ke public.";
            else if (!hasVendor)
                warning = "vendor/ belum ada. Jalankan composer install sebelum membuka Laravel.";
            if (!hasEnv)
                warning = string.IsNullOrWhiteSpace(warning)
                    ? ".env belum ada. Salin .env.example lalu isi konfigurasi aplikasi."
                    : warning + " .env belum ada.";
        }
        else
        {
            documentRoot = ResolveDocumentRoot(fullRoot, documentRootOverride);
        }

        var hasPhp = composer.Length > 0 ||
                     File.Exists(Path.Combine(fullRoot, "index.php")) ||
                     Directory.EnumerateFiles(fullRoot, "*.php", SearchOption.TopDirectoryOnly).Any();
        var kind = isLaravel ? PhpProjectKind.Laravel : hasPhp ? PhpProjectKind.Php : PhpProjectKind.None;
        if (kind == PhpProjectKind.None && hasPublicIndex)
        {
            kind = PhpProjectKind.Php;
            if (documentRootOverride is null) documentRoot = Path.Combine(fullRoot, "public");
        }
        return new PhpProjectInfo(kind, fullRoot, documentRoot, displayName, hasVendor, hasEnv, warning);
    }

    public static string ResolveDocumentRoot(string root, string? documentRootOverride)
    {
        if (string.IsNullOrWhiteSpace(documentRootOverride)) return root;
        try
        {
            var candidate = Path.GetFullPath(Path.IsPathRooted(documentRootOverride)
                ? documentRootOverride
                : Path.Combine(root, documentRootOverride));
            var relative = Path.GetRelativePath(root, candidate);
            if (Path.IsPathRooted(relative) || relative == ".." ||
                relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return root;
            return Directory.Exists(candidate) ? candidate : root;
        }
        catch { return root; }
    }

    private static string ReadComposer(string root)
    {
        try
        {
            var path = Path.Combine(root, "composer.json");
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var buffer = new char[256 * 1024];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch { return ""; }
    }

    public static int FindFreePort(int start, int end, ISet<int>? reserved = null)
    {
        start = Math.Clamp(start, 1024, 65535);
        end = Math.Clamp(end, start, 65535);
        if (end - start > 1000) end = start + 1000;
        for (var port = start; port <= end; port++)
        {
            if (reserved is not null && reserved.Contains(port)) continue;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException) { }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"Tidak ada port loopback yang bebas pada rentang {start}–{end}.");
    }

    public static string ToApachePath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/');

    public static string WorkspaceStateDirectory(string workspaceRoot)
    {
        var normalized = Path.GetFullPath(workspaceRoot).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DepupCode", "servers", hash);
    }

    public static string RenderApacheConfig(
        string apacheRoot,
        string phpDirectory,
        string documentRoot,
        string stateDirectory,
        string workspaceRoot,
        int port,
        bool isLaravel)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var root = ToApachePath(apacheRoot);
        var php = ToApachePath(phpDirectory);
        var docroot = ToApachePath(documentRoot);
        var state = ToApachePath(stateDirectory);
        var workspace = ToApachePath(workspaceRoot);
        var probeDirectory = ToApachePath(Path.Combine(stateDirectory, "probe"));
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by Depup Code. Do not edit; this file is recreated on every start.");
        sb.AppendLine($"# Workspace: {workspace}");
        sb.AppendLine($"# Generated: {DateTime.UtcNow:O}");
        sb.AppendLine($"ServerRoot \"{root}\"");
        sb.AppendLine($"Listen 127.0.0.1:{port}");
        sb.AppendLine($"ServerName 127.0.0.1:{port}");
        sb.AppendLine("ServerTokens Prod");
        sb.AppendLine("ServerSignature Off");
        sb.AppendLine("Timeout 120");
        sb.AppendLine("KeepAlive On");
        sb.AppendLine("MaxKeepAliveRequests 200");
        sb.AppendLine($"PidFile \"{state}/httpd.pid\"");
        sb.AppendLine("LogLevel warn");
        sb.AppendLine($"ErrorLog \"{state}/error.log\"");
        sb.AppendLine($"CustomLog \"{state}/access.log\" common");
        sb.AppendLine();
        foreach (var module in new[]
                 {
                     "authz_core", "authz_host", "access_compat", "dir",
                     "mime", "rewrite", "log_config", "alias"
                 })
            sb.AppendLine($"LoadModule {module}_module modules/mod_{module}.so");
        sb.AppendLine($"PHPIniDir \"{state}\"");
        sb.AppendLine($"LoadModule php_module \"{php}/php8apache2_4.dll\"");
        sb.AppendLine();
        sb.AppendLine($"DocumentRoot \"{docroot}\"");
        sb.AppendLine($"<Directory \"{docroot}\">");
        sb.AppendLine("    Options -Indexes +FollowSymLinks");
        sb.AppendLine("    AllowOverride All");
        sb.AppendLine("    Require all granted");
        sb.AppendLine("</Directory>");
        sb.AppendLine();
        sb.AppendLine("DirectoryIndex index.php index.html");
        sb.AppendLine("<FilesMatch \"\\.php$\">");
        sb.AppendLine("    SetHandler application/x-httpd-php");
        sb.AppendLine("</FilesMatch>");
        sb.AppendLine();
        sb.AppendLine("# Blokir file konfigurasi/rahasia yang tidak boleh diakses lewat HTTP.");
        sb.AppendLine("<FilesMatch \"(?i)^(\\.env.*|\\.git.*|composer\\.(json|lock)|auth\\.json|\\.htaccess|\\.htpasswd)$\">");
        sb.AppendLine("    Require all denied");
        sb.AppendLine("</FilesMatch>");
        sb.AppendLine("<DirectoryMatch \"(?i)(^|/)\\.git(/|$)\">");
        sb.AppendLine("    Require all denied");
        sb.AppendLine("</DirectoryMatch>");
        sb.AppendLine();
        sb.AppendLine("# Probe internal untuk memastikan modul PHP benar-benar aktif.");
        sb.AppendLine($"Alias {HealthPath} \"{probeDirectory}/health.php\"");
        sb.AppendLine($"<Directory \"{probeDirectory}\">");
        sb.AppendLine("    Require all granted");
        sb.AppendLine("</Directory>");
        sb.AppendLine();
        if (isLaravel)
        {
            sb.AppendLine("# Laravel front controller: semua request non-file diarahkan ke index.php.");
            sb.AppendLine("<IfModule mod_rewrite.c>");
            sb.AppendLine("    RewriteEngine On");
            sb.AppendLine("    RewriteCond %{REQUEST_FILENAME} !-f");
            sb.AppendLine("    RewriteCond %{REQUEST_FILENAME} !-d");
            sb.AppendLine("    RewriteRule ^ index.php [L]");
            sb.AppendLine("</IfModule>");
        }
        return sb.ToString();
    }

    public static string RenderPhpIni(string phpDirectory, string stateDirectory)
    {
        var php = ToApachePath(phpDirectory);
        var state = ToApachePath(stateDirectory);
        var sb = new StringBuilder();
        sb.AppendLine("[PHP]");
        sb.AppendLine("engine = On");
        sb.AppendLine("short_open_tag = Off");
        sb.AppendLine("precision = 14");
        sb.AppendLine("output_buffering = 4096");
        sb.AppendLine("zlib.output_compression = Off");
        sb.AppendLine("implicit_flush = Off");
        sb.AppendLine("serialize_precision = -1");
        sb.AppendLine("max_execution_time = 120");
        sb.AppendLine("max_input_time = 120");
        sb.AppendLine("memory_limit = 256M");
        sb.AppendLine("error_reporting = E_ALL & ~E_DEPRECATED");
        sb.AppendLine("display_errors = Off");
        sb.AppendLine("display_startup_errors = Off");
        sb.AppendLine("log_errors = On");
        sb.AppendLine($"error_log = \"{state}/php-error.log\"");
        sb.AppendLine("report_memleaks = On");
        sb.AppendLine("html_errors = Off");
        sb.AppendLine("variables_order = \"GPCS\"");
        sb.AppendLine("request_order = \"GP\"");
        sb.AppendLine("register_argc_argv = Off");
        sb.AppendLine("auto_globals_jit = On");
        sb.AppendLine("default_charset = \"UTF-8\"");
        sb.AppendLine("default_mimetype = \"text/html\"");
        sb.AppendLine("date.timezone = \"UTC\"");
        sb.AppendLine("cgi.fix_pathinfo = 1");
        sb.AppendLine("upload_max_filesize = 64M");
        sb.AppendLine("post_max_size = 64M");
        sb.AppendLine("max_file_uploads = 20");
        sb.AppendLine("allow_url_fopen = On");
        sb.AppendLine("file_uploads = On");
        sb.AppendLine();
        sb.AppendLine("; Ekstensi yang dibutuhkan PHP modern dan Laravel.");
        sb.AppendLine($"extension_dir = \"{php}/ext\"");
        foreach (var extension in new[] { "curl", "fileinfo", "mbstring", "openssl", "pdo_mysql", "pdo_sqlite", "mysqli", "sqlite3", "intl" })
            sb.AppendLine($"extension={extension}");
        sb.AppendLine();
        sb.AppendLine("[opcache]");
        sb.AppendLine("opcache.enable = 1");
        sb.AppendLine("opcache.enable_cli = 0");
        sb.AppendLine("opcache.memory_consumption = 128");
        sb.AppendLine("opcache.validate_timestamps = 1");
        sb.AppendLine("opcache.revalidate_freq = 0");
        return sb.ToString();
    }

    public static string RenderPhpFallbackRouter(string documentRoot, bool isLaravel)
    {
        var docroot = ToApachePath(documentRoot).Replace("'", "\\'");
        var sb = new StringBuilder();
        sb.AppendLine("<?php");
        sb.AppendLine("// Router sementara untuk PHP built-in server milik Depup Code.");
        sb.AppendLine($"$depupDocumentRoot = '{docroot}';");
        sb.AppendLine("$depupUri = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?: '/';");
        sb.AppendLine($"if ($depupUri === '{HealthPath}') {{ header('Content-Type: text/plain'); echo '{HealthBody}'; return true; }}");
        sb.AppendLine("if ($depupUri !== '/' && is_file($depupDocumentRoot . $depupUri)) return false;");
        if (isLaravel)
        {
            sb.AppendLine($"$_SERVER['SCRIPT_NAME'] = '/index.php';");
            sb.AppendLine($"require $depupDocumentRoot . '/index.php';");
        }
        else
        {
            sb.AppendLine("if (is_file($depupDocumentRoot . '/index.php')) return false;");
            sb.AppendLine("http_response_code(404);");
            sb.AppendLine("echo 'Not Found';");
            sb.AppendLine("return true;");
        }
        return sb.ToString();
    }

    public async Task<PhpServerSession> StartAsync(
        string workspaceRoot,
        PhpRuntimeInstallation runtime,
        AppSettings settings,
        string? documentRootOverride = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PhpServerService));
        var project = DetectProject(workspaceRoot, documentRootOverride);
        if (!project.IsRunnable)
            throw new InvalidOperationException(
                "Folder ini tidak terdeteksi sebagai project PHP atau Laravel. Pilih folder yang berisi index.php, composer.json, atau artisan.");
        if (!runtime.HasPhp && !runtime.HasApache)
            throw new InvalidOperationException("Runtime PHP/Apache belum terpasang. Unduh runtime dari Settings → Local Server.");
        if (!project.IsLaravel && !Directory.Exists(project.DocumentRoot))
            throw new InvalidOperationException("Document root tidak ditemukan: " + project.DocumentRoot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                EmitLog("Menghentikan server yang sedang berjalan sebelum memulai workspace baru.");
                await StopCoreAsync().ConfigureAwait(false);
            }

            var stateDirectory = WorkspaceStateDirectory(project.Root);
            Directory.CreateDirectory(stateDirectory);
            var probeDirectory = Path.Combine(stateDirectory, "probe");
            Directory.CreateDirectory(probeDirectory);
            await File.WriteAllTextAsync(Path.Combine(probeDirectory, "health.php"),
                $"<?php echo '{HealthBody}';", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            TryDeleteFile(Path.Combine(stateDirectory, "error.log"));
            TryDeleteFile(Path.Combine(stateDirectory, "php-error.log"));

            var port = FindFreePort(settings.LocalServerPortStart, settings.LocalServerPortEnd);
            var url = $"http://127.0.0.1:{port}";
            var confPath = Path.Combine(stateDirectory, "httpd.conf");
            var phpIniPath = Path.Combine(stateDirectory, "php.ini");
            var errorLog = Path.Combine(stateDirectory, "error.log");
            var phpErrorLog = Path.Combine(stateDirectory, "php-error.log");
            Exception? apacheFailure = null;

            if (runtime.HasPhp)
                await File.WriteAllTextAsync(phpIniPath, RenderPhpIni(runtime.PhpDirectory!, stateDirectory),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            if (runtime.HasApache)
            {
                await File.WriteAllTextAsync(confPath,
                    RenderApacheConfig(runtime.ApacheRoot!, runtime.PhpDirectory!, project.DocumentRoot, stateDirectory,
                        project.Root, port, project.IsLaravel),
                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                progress?.Report($"Menjalankan Apache + PHP di {url} …");
                try
                {
                    await StartApacheAsync(runtime, confPath, phpIniPath, stateDirectory, url, port, errorLog, phpErrorLog,
                        cancellationToken).ConfigureAwait(false);
                    return PublishSession(project, url, port, "Apache + mod_php", stateDirectory, errorLog, phpErrorLog);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    apacheFailure = ex;
                    EmitLog("Apache gagal dimulai: " + ex.Message);
                    await StopCoreAsync().ConfigureAwait(false);
                    if (!settings.LocalServerUsePhpFallback)
                        throw new InvalidOperationException(
                            "Apache gagal dimulai dan fallback PHP dinonaktifkan. " + ex.Message, ex);
                }
            }

            if (!runtime.HasPhp)
                throw new InvalidOperationException(
                    "Apache tidak tersedia dan PHP belum terpasang. Unduh runtime dari Settings → Local Server.", apacheFailure);

            progress?.Report($"Apache tidak tersedia; memakai PHP built-in server (fallback) di {url} …");
            await StartFallbackAsync(runtime, project, stateDirectory, phpIniPath, url, port, cancellationToken).ConfigureAwait(false);
            var mode = apacheFailure is null ? "PHP built-in server (fallback)" : "PHP built-in server (fallback · Apache gagal)";
            return PublishSession(project, url, port, mode, stateDirectory, errorLog, phpErrorLog);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartApacheAsync(
        PhpRuntimeInstallation runtime, string confPath, string phpIniPath, string stateDirectory,
        string url, int port, string errorLog, string phpErrorLog, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(runtime.ApacheHttpdExe!)
        {
            WorkingDirectory = runtime.ApacheRoot!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(confPath);
        AddRuntimePath(psi, runtime);
        psi.Environment["PHPRC"] = stateDirectory;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        AttachProcessLogs(process);
        _job = new ProcessJobObject();
        if (!process.Start()) throw new InvalidOperationException("Proses Apache tidak dapat dimulai.");
        _job.Assign(process);
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        StartLogPump(errorLog, phpErrorLog);

        var healthy = await WaitForHealthAsync(url, process, 14, cancellationToken).ConfigureAwait(false);
        if (!healthy)
        {
            var tail = ReadLogTail(errorLog, 24);
            throw new InvalidOperationException(
                "Apache tidak merespons di " + url + " dalam 14 detik." +
                (string.IsNullOrWhiteSpace(tail) ? "" : "\n" + tail));
        }
    }

    private async Task StartFallbackAsync(
        PhpRuntimeInstallation runtime, PhpProjectInfo project, string stateDirectory, string phpIniPath,
        string url, int port, CancellationToken cancellationToken)
    {
        var routerPath = Path.Combine(stateDirectory, "router.php");
        await File.WriteAllTextAsync(routerPath, RenderPhpFallbackRouter(project.DocumentRoot, project.IsLaravel),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        var psi = new ProcessStartInfo(runtime.PhpExe!)
        {
            WorkingDirectory = project.DocumentRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(phpIniPath);
        psi.ArgumentList.Add("-S");
        psi.ArgumentList.Add($"127.0.0.1:{port}");
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add(project.DocumentRoot);
        psi.ArgumentList.Add(routerPath);
        AddRuntimePath(psi, runtime);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        AttachProcessLogs(process);
        _job = new ProcessJobObject();
        if (!process.Start()) throw new InvalidOperationException("PHP built-in server tidak dapat dimulai.");
        _job.Assign(process);
        _process = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var healthy = await WaitForHealthAsync(url, process, 10, cancellationToken).ConfigureAwait(false);
        if (!healthy)
            throw new InvalidOperationException("PHP built-in server tidak merespons di " + url + " dalam 10 detik.");
    }

    private static void AddRuntimePath(ProcessStartInfo psi, PhpRuntimeInstallation runtime)
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(runtime.PhpDirectory)) parts.Add(runtime.PhpDirectory);
        if (!string.IsNullOrWhiteSpace(runtime.ApacheRoot)) parts.Add(Path.Combine(runtime.ApacheRoot, "bin"));
        parts.Add(current);
        psi.Environment["PATH"] = string.Join(Path.PathSeparator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private void AttachProcessLogs(Process process)
    {
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) EmitLog(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) EmitLog(e.Data); };
    }

    private void StartLogPump(string errorLog, string phpErrorLog)
    {
        _logPumpCancellation = new CancellationTokenSource();
        var token = _logPumpCancellation.Token;
        _logPump = Task.Run(() => PumpLogsAsync(errorLog, phpErrorLog, token), CancellationToken.None);
    }

    private async Task PumpLogsAsync(string errorLog, string phpErrorLog, CancellationToken cancellationToken)
    {
        long apachePosition = 0, phpPosition = 0;
        var apachePending = new StringBuilder();
        var phpPending = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var line in ReadNewLines(errorLog, ref apachePosition, apachePending))
                    EmitLog("[apache] " + line);
                foreach (var line in ReadNewLines(phpErrorLog, ref phpPosition, phpPending))
                    EmitLog("[php] " + line);
            }
            catch { }
            try { await Task.Delay(600, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static IEnumerable<string> ReadNewLines(string path, ref long position, StringBuilder pending)
    {
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < position) position = 0;
        if (stream.Length == position) return [];
        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, true);
        var text = reader.ReadToEnd();
        position = stream.Position;
        pending.Append(text);
        var lines = new List<string>();
        var content = pending.ToString();
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != '\n') continue;
            lines.Add(content[start..i].TrimEnd('\r'));
            start = i + 1;
        }
        pending.Clear();
        if (start < content.Length) pending.Append(content[start..]);
        return lines;
    }

    private PhpServerSession PublishSession(PhpProjectInfo project, string url, int port, string mode,
        string stateDirectory, string errorLog, string phpErrorLog)
    {
        var session = new PhpServerSession(project.Root, project.DisplayName, project.DocumentRoot, url, port,
            project.IsLaravel, mode, DateTime.UtcNow, _process?.Id ?? 0, stateDirectory, errorLog, phpErrorLog);
        lock (_logLock) _current = session;
        if (!string.IsNullOrWhiteSpace(project.Warning)) EmitLog(project.Warning);
        EmitLog($"{mode} aktif: {url} (document root: {project.DocumentRoot})");
        StateChanged?.Invoke(this, session);
        return session;
    }

    private static async Task<bool> WaitForHealthAsync(string url, Process process, int seconds, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var probe = url.TrimEnd('/') + HealthPath;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return false;
            try
            {
                using var response = await HealthClient.GetAsync(probe, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && body.Contains(HealthBody, StringComparison.Ordinal)) return true;
                if (response.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.Forbidden) return false;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            try { await Task.Delay(350, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }
        return false;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        var process = _process;
        _process = null;
        try { _logPumpCancellation?.Cancel(); } catch { }
        try { if (_logPump is not null) await _logPump.ConfigureAwait(false); } catch { }
        _logPump = null;
        _logPumpCancellation?.Dispose();
        _logPumpCancellation = null;

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    try { _job?.Terminate(); } catch { }
                    try { process.Kill(entireProcessTree: true); } catch { }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); } catch { }
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                }
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }
        _job?.Dispose();
        _job = null;

        PhpServerSession? previous;
        lock (_logLock)
        {
            previous = _current;
            _current = null;
        }
        if (previous is not null)
        {
            EmitLog("Server lokal dihentikan.");
            StateChanged?.Invoke(this, null);
        }
    }

    public string ReadLogTail(int lines = 60)
    {
        var session = Current;
        if (session is null) return "";
        var parts = new List<string>();
        var apache = ReadLogTail(session.ErrorLogPath, lines / 2);
        var php = ReadLogTail(session.PhpErrorLogPath, lines / 2);
        if (!string.IsNullOrWhiteSpace(apache)) parts.Add("--- Apache error.log ---\n" + apache);
        if (!string.IsNullOrWhiteSpace(php)) parts.Add("--- PHP error.log ---\n" + php);
        lock (_logLock) parts.AddRange(_recentLog.TakeLast(lines).Select(line => "[stdout] " + line));
        return string.Join("\n", parts);
    }

    public static string ReadLogTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            var tail = new Queue<string>();
            while (reader.ReadLine() is { } line)
            {
                tail.Enqueue(line);
                if (tail.Count > Math.Max(1, lines)) tail.Dequeue();
            }
            return string.Join("\n", tail);
        }
        catch { return ""; }
    }

    private void EmitLog(string line)
    {
        var safe = DiagnosticSecretRedactor.Redact(line.TrimEnd());
        if (safe.Length == 0) return;
        lock (_logLock)
        {
            _recentLog.Add(safe);
            if (_recentLog.Count > 400) _recentLog.RemoveRange(0, _recentLog.Count - 400);
        }
        try { LogReceived?.Invoke(this, safe); } catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private sealed class ProcessJobObject : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;
        private IntPtr _handle;

        public ProcessJobObject()
        {
            try
            {
                _handle = CreateJobObject(IntPtr.Zero, null);
                if (_handle == IntPtr.Zero) return;
                var info = new JobObjectExtendedLimitInformationData
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose
                    }
                };
                var length = Marshal.SizeOf<JobObjectExtendedLimitInformationData>();
                var pointer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, pointer, false);
                    SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, pointer, (uint)length);
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }
            catch { _handle = IntPtr.Zero; }
        }

        public void Assign(Process process)
        {
            if (_handle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
        }

        public void Terminate()
        {
            if (_handle != IntPtr.Zero) TerminateJobObject(_handle, 1);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            try { CloseHandle(_handle); } catch { }
            _handle = IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
