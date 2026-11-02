using System.Diagnostics;
using System.Security.Cryptography;
using DepupCode.Models;
using Newtonsoft.Json;

namespace DepupCode.Services;

public sealed record RuntimeComponentManifest(
    string Name,
    string Version,
    string DownloadUrl,
    string Sha256,
    string SizeLabel,
    string Homepage,
    string LicenseFile);

/// <summary>
/// Runtime Apache + PHP milik Depup Code. Semua binary diunduh dari sumber resmi
/// saat pertama dipakai, diverifikasi SHA-256, lalu diekstrak ke %LOCALAPPDATA%.
/// Tidak ada installer, service Windows, hak Administrator, atau perubahan PATH global.
/// </summary>
public sealed class PhpRuntimeService
{
    public const string PhpVersion = "8.4.27";
    public const string PhpDownloadUrl = "https://windows.php.net/downloads/releases/php-8.4.27-1-Win32-vs17-x64.zip";
    public const string PhpSha256 = "53db2047139bdfb8e6c7c7d5860a0a6a2a4f1dcac49fcf18ae610a14d8ccdf5b";
    public const string PhpSizeLabel = "33.82 MB";

    public const string ApacheVersion = "2.4.66";
    public const string ApacheDownloadUrl = "https://www.apachelounge.com/download/VS17/binaries/httpd-2.4.66-251206-Win64-VS17.zip";
    public const string ApacheSha256 = "2cd1f349b6705e43e784e876a233eeb1a859fa6b8abc693a91f87b35a368f7f9";
    public const string ApacheSizeLabel = "10.99 MB";

    private static readonly HttpClient Http = CreateHttpClient();

    public PhpRuntimeService(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DepupCode", "runtimes")
            : Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string PhpDirectory => Path.Combine(RootDirectory, "php", PhpVersion);
    public string ApacheDirectory => Path.Combine(RootDirectory, "apache", ApacheVersion);
    public string ManifestPath => Path.Combine(RootDirectory, "manifest.json");
    public string NoticePath => Path.Combine(RootDirectory, "NOTICE.txt");

    public static RuntimeComponentManifest PhpManifest => new(
        "PHP", PhpVersion, PhpDownloadUrl, PhpSha256, PhpSizeLabel,
        "https://windows.php.net/", "php/license.txt");

    public static RuntimeComponentManifest ApacheManifest => new(
        "Apache HTTP Server", ApacheVersion, ApacheDownloadUrl, ApacheSha256, ApacheSizeLabel,
        "https://httpd.apache.org/", "apache/LICENSE.txt");

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DepupCode/2.5.0 (Windows; local PHP runtime)");
        return client;
    }

    public bool IsPhpInstalled => File.Exists(Path.Combine(PhpDirectory, "php.exe")) &&
                                  File.Exists(Path.Combine(PhpDirectory, "php8apache2_4.dll"));
    public bool IsApacheInstalled => File.Exists(Path.Combine(ApacheDirectory, "bin", "httpd.exe"));

    public PhpRuntimeInstallation Resolve(AppSettings? settings = null)
    {
        var phpDirectory = ResolvePhpDirectory(settings?.PhpRuntimePath) ??
                           (IsPhpInstalled ? PhpDirectory : null);
        var apacheRoot = ResolveApacheRoot(settings?.ApacheRuntimePath) ??
                         (IsApacheInstalled ? ApacheDirectory : null);
        return new PhpRuntimeInstallation(
            phpDirectory,
            phpDirectory is null ? null : Path.Combine(phpDirectory, "php.exe"),
            phpDirectory is null ? null : Path.Combine(phpDirectory, "php-cgi.exe"),
            apacheRoot,
            apacheRoot is null ? null : FindHttpd(apacheRoot),
            PhpVersion,
            ApacheVersion);
    }

    public static string? ResolvePhpDirectory(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            var path = configured.Trim().Trim('"');
            if (File.Exists(path) && Path.GetFileName(path).Equals("php.exe", StringComparison.OrdinalIgnoreCase))
                path = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(path)) return null;
            return File.Exists(Path.Combine(path, "php.exe")) ? Path.GetFullPath(path) : null;
        }
        catch { return null; }
    }

    public static string? ResolveApacheRoot(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            var path = configured.Trim().Trim('"');
            if (File.Exists(path) && Path.GetFileName(path).Equals("httpd.exe", StringComparison.OrdinalIgnoreCase))
            {
                var bin = Path.GetDirectoryName(path)!;
                return Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(bin)
                    : bin;
            }
            if (!Directory.Exists(path)) return null;
            if (File.Exists(Path.Combine(path, "bin", "httpd.exe"))) return Path.GetFullPath(path);
            if (File.Exists(Path.Combine(path, "httpd.exe"))) return Path.GetFullPath(path);
            var nested = Path.Combine(path, "Apache24");
            if (File.Exists(Path.Combine(nested, "bin", "httpd.exe"))) return Path.GetFullPath(nested);
            return null;
        }
        catch { return null; }
    }

    public static string? FindHttpd(string apacheRoot)
    {
        var candidate = Path.Combine(apacheRoot, "bin", "httpd.exe");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(apacheRoot, "httpd.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    public async Task<PhpRuntimeInstallation> EnsureInstalledAsync(
        AppSettings? settings,
        IProgress<RuntimeInstallProgress>? progress = null,
        bool includeApache = true,
        CancellationToken cancellationToken = default)
    {
        if (!IsPhpInstalled)
            await InstallPhpAsync(progress, cancellationToken).ConfigureAwait(false);
        if (includeApache && !IsApacheInstalled)
            await InstallApacheAsync(progress, cancellationToken).ConfigureAwait(false);
        WriteManifest(includeApache);
        return Resolve(settings);
    }

    public async Task InstallPhpAsync(IProgress<RuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsPhpInstalled) return;
        var archive = await DownloadAsync(PhpManifest, progress, cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(RootDirectory, "php", ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new RuntimeInstallProgress("PHP", "extract", 0.35, "Mengekstrak PHP…"));
            SafeZipExtractor.ExtractToDirectory(archive, staging);
            RequireFile(staging, "php.exe");
            RequireFile(staging, "php8apache2_4.dll");
            RequireFile(staging, "ext");
            ReplaceDirectory(staging, PhpDirectory);
            progress?.Report(new RuntimeInstallProgress("PHP", "done", 0.5, $"PHP {PhpVersion} siap."));
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    public async Task InstallApacheAsync(IProgress<RuntimeInstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (IsApacheInstalled) return;
        var archive = await DownloadAsync(ApacheManifest, progress, cancellationToken).ConfigureAwait(false);
        var staging = Path.Combine(RootDirectory, "apache", ".staging-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress?.Report(new RuntimeInstallProgress("Apache", "extract", 0.85, "Mengekstrak Apache…"));
            SafeZipExtractor.ExtractToDirectory(archive, staging);
            var runtimeRoot = new[]
                {
                    Path.Combine(staging, "Apache24"),
                    staging
                }
                .FirstOrDefault(path => File.Exists(Path.Combine(path, "bin", "httpd.exe")));
            if (runtimeRoot is null) throw new InvalidDataException("Arsip Apache tidak berisi bin\\httpd.exe.");
            RequireFile(runtimeRoot, "modules\\mod_rewrite.so");
            ReplaceDirectory(runtimeRoot, ApacheDirectory);
            progress?.Report(new RuntimeInstallProgress("Apache", "done", 1.0, $"Apache {ApacheVersion} siap."));
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
        finally
        {
            TryDeleteFile(archive);
        }
    }

    public void RemovePhp()
    {
        TryDeleteDirectory(Path.Combine(RootDirectory, "php"));
    }

    public void RemoveApache()
    {
        TryDeleteDirectory(Path.Combine(RootDirectory, "apache"));
    }

    public void RemoveAll() => TryDeleteDirectory(RootDirectory);

    private async Task<string> DownloadAsync(RuntimeComponentManifest manifest, IProgress<RuntimeInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(RootDirectory, ".cache"));
        var target = Path.Combine(RootDirectory, ".cache", Path.GetFileName(new Uri(manifest.DownloadUrl).AbsolutePath));
        var partial = target + ".partial";
        TryDeleteFile(partial);

        progress?.Report(new RuntimeInstallProgress(manifest.Name, "download", 0.02, $"Mengunduh {manifest.Name} {manifest.Version}…"));
        using (var response = await Http.GetAsync(manifest.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true);
            var buffer = new byte[131072];
            long received = 0;
            var lastReported = 0d;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                if (total is > 0)
                {
                    var fraction = Math.Clamp((double)received / total.Value, 0, 1);
                    if (fraction - lastReported >= 0.01 || fraction >= 1)
                    {
                        lastReported = fraction;
                        progress?.Report(new RuntimeInstallProgress(manifest.Name, "download", fraction * 0.3,
                            $"Mengunduh {manifest.Name}… {fraction * 100:0}% ({received / 1024d / 1024d:0.0} MB)"));
                    }
                }
            }
        }

        progress?.Report(new RuntimeInstallProgress(manifest.Name, "verify", 0.32, "Memverifikasi SHA-256…"));
        var actual = await ComputeSha256Async(partial, cancellationToken).ConfigureAwait(false);
        if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDeleteFile(partial);
            throw new InvalidDataException(
                $"Checksum {manifest.Name} tidak cocok. Unduhan dihapus. Diharapkan {manifest.Sha256}, diterima {actual}.");
        }
        File.Move(partial, target, true);
        return target;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void WriteManifest(bool includeApache)
    {
        Directory.CreateDirectory(RootDirectory);
        var components = new List<RuntimeComponentManifest> { PhpManifest };
        if (includeApache) components.Add(ApacheManifest);
        File.WriteAllText(ManifestPath, JsonConvert.SerializeObject(new
        {
            product = "Depup Code",
            generatedUtc = DateTime.UtcNow,
            installRoot = RootDirectory,
            components
        }, Formatting.Indented));
        File.WriteAllText(NoticePath,
            "Depup Code local runtime\r\n" +
            "=======================\r\n\r\n" +
            "Runtime ini diunduh dari sumber resmi dan diverifikasi SHA-256 oleh Depup Code.\r\n" +
            "Tidak ada installer, service Windows, atau perubahan PATH global.\r\n\r\n" +
            $"PHP {PhpVersion}\r\n  Sumber: {PhpDownloadUrl}\r\n  SHA-256: {PhpSha256}\r\n  Lisensi: {Path.Combine(PhpDirectory, "license.txt")}\r\n\r\n" +
            $"Apache HTTP Server {ApacheVersion} (Apache Lounge Win64 VS17)\r\n  Sumber: {ApacheDownloadUrl}\r\n  SHA-256: {ApacheSha256}\r\n  Lisensi: {Path.Combine(ApacheDirectory, "LICENSE.txt")}\r\n\r\n" +
            "Apache License 2.0: https://www.apache.org/licenses/LICENSE-2.0\r\n" +
            "PHP License 3.01: https://www.php.net/license/3_01.txt\r\n");
    }

    private static void RequireFile(string root, string relative)
    {
        var full = Path.Combine(root, relative);
        if (!File.Exists(full) && !Directory.Exists(full))
            throw new InvalidDataException($"Runtime tidak lengkap: {relative} tidak ditemukan.");
    }

    private static void ReplaceDirectory(string source, string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        Directory.Move(source, destination);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed record PhpRuntimeInstallation(
    string? PhpDirectory,
    string? PhpExe,
    string? PhpCgiExe,
    string? ApacheRoot,
    string? ApacheHttpdExe,
    string PhpVersion,
    string ApacheVersion)
{
    public bool HasPhp => !string.IsNullOrWhiteSpace(PhpExe) && File.Exists(PhpExe);
    public bool HasApache => !string.IsNullOrWhiteSpace(ApacheHttpdExe) && File.Exists(ApacheHttpdExe);
    public bool IsComplete => HasPhp && HasApache;
}

public sealed record RuntimeInstallProgress(string Component, string Stage, double Fraction, string Message);
