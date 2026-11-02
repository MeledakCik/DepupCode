using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using DepupCode.Models;
using DepupCode.Services;
using Xunit;

namespace DepupCode.Tests;

public sealed class PhpServerServiceTests
{
    [Fact]
    public void Detects_laravel_and_forces_public_document_root()
    {
        var root = CreateWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "artisan"), "#!/usr/bin/env php\n");
            File.WriteAllText(Path.Combine(root, "composer.json"), """{"require":{"laravel/framework":"^11.0"}}""");
            Directory.CreateDirectory(Path.Combine(root, "public"));
            File.WriteAllText(Path.Combine(root, "public", "index.php"), "<?php echo 'ok';");

            var project = PhpServerService.DetectProject(root);

            Assert.Equal(PhpProjectKind.Laravel, project.Kind);
            Assert.True(project.IsLaravel);
            Assert.Equal(Path.Combine(root, "public"), project.DocumentRoot);
            Assert.False(project.HasVendor);
            Assert.False(project.HasEnvFile);
            Assert.Contains("vendor", project.Warning, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Detects_plain_php_and_respects_subfolder_override()
    {
        var root = CreateWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "index.php"), "<?php echo 'ok';");
            Directory.CreateDirectory(Path.Combine(root, "site"));
            Directory.CreateDirectory(Path.Combine(root, "outside"));

            var project = PhpServerService.DetectProject(root, "site");

            Assert.Equal(PhpProjectKind.Php, project.Kind);
            Assert.Equal(Path.Combine(root, "site"), project.DocumentRoot);
            Assert.Equal(root, PhpServerService.ResolveDocumentRoot(root, "..\\outside"));
            Assert.Equal(Path.Combine(root, "outside"), PhpServerService.ResolveDocumentRoot(root, "outside"));

            var plain = PhpServerService.DetectProject(root);
            Assert.Equal(root, plain.DocumentRoot);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Non_php_folder_is_not_runnable()
    {
        var root = CreateWorkspace();
        try
        {
            File.WriteAllText(Path.Combine(root, "readme.md"), "hello");
            var project = PhpServerService.DetectProject(root);
            Assert.Equal(PhpProjectKind.None, project.Kind);
            Assert.False(project.IsRunnable);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Apache_config_is_loopback_only_and_uses_forward_slashes()
    {
        var config = PhpServerService.RenderApacheConfig(
            @"C:\Program Files\Apache 2.4", @"C:\Users\Hype AMD\php 8.4", @"C:\Work\Laravel App\public",
            @"C:\Users\Hype AMD\AppData\Local\DepupCode\servers\abc", @"C:\Work\Laravel App", 8123, isLaravel: true);

        Assert.Contains("Listen 127.0.0.1:8123", config);
        Assert.DoesNotContain("0.0.0.0", config);
        Assert.Contains("ServerRoot \"C:/Program Files/Apache 2.4\"", config);
        Assert.Contains("DocumentRoot \"C:/Work/Laravel App/public\"", config);
        Assert.Contains("C:/Users/Hype AMD/php 8.4/php8apache2_4.dll", config);
        Assert.Contains("AllowOverride All", config);
        Assert.Contains("DirectoryIndex index.php index.html", config);
        Assert.Contains("RewriteCond %{REQUEST_FILENAME} !-f", config);
        Assert.Contains("RewriteRule ^ index.php [L]", config);
        Assert.Contains(".env", config);
        Assert.Contains("composer\\.(json|lock)", config);
        Assert.Contains("mod_alias.so", config);
        Assert.DoesNotContain("mod_proxy", config);

        var iniIndex = config.IndexOf("PHPIniDir", StringComparison.Ordinal);
        var moduleIndex = config.IndexOf("LoadModule php_module", StringComparison.Ordinal);
        Assert.True(iniIndex >= 0 && moduleIndex > iniIndex, "PHPIniDir harus muncul sebelum LoadModule php_module.");
    }

    [Fact]
    public void Plain_php_config_has_no_laravel_front_controller()
    {
        var config = PhpServerService.RenderApacheConfig(
            @"C:\Apache24", @"C:\php", @"C:\Work\Plain", @"C:\state", @"C:\Work\Plain", 8000, isLaravel: false);

        Assert.DoesNotContain("RewriteRule ^ index.php [L]", config);
        Assert.Contains("Options -Indexes", config);
    }

    [Fact]
    public void Php_ini_contains_laravel_extensions_and_safe_error_settings()
    {
        var ini = PhpServerService.RenderPhpIni(@"C:\php runtime", @"C:\state folder");

        Assert.Contains("extension_dir = \"C:/php runtime/ext\"", ini);
        foreach (var extension in new[] { "curl", "fileinfo", "mbstring", "openssl", "pdo_mysql", "pdo_sqlite" })
            Assert.Contains($"extension={extension}", ini);
        Assert.Contains("display_errors = Off", ini);
        Assert.Contains("log_errors = On", ini);
        Assert.Contains("error_log = \"C:/state folder/php-error.log\"", ini);
        Assert.Contains("opcache.enable = 1", ini);
    }

    [Fact]
    public void Fallback_router_has_health_probe_and_laravel_entrypoint()
    {
        var router = PhpServerService.RenderPhpFallbackRouter(@"C:\Work\Laravel App\public", isLaravel: true);

        Assert.Contains("__depup_health.php", router);
        Assert.Contains("DEPUP-PHP-OK", router);
        Assert.Contains("C:/Work/Laravel App/public", router);
        Assert.Contains("require $depupDocumentRoot . '/index.php';", router);
    }

    [Fact]
    public void Find_free_port_skips_a_bound_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var busy = ((IPEndPoint)listener.LocalEndpoint).Port;

        var port = PhpServerService.FindFreePort(busy, Math.Min(65535, busy + 12));

        Assert.NotEqual(busy, port);
        Assert.InRange(port, busy + 1, Math.Min(65535, busy + 12));
    }

    [Fact]
    public void Find_free_port_reports_exhausted_range()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var busy = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.Throws<InvalidOperationException>(() => PhpServerService.FindFreePort(busy, busy));
    }

    [Fact]
    public void Safe_zip_extractor_rejects_zip_slip()
    {
        var root = CreateWorkspace();
        try
        {
            var archive = Path.Combine(root, "evil.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("../evil.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("escape");
            }
            var destination = Path.Combine(root, "out");

            var error = Assert.Throws<InvalidDataException>(() => SafeZipExtractor.ExtractToDirectory(archive, destination));
            Assert.Contains("tidak aman", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Safe_zip_extractor_extracts_normal_archive()
    {
        var root = CreateWorkspace();
        try
        {
            var archive = Path.Combine(root, "runtime.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("bin/php.exe");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("fake");
            }
            var destination = Path.Combine(root, "out");

            SafeZipExtractor.ExtractToDirectory(archive, destination);

            Assert.Equal("fake", File.ReadAllText(Path.Combine(destination, "bin", "php.exe")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Runtime_manifest_is_pinned_to_official_https_and_sha256()
    {
        foreach (var manifest in new[] { PhpRuntimeService.PhpManifest, PhpRuntimeService.ApacheManifest })
        {
            var uri = new Uri(manifest.DownloadUrl);
            Assert.Equal(Uri.UriSchemeHttps, uri.Scheme);
            Assert.Contains(uri.Host, new[] { "windows.php.net", "www.apachelounge.com" });
            Assert.Matches("^[a-f0-9]{64}$", manifest.Sha256);
            Assert.False(string.IsNullOrWhiteSpace(manifest.Version));
            Assert.False(string.IsNullOrWhiteSpace(manifest.LicenseFile));
        }
        Assert.NotEqual(PhpRuntimeService.PhpSha256, PhpRuntimeService.ApacheSha256);
    }

    [Fact]
    public void Workspace_state_directory_is_stable_and_opaque()
    {
        var first = PhpServerService.WorkspaceStateDirectory(@"C:\Work\My App");
        var second = PhpServerService.WorkspaceStateDirectory(@"C:\Work\My App");
        var other = PhpServerService.WorkspaceStateDirectory(@"C:\Work\Other");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.DoesNotContain("My App", first);
        Assert.Contains(Path.Combine("DepupCode", "servers"), first);
    }

    [Fact]
    public void Unsupported_or_empty_runtime_paths_are_ignored()
    {
        Assert.Null(PhpRuntimeService.ResolvePhpDirectory(""));
        Assert.Null(PhpRuntimeService.ResolvePhpDirectory(@"C:\does\not\exist"));
        Assert.Null(PhpRuntimeService.ResolveApacheRoot(@"C:\does\not\exist"));

        var settings = new AppSettings { PhpRuntimePath = @"C:\nope", ApacheRuntimePath = @"C:\nope" };
        var runtime = new PhpRuntimeService(Path.Combine(Path.GetTempPath(), "depup-unused-runtime")).Resolve(settings);
        Assert.False(runtime.HasPhp);
        Assert.False(runtime.HasApache);
        Assert.False(runtime.IsComplete);
    }

    [Fact]
    public void Secret_redaction_hides_tokens_in_server_logs()
    {
        var redacted = DiagnosticSecretRedactor.Redact("APP_KEY=base64:abc123 token: sk-proj-abcdefghijklmnop Authorization: Bearer abc.def.ghi");
        Assert.DoesNotContain("sk-proj-abcdefghijklmnop", redacted);
        Assert.DoesNotContain("Bearer abc.def.ghi", redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "depup-php-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
