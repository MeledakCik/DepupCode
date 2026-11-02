using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Headers;
using System.Text;
using DepupCode.Models;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public sealed class DepupSessionInfo
{
    public bool IsConnected { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Picture { get; set; } = "";
    public bool IsAdmin { get; set; }
    public string BaseUrl { get; set; } = "https://depup.cloud";
    public string Message { get; set; } = "";
}

public sealed class DepupTunnelSession
{
    public string Id { get; set; } = "";
    public string Token { get; set; } = "";
    public string Url { get; set; } = "";
    public string PublicBase { get; set; } = "https://depup.cloud";
    public string ExpiresAt { get; set; } = "";
    public int LocalPort { get; set; }
}

public sealed record DepupCloudUser(string Name, string Email);

/// <summary>
/// Native C# client for depup.cloud (https://github.com/MeledakCik/deployone).
/// Implements /api/auth/session, /api/auth/app-exchange, /api/sites, /api/deploy,
/// and the Link Gaib HTTP long-polling reverse tunnel (/api/tunnel/create, /poll, /respond, /close).
/// </summary>
public sealed class DepupCloudService : IDisposable
{
    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly HttpClient _pollHttp;
    private readonly HttpClient _localHttp;
    private CancellationTokenSource? _tunnelCts;
    private DepupTunnelSession? _activeTunnel;

    public static DepupCloudService Shared { get; } = new(new SettingsService());

    public event EventHandler<string>? TunnelLog;
    public event EventHandler<DepupTunnelSession?>? TunnelStateChanged;

    public DepupTunnelSession? ActiveTunnel => _activeTunnel;
    public bool IsTunnelActive => _activeTunnel is not null && _tunnelCts is { IsCancellationRequested: false };

    public DepupCloudService(SettingsService settings)
    {
        _settings = settings;
        var handler = new HttpClientHandler { UseCookies = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("depup-cli/1.0.0 (DepupCode/2.5.0)");

        var pollHandler = new HttpClientHandler { UseCookies = false };
        _pollHttp = new HttpClient(pollHandler) { Timeout = TimeSpan.FromSeconds(45) };
        _pollHttp.DefaultRequestHeaders.UserAgent.ParseAdd("depup-cli/1.0.0 (DepupCode/2.5.0)");

        var localHandler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None
        };
        _localHttp = new HttpClient(localHandler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    public string GetBaseUrl()
    {
        var raw = _settings.Load().DepupCloudUrl?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "https://depup.cloud";
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            raw = "https://" + raw;
        return raw.TrimEnd('/');
    }

    private void AttachAuth(HttpRequestMessage request, string? explicitToken = null)
    {
        var token = explicitToken ?? _settings.GetToken("depup");
        if (string.IsNullOrWhiteSpace(token)) return;
        token = token.Trim();
        if (token.StartsWith("depup_session=", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("Cookie", token);
            var rawVal = token["depup_session=".Length..].Split(';')[0].Trim();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rawVal);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Cookie", $"depup_session={token}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public async Task<DepupSessionInfo> CheckSessionAsync(string? customToken = null, string? customBaseUrl = null,
        CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(customBaseUrl) ? GetBaseUrl() : customBaseUrl.Trim().TrimEnd('/');
        if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) baseUrl = "https://" + baseUrl;

        var info = new DepupSessionInfo { BaseUrl = baseUrl };
        if (_settings.Load().IsOfflineMode)
        {
            info.Message = "Mode Offline aktif. Data memakai cache lokal; koneksi Depup Cloud dijeda.";
            return info;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/auth/session");
            AttachAuth(req, customToken);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(text))
            {
                var json = JObject.Parse(text);
                var user = json["data"]?["user"] as JObject ?? json["user"] as JObject;
                if (user is not null)
                {
                    info.IsConnected = true;
                    info.Name = (string?)user["name"] ?? "Depup User";
                    info.Email = (string?)user["email"] ?? "";
                    info.Picture = (string?)user["picture"] ?? "";
                    info.IsAdmin = (bool?)user["isAdmin"] ?? false;
                    info.Message = $"Terhubung ke {baseUrl} sebagai {info.Name} ({info.Email})";
                    return info;
                }
            }

            using var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/sites");
            AttachAuth(pingReq, customToken);
            using var pingResp = await _http.SendAsync(pingReq, ct);
            if ((int)pingResp.StatusCode < 500)
            {
                info.IsConnected = true;
                info.Name = string.IsNullOrWhiteSpace(customToken ?? _settings.GetToken("depup")) ? "Guest / Local CLI" : "Authenticated Token";
                info.Message = $"Gateway {baseUrl} aktif dan siap menerima tunnel / deploy.";
                return info;
            }
            info.Message = $"Server merespons dengan HTTP {(int)pingResp.StatusCode}.";
        }
        catch (Exception ex)
        {
            info.Message = $"Tidak dapat menghubungi {baseUrl}: {ex.Message}";
        }
        return info;
    }

    public async Task<DepupSessionInfo> ExchangeAppTokenAsync(string handoffToken, string verifier,
        CancellationToken ct = default)
    {
        var baseUrl = GetBaseUrl();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/auth/app-exchange")
        {
            Content = new StringContent(JsonConvert.SerializeObject(new
            {
                token = handoffToken.Trim(),
                verifier = verifier.Trim()
            }), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"App exchange gagal ({(int)resp.StatusCode}): {ExtractError(body)}");

        string? sessionCookie = null;
        if (resp.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var c in cookies)
            {
                var part = c.Split(';')[0].Trim();
                if (part.StartsWith("depup_session=", StringComparison.OrdinalIgnoreCase))
                {
                    sessionCookie = part["depup_session=".Length..];
                    break;
                }
            }
        }

        var json = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
        sessionCookie ??= (string?)json["data"]?["token"] ?? (string?)json["token"];
        if (!string.IsNullOrWhiteSpace(sessionCookie))
            _settings.SaveToken("depup", sessionCookie);

        var user = json["data"]?["user"] as JObject;
        return new DepupSessionInfo
        {
            IsConnected = true,
            BaseUrl = baseUrl,
            Name = (string?)user?["name"] ?? "Depup User",
            Email = (string?)user?["email"] ?? "",
            Picture = (string?)user?["picture"] ?? "",
            Message = "Sesi Depup Cloud berhasil ditautkan."
        };
    }

    public async Task<List<ProjectRecord>> ListSitesAsync(CancellationToken ct = default)
    {
        EnsureOnlineMode();
        if (await GetSessionUserAsync(ct) is null)
            throw new InvalidOperationException("Masuk ke akun Depup Cloud untuk memuat project dan Link Instan.");

        var list = new List<ProjectRecord>();
        var data = await GetDataAsync("/api/sites", ct);
        foreach (var item in (data["sites"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var siteName = (string?)item["name"] ?? (string?)item["siteName"] ?? (string?)item["id"];
            if (string.IsNullOrWhiteSpace(siteName)) continue;
            var url = (string?)item["url"] ?? "";
            var updated = ReadCloudDate(item["createdAt"]);
            list.Add(new ProjectRecord
            {
                Name = siteName,
                Platform = "Depup Edge CDN",
                PlatformSub = "Link Instan",
                Framework = "Static / Edge Bundle",
                SiteUrl = url,
                ProviderProjectId = (string?)item["id"] ?? siteName,
                Status = "Aktif",
                IconKind = "bolt",
                Author = "Depup Cloud",
                UpdatedAt = updated
            });
        }
        return list;
    }

    public async Task<DeploymentResult> DeployToDepupCloudAsync(RepoAnalysis repo, string projectName,
        IReadOnlyCollection<EnvironmentVariable> envVars,
        IProgress<(int Percent, string Message)>? progress = null, CancellationToken ct = default)
    {
        EnsureOnlineMode();
        var baseUrl = GetBaseUrl();
        var safeName = Slug(projectName);
        progress?.Report((20, $"Menghubungi gateway {baseUrl}…"));

        var staticDirectory = ResolveStaticSiteDirectory(repo);
        if (staticDirectory is not null)
            return await UploadStaticSiteAsync(repo, staticDirectory, safeName, baseUrl, progress, ct);

        if (!string.IsNullOrWhiteSpace(repo.RepositoryUrl) &&
            repo.RepositoryUrl.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report((45, $"Mengirim pipeline '{safeName}' ke Depup Cloud…"));
            return await DeployAsync(repo, "vercel", safeName, envVars, progress, ct);
        }

        throw new InvalidOperationException(
            "Tidak ada hasil build website statis berisi index.html untuk Depup Edge, dan project ini belum terhubung ke repository GitHub. " +
            "Jalankan build lalu pilih folder hasil build, atau sambungkan repository GitHub.");
    }

    private async Task<DeploymentResult> UploadStaticSiteAsync(RepoAnalysis repo, string directory, string name, string baseUrl,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        const int defaultChunkBytes = 512 * 1024;
        const long clientMaxBytes = 50L * 1024 * 1024;
        const int clientMaxFiles = 500;

        var sitesInfo = await GetDataAsync("/api/sites", ct);
        if (sitesInfo["enabled"]?.Value<bool>() == false)
            throw new InvalidOperationException("Hosting Link Instan sedang dinonaktifkan di Depup Cloud.");
        var maxBytes = Math.Clamp(sitesInfo["maxBytes"]?.Value<long>() ?? 10L * 1024 * 1024, 1L, clientMaxBytes);
        var maxFiles = Math.Clamp(sitesInfo["maxFiles"]?.Value<int>() ?? clientMaxFiles, 1, clientMaxFiles);
        var chunkBytes = Math.Clamp(sitesInfo["chunkBytes"]?.Value<int>() ?? defaultChunkBytes, 1, 4 * 1024 * 1024);

        var files = ReadStaticFiles(directory, maxBytes, maxFiles);
        if (!files.Any(f => f.Path.Equals("index.html", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Folder hasil build harus memiliki index.html di root.");

        var totalBytes = files.Sum(f => (long)f.Content.Length);
        progress?.Report((58, $"Mendaftarkan {files.Count} file ({FormatBytes(totalBytes)}) ke Depup Edge…"));
        var createPayload = new JObject
        {
            ["name"] = name,
            ["files"] = new JArray(files.Select(f => new JObject { ["path"] = f.Path, ["size"] = f.Content.Length }))
        };
        var created = await SendCloudJsonAsync(HttpMethod.Post, $"{baseUrl}/api/sites", createPayload, ct);
        var id = (string?)created["id"];
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Depup Cloud tidak mengembalikan ID site.");

        var chunkCount = Math.Max(1, (int)Math.Ceiling(totalBytes / (double)chunkBytes));
        var combined = new byte[checked((int)totalBytes)];
        var offset = 0;
        foreach (var file in files)
        {
            Buffer.BlockCopy(file.Content, 0, combined, offset, file.Content.Length);
            offset += file.Content.Length;
        }
        for (var index = 0; index < chunkCount; index++)
        {
            EnsureOnlineMode();
            ct.ThrowIfCancellationRequested();
            var start = index * chunkBytes;
            var length = Math.Min(chunkBytes, combined.Length - start);
            using var content = new ByteArrayContent(combined, start, length);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/api/sites/{Uri.EscapeDataString(id)}/chunk?i={index}") { Content = content };
            AttachAuth(request);
            using var response = await _http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Gagal mengunggah potongan {index + 1}/{chunkCount}: {ApiErrorMessage(body, response.StatusCode)}");
            progress?.Report((60 + (int)(30.0 * (index + 1) / chunkCount), $"Mengunggah file ke Depup Edge ({index + 1}/{chunkCount})…"));
        }

        EnsureOnlineMode();
        progress?.Report((94, "Mengaktifkan link Depup Edge…"));
        var finished = await SendCloudJsonAsync(HttpMethod.Post, $"{baseUrl}/api/sites/{Uri.EscapeDataString(id)}/finish", new JObject(), ct);
        var url = (string?)finished["url"] ?? (string?)created["url"];
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Depup Cloud tidak mengembalikan URL site.");
        var historyMessage = "";
        var repository = repo.RepositoryUrl.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)
            ? repo.RepositoryUrl
            : "";
        try { await SaveHistoryAsync(name, "Depup Edge CDN", url, "ready", repository, id, ct); }
        catch { historyMessage = " Riwayat akun belum tersinkron."; }
        progress?.Report((100, $"Site Depup Edge aktif di {url}."));
        return new DeploymentResult
        {
            Id = id,
            ProviderProjectId = id,
            Url = url,
            Status = "Aktif",
            Message = $"Depup Edge CDN aktif di {url}.{historyMessage}"
        };
    }

    private async Task<JObject> SendCloudJsonAsync(HttpMethod method, string url, JObject payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        AttachAuth(request);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ApiErrorMessage(body, response.StatusCode));
        try
        {
            var json = JObject.Parse(body);
            return json["data"] as JObject ?? json;
        }
        catch (JsonException ex) { throw new InvalidOperationException("Respons Depup Cloud tidak valid.", ex); }
    }

    private static string ApiErrorMessage(string body, HttpStatusCode statusCode)
    {
        try
        {
            var json = JObject.Parse(body);
            return (string?)json["error"] ?? (string?)json["message"] ?? $"HTTP {(int)statusCode} ({statusCode}).";
        }
        catch { return $"HTTP {(int)statusCode} ({statusCode})."; }
    }

    private sealed record StaticSiteFile(string Path, byte[] Content);

    private static List<StaticSiteFile> ReadStaticFiles(string directory, long maxBytes, int maxFiles)
    {
        var ignoredDirectories = new HashSet<string>([".git", "node_modules", ".next", ".vercel", "bin", "obj", "vendor", "coverage"], StringComparer.OrdinalIgnoreCase);
        var files = new List<StaticSiteFile>();
        long totalBytes = 0;
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(directory));
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var subdirectory in Directory.EnumerateDirectories(current))
            {
                var info = new DirectoryInfo(subdirectory);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || ignoredDirectories.Contains(info.Name)) continue;
                pending.Push(subdirectory);
            }
            foreach (var path in Directory.EnumerateFiles(current))
            {
                var info = new FileInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || IsEnvironmentFile(info.Name)) continue;
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                if (relative.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or "..")) continue;
                if (relative.Equals("index.html", StringComparison.OrdinalIgnoreCase)) relative = "index.html";
                if (files.Count >= maxFiles) throw new InvalidOperationException($"Site melewati batas {maxFiles} file Depup Edge.");
                totalBytes += info.Length;
                if (totalBytes > maxBytes) throw new InvalidOperationException($"Ukuran website melewati batas Depup Edge ({FormatBytes(maxBytes)}).");
                files.Add(new StaticSiteFile(relative, File.ReadAllBytes(path)));
            }
        }
        return files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsEnvironmentFile(string fileName) =>
        fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveStaticSiteDirectory(RepoAnalysis repo)
    {
        if (string.IsNullOrWhiteSpace(repo.LocalSourcePath) || !Directory.Exists(repo.LocalSourcePath)) return null;
        var projectRoot = Path.GetFullPath(repo.LocalSourcePath);
        var candidates = new List<string>();
        foreach (var name in new[] { "dist", "build", "out", "www", "_site" }) candidates.Add(Path.Combine(projectRoot, name));
        if (!string.IsNullOrWhiteSpace(repo.StaticDirectory)) candidates.Add(Path.Combine(projectRoot, repo.StaticDirectory.Replace('/', Path.DirectorySeparatorChar)));
        if (repo.Kind == ProjectKind.Static) candidates.Add(projectRoot);
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        return null;
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024.0 / 1024.0:0.#} MB"
        : $"{bytes / 1024.0:0.#} KB";

    // =====================================================================
    //  Akun Depup Cloud (login Google PKCE, sinkronisasi env/token, riwayat deploy)
    //  Digabungkan dari build Codex. Semua panggilan di blok ini memakai cookie depup_session saja
    //  (persis perilaku Codex); panggilan lama Argon tetap memakai AttachAuth.
    // =====================================================================

    private const string SessionCookieName = "depup_session";

    public async Task<DepupCloudUser?> GetSessionUserAsync(CancellationToken cancellationToken = default)
    {
        var cookie = _settings.GetDepupSessionCookie();
        if (string.IsNullOrWhiteSpace(cookie)) return null;
        var data = await GetDataAsync("/api/auth/session", cancellationToken);
        var user = data["user"] as JObject;
        var email = (string?)user?["email"];
        if (string.IsNullOrWhiteSpace(email)) return null; // jangan hapus token: bisa jadi token manual jenis lain
        if (!string.Equals(_settings.Load().DepupEmail, email, StringComparison.OrdinalIgnoreCase))
            _settings.SaveDepupSession(cookie, email);
        return new DepupCloudUser((string?)user?["name"] ?? email, email);
    }

    /// <summary>Memulai login Google (PKCE) di browser. Hasilnya kembali lewat tautan depup://auth.</summary>
    public void StartLogin()
    {
        EnsureOnlineMode();
        var baseUrl = GetBaseUrl();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        RegisterProtocolHandler();
        _settings.SaveDepupLoginVerifier(verifier);
        var loginUrl = $"{baseUrl}/api/auth/google?app=1&challenge={Uri.EscapeDataString(challenge)}";
        Process.Start(new ProcessStartInfo(loginUrl) { UseShellExecute = true });
    }

    /// <summary>Menyelesaikan handoff depup://auth dan menyimpan cookie sesi (DPAPI).</summary>
    public async Task CompleteLoginAsync(string deepLink, CancellationToken cancellationToken = default)
    {
        EnsureOnlineMode();
        if (!Uri.TryCreate(deepLink, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("depup", StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("auth", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tautan login Depup tidak dikenali.");

        var query = ParseQuery(uri.Query);
        if (query.TryGetValue("error", out var authError) && !string.IsNullOrWhiteSpace(authError))
        {
            _settings.SaveDepupLoginVerifier("");
            throw new InvalidOperationException(authError);
        }
        if (!query.TryGetValue("token", out var handoff) || string.IsNullOrWhiteSpace(handoff))
            throw new InvalidOperationException("Tautan login tidak membawa token. Silakan coba masuk lagi.");

        var verifier = _settings.GetDepupLoginVerifier();
        if (string.IsNullOrWhiteSpace(verifier))
            throw new InvalidOperationException("Permintaan login sudah kedaluwarsa. Silakan mulai login lagi.");

        var baseUrl = GetBaseUrl();
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("depup-cli/1.0.0 (DepupCode/2.5.0)");
        var payload = new JObject { ["token"] = handoff, ["verifier"] = verifier };
        using var content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(baseUrl + "/api/auth/app-exchange", content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        _settings.SaveDepupLoginVerifier("");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ReadApiError(body, response.StatusCode));

        var cookie = cookies.GetCookies(new Uri(baseUrl))[SessionCookieName]?.Value;
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException("Depup Cloud tidak mengirim sesi login. Coba masuk lagi.");

        _settings.SaveDepupSession(cookie, "");
        var user = await GetSessionUserAsync(cancellationToken);
        if (user is null)
        {
            _settings.ClearDepupSession();
            throw new InvalidOperationException("Sesi berhasil dibuat, tetapi akun Depup tidak dapat dibaca.");
        }
    }

    public void DisconnectThisDevice() => _settings.ClearDepupSession();

    public void OpenDashboard(string view = "dashboard")
    {
        var url = GetBaseUrl() + "/dashboard" + (view == "dashboard" ? "" : "?view=" + Uri.EscapeDataString(view));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public Task<JObject> GetUserDataAsync(CancellationToken cancellationToken = default) =>
        GetDataAsync("/api/user-data", cancellationToken);

    public async Task<List<DomainRecord>> ListDomainsAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        return (data["domains"] as JArray ?? new JArray()).OfType<JObject>().Select(item => new DomainRecord
        {
            Id = (string?)item["id"] ?? Guid.NewGuid().ToString("N"),
            Domain = (string?)item["domain"] ?? "",
            Project = (string?)item["project"] ?? "",
            Status = (string?)item["status"] ?? "Pending",
            SyncedToVercel = (bool?)item["syncedToVercel"] ?? false,
            SyncedToCloudflare = (bool?)item["syncedToCloudflare"] ?? false,
            SyncedToRailway = (bool?)item["syncedToRailway"] ?? false,
            UpdatedAt = ReadCloudDate(item["updatedAt"])
        }).Where(item => !string.IsNullOrWhiteSpace(item.Domain)).ToList();
    }

    public async Task SaveDomainAsync(DomainRecord domain, CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var domains = (data["domains"] as JArray)?.DeepClone() as JArray ?? new JArray();
        var existing = domains.OfType<JObject>().FirstOrDefault(item =>
            string.Equals((string?)item["id"], domain.Id, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals((string?)item["domain"], domain.Domain, StringComparison.OrdinalIgnoreCase) &&
             string.Equals((string?)item["project"], domain.Project, StringComparison.OrdinalIgnoreCase)));
        var record = new JObject
        {
            ["id"] = (string?)existing?["id"] ?? domain.Id,
            ["domain"] = domain.Domain.Trim(),
            ["project"] = domain.Project.Trim(),
            ["status"] = domain.Status is "Active" ? "Active" : "Pending",
            ["syncedToVercel"] = domain.SyncedToVercel || (bool?)existing?["syncedToVercel"] == true,
            ["syncedToCloudflare"] = domain.SyncedToCloudflare || (bool?)existing?["syncedToCloudflare"] == true,
            ["syncedToRailway"] = domain.SyncedToRailway || (bool?)existing?["syncedToRailway"] == true
        };
        if (existing is null) domains.Insert(0, record);
        else
        {
            var index = domains.IndexOf(existing);
            domains[index] = record;
        }
        await PutDataAsync(new JObject { ["domains"] = domains }, cancellationToken);
    }

    public async Task<JObject> GetCloudSettingsTokensAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        return (data["settingsTokens"] as JObject) ?? new JObject();
    }

    public async Task SyncSettingsToCloudAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var tokens = (data["settingsTokens"] as JObject)?.DeepClone() as JObject ?? new JObject();
        var local = _settings.Load();
        SetIfPresent(tokens, "vercelToken", _settings.GetToken("vercel"));
        SetIfPresent(tokens, "cloudflareToken", _settings.GetToken("cloudflare"));
        SetIfPresent(tokens, "cloudflareAccountId", local.CloudflareAccountId);
        SetIfPresent(tokens, "githubPat", _settings.GetToken("github"));
        SetIfPresent(tokens, "railwayToken", _settings.GetToken("railway"));
        await PutDataAsync(new JObject { ["settingsTokens"] = tokens }, cancellationToken);
    }

    public async Task ImportSettingsFromCloudAsync(CancellationToken cancellationToken = default)
    {
        var tokens = await GetCloudSettingsTokensAsync(cancellationToken);
        if (tokens.Property("vercelToken") is not null) _settings.SaveToken("vercel", (string?)tokens["vercelToken"] ?? "");
        if (tokens.Property("cloudflareToken") is not null) _settings.SaveToken("cloudflare", (string?)tokens["cloudflareToken"] ?? "");
        if (tokens.Property("githubPat") is not null) _settings.SaveToken("github", (string?)tokens["githubPat"] ?? "");
        if (tokens.Property("railwayToken") is not null) _settings.SaveToken("railway", (string?)tokens["railwayToken"] ?? "");
        var settings = _settings.Load();
        if (tokens.Property("cloudflareAccountId") is not null)
            settings.CloudflareAccountId = (string?)tokens["cloudflareAccountId"] ?? settings.CloudflareAccountId;
        _settings.Save(settings);
    }

    /// <summary>Token provider lokal dulu; kalau kosong, ambil dari akun Depup Cloud.</summary>
    public async Task<string> GetProviderTokenAsync(string provider, CancellationToken cancellationToken = default)
    {
        var local = _settings.GetToken(provider);
        if (!string.IsNullOrWhiteSpace(local)) return local;
        var cloud = await GetCloudSettingsTokensAsync(cancellationToken);
        var key = provider.ToLowerInvariant() switch
        {
            "github" => "githubPat",
            "cloudflare" => "cloudflareToken",
            _ => provider.ToLowerInvariant() + "Token"
        };
        return (string?)cloud[key] ?? "";
    }

    public async Task<List<EnvironmentVariable>> LoadEnvironmentAsync(string projectName,
        CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var result = new List<EnvironmentVariable>();
        foreach (var item in (data["envVars"] as JArray ?? new JArray()).OfType<JObject>())
        {
            if (!string.Equals((string?)item["project"], projectName, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new EnvironmentVariable
            {
                Name = (string?)item["key"] ?? "",
                Value = (string?)item["value"] ?? "",
                Target = (string?)item["environment"] ?? "Production",
                IsSecret = !((bool?)item["visible"] ?? false)
            });
        }
        return result;
    }

    public async Task SaveEnvironmentAsync(string projectName, IEnumerable<EnvironmentVariable> variables,
        CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var entries = (data["envVars"] as JArray)?.DeepClone() as JArray ?? new JArray();
        var retained = entries.OfType<JObject>()
            .Where(x => !string.Equals((string?)x["project"], projectName, StringComparison.OrdinalIgnoreCase))
            .Select(x => (JObject)x.DeepClone()).ToList();
        foreach (var variable in variables)
        {
            var old = entries.OfType<JObject>().FirstOrDefault(x =>
                string.Equals((string?)x["project"], projectName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)x["key"], variable.Name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)x["environment"], variable.Target, StringComparison.OrdinalIgnoreCase));
            retained.Add(new JObject
            {
                ["id"] = (string?)old?["id"] ?? Guid.NewGuid().ToString("N"),
                ["key"] = variable.Name,
                ["value"] = variable.Value,
                ["environment"] = variable.Target.Equals("Preview", StringComparison.OrdinalIgnoreCase) ? "Preview" : "Production",
                ["visible"] = !variable.IsSecret,
                ["project"] = projectName,
                ["syncedToVercel"] = (bool?)old?["syncedToVercel"] ?? false,
                ["syncedToCloudflare"] = (bool?)old?["syncedToCloudflare"] ?? false,
                ["syncedToRailway"] = (bool?)old?["syncedToRailway"] ?? false
            });
        }
        await PutDataAsync(new JObject { ["envVars"] = new JArray(retained) }, cancellationToken);
    }

    /// <summary>Riwayat deploy yang tersimpan di akun Depup Cloud (/api/user-data → history).</summary>
    public async Task<List<ProjectRecord>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var result = new List<ProjectRecord>();
        foreach (var item in (data["history"] as JArray ?? new JArray()).OfType<JObject>())
        {
            var repo = (string?)item["repo"] ?? "";
            var url = (string?)item["domain"] ?? "";
            var status = NormalizeStatus((string?)item["status"] ?? "");
            var building = status == "Building";
            var date = DateTime.TryParse((string?)item["date"], out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;
            result.Add(new ProjectRecord
            {
                Name = (string?)item["name"] ?? "Depup project",
                RepositoryUrl = repo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? repo :
                    repo.Contains('/') ? "https://github.com/" + repo.Trim('/') : "",
                Platform = NormalizePlatform((string?)item["platform"] ?? "Depup Cloud"),
                PlatformSub = "Riwayat akun Depup Cloud",
                Framework = "",
                CommitHash = "",
                Author = "Depup Cloud",
                SiteUrl = url,
                Status = status,
                BuildProgress = building ? 60 : 100,
                IconKind = "cloud",
                UpdatedAt = date
            });
        }
        return result;
    }

    /// <summary>
    /// Deploy lewat akun Depup Cloud ke Vercel / Cloudflare Pages / Railway: validasi repo, mulai build,
    /// polling status sampai selesai, lalu simpan riwayat ke akun. Token provider diambil dari lokal atau dari cloud.
    /// </summary>
    public async Task<DeploymentResult> DeployAsync(RepoAnalysis repo, string platform, string projectName,
        IReadOnlyCollection<EnvironmentVariable> localEnvironment,
        IProgress<(int Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(repo.RepositoryUrl))
            throw new InvalidOperationException(
                "Rute Depup Cloud membutuhkan repository GitHub. Upload project ke GitHub dulu (menu Upload) atau pilih Depup Edge.");

        var platformKey = platform.ToLowerInvariant() switch
        {
            "cloudflare" or "cloudflare pages" => "cloudflare",
            "railway" => "railway",
            _ => "vercel"
        };
        var providerToken = await GetProviderTokenAsync(platformKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(providerToken))
            throw new InvalidOperationException($"Token {platformKey} belum ada di Settings atau akun Depup Cloud.");
        var githubToken = await GetProviderTokenAsync("github", cancellationToken);
        var accountId = _settings.Load().CloudflareAccountId;
        if (platformKey == "cloudflare" && string.IsNullOrWhiteSpace(accountId))
        {
            var cloudSettings = await GetCloudSettingsTokensAsync(cancellationToken);
            accountId = (string?)cloudSettings["cloudflareAccountId"] ?? "";
        }
        if (platformKey == "cloudflare" && string.IsNullOrWhiteSpace(accountId))
            throw new InvalidOperationException("Cloudflare Account ID belum disimpan di Settings Depup.");

        var env = localEnvironment.ToList();
        if (env.Count == 0)
            env = await LoadEnvironmentAsync(projectName, cancellationToken);
        var envText = ToEnvText(env);
        var body = new JObject
        {
            ["projectName"] = projectName,
            ["githubUrl"] = repo.RepositoryUrl,
            ["githubPat"] = githubToken,
            ["envText"] = envText
        };
        string createPath;
        switch (platformKey)
        {
            case "cloudflare":
                body["cloudflareToken"] = providerToken;
                body["accountId"] = accountId;
                createPath = "/api/cloudflare/deploy";
                break;
            case "railway":
                body["railwayToken"] = providerToken;
                createPath = "/api/railway/deploy";
                break;
            default:
                body["vercelToken"] = providerToken;
                createPath = "/api/deploy";
                break;
        }

        var didStart = false;
        try
        {
            progress?.Report((18, "Depup Cloud memeriksa repository dan kredensial…"));
            var created = await PostDataAsync(createPath, body, cancellationToken);
            didStart = true;
            var deploymentId = (string?)created["deploymentId"] ?? (string?)created["id"] ?? "";
            var url = (string?)created["url"] ?? (string?)created["domain"] ?? "";
            if (string.IsNullOrWhiteSpace(deploymentId))
                throw new InvalidOperationException("Deploy diterima, tetapi Depup Cloud tidak mengembalikan ID deployment.");
            var state = ((string?)created["readyState"] ?? "QUEUED").ToUpperInvariant();
            progress?.Report((42, "Build dimulai di " + platform + "…"));

            for (var attempt = 0; attempt < 120 && state is not ("READY" or "ERROR" or "CANCELED"); attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                var pollPath = platformKey switch
                {
                    "cloudflare" => $"/api/cloudflare/deploy/{Uri.EscapeDataString(deploymentId)}?project={Uri.EscapeDataString(projectName)}&accountId={Uri.EscapeDataString(accountId)}",
                    "railway" => "/api/railway/deploy/" + Uri.EscapeDataString(deploymentId),
                    _ => "/api/deploy/" + Uri.EscapeDataString(deploymentId)
                };
                var headers = new Dictionary<string, string>
                {
                    ["x-" + platformKey + "-token"] = providerToken
                };
                var status = await GetDataAsync(pollPath, cancellationToken, headers);
                state = ((string?)status["readyState"] ?? state).ToUpperInvariant();
                url = (string?)status["url"] ?? url;
                var percent = state switch { "BUILDING" or "QUEUED" or "INITIALIZING" => 66, "READY" => 100, _ => 78 };
                progress?.Report((percent, StatusMessage(state, platform)));
                if (state is "ERROR" or "CANCELED")
                    throw new InvalidOperationException((string?)status["errorMessage"] ?? $"Build {state.ToLowerInvariant()} di {platform}.");
            }

            var stillBuilding = state is not "READY";
            if (stillBuilding)
                progress?.Report((90, "Build masih berjalan. URL dan status dapat disegarkan dari Projects."));
            else
                progress?.Report((100, "Deploy berhasil. Menyimpan riwayat ke akun Depup Cloud…"));

            var finalStatus = stillBuilding ? "building" : "ready";
            var historyMessage = "";
            try
            {
                await SaveHistoryAsync(projectName, platformKey, url, finalStatus,
                    repo.Owner + "/" + repo.Repository, deploymentId, cancellationToken);
            }
            catch { historyMessage = " Riwayat akun belum tersinkron; proyek tetap berhasil dikirim."; }
            return new DeploymentResult
            {
                Id = deploymentId,
                Url = url,
                Status = stillBuilding ? "Building" : "Ready",
                Message = (stillBuilding ? "Deploy diterima dan build masih berjalan di " + platform + "." : "Deploy berhasil melalui Depup Cloud.") + historyMessage
            };
        }
        catch
        {
            if (didStart)
            {
                try { await SaveHistoryAsync(projectName, platformKey, "", "error", repo.Owner + "/" + repo.Repository, "", CancellationToken.None); }
                catch { }
            }
            throw;
        }
    }

    private async Task SaveHistoryAsync(string name, string platform, string url, string status, string repo,
        string id, CancellationToken cancellationToken)
    {
        var data = await GetUserDataAsync(cancellationToken);
        var history = (data["history"] as JArray)?.DeepClone() as JArray ?? new JArray();
        history.Insert(0, new JObject
        {
            ["id"] = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
            ["name"] = name,
            ["platform"] = platform,
            ["domain"] = url,
            ["date"] = DateTime.UtcNow.ToString("O"),
            ["status"] = status,
            ["repo"] = repo
        });
        if (history.Count > 200) history = new JArray(history.Take(200));
        await PutDataAsync(new JObject { ["history"] = history }, cancellationToken);
    }

    // ---------- HTTP helper khusus endpoint akun (cookie saja) ----------

    private async Task<JObject> GetDataAsync(string path, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        EnsureOnlineMode();
        using var request = CreateCloudRequest(HttpMethod.Get, path);
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadDataAsync(response, cancellationToken);
    }

    private async Task<JObject> PostDataAsync(string path, JObject body, CancellationToken cancellationToken)
    {
        EnsureOnlineMode();
        using var request = CreateCloudRequest(HttpMethod.Post, path);
        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadDataAsync(response, cancellationToken);
    }

    private async Task<JObject> PutDataAsync(JObject body, CancellationToken cancellationToken)
    {
        EnsureOnlineMode();
        using var request = CreateCloudRequest(HttpMethod.Put, "/api/user-data");
        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadDataAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateCloudRequest(HttpMethod method, string path)
    {
        var cookie = _settings.GetDepupSessionCookie();
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException("Hubungkan akun Depup Cloud dari Settings terlebih dahulu.");
        var request = new HttpRequestMessage(method, GetBaseUrl() + path);
        request.Headers.TryAddWithoutValidation("Cookie", SessionCookieName + "=" + cookie);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        return request;
    }

    private void EnsureOnlineMode()
    {
        if (_settings.Load().IsOfflineMode)
            throw new InvalidOperationException("Mode Offline aktif. Aksi cloud dinonaktifkan; ubah ke Mode Online untuk melanjutkan.");
    }

    private static DateTime ReadCloudDate(JToken? value)
    {
        if (value is null) return DateTime.UtcNow;
        if (value.Type == JTokenType.Integer)
        {
            var milliseconds = value.Value<long>();
            try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds).UtcDateTime; }
            catch { return DateTime.UtcNow; }
        }
        return DateTime.TryParse(value.ToString(), out var parsed) ? parsed.ToUniversalTime() : DateTime.UtcNow;
    }

    private static async Task<JObject> ReadDataAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        JObject root;
        try { root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text); }
        catch { throw new HttpRequestException($"Depup Cloud mengembalikan respons yang tidak valid (HTTP {(int)response.StatusCode})."); }
        if (!response.IsSuccessStatusCode || (bool?)root["ok"] == false)
            throw new HttpRequestException(ReadApiError(text, response.StatusCode));
        return root["data"] as JObject ?? root;
    }

    private static string ReadApiError(string body, HttpStatusCode status)
    {
        try
        {
            var root = JObject.Parse(body);
            return (string?)root["error"] ?? (string?)root["message"] ?? $"Permintaan Depup Cloud gagal (HTTP {(int)status}).";
        }
        catch { return $"Permintaan Depup Cloud gagal (HTTP {(int)status})."; }
    }

    private static string ToEnvText(IEnumerable<EnvironmentVariable> values)
    {
        var selected = values.Where(v => v.Target.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                                         v.Target.Equals("Production", StringComparison.OrdinalIgnoreCase));
        return string.Join("\n", selected.Select(v => v.Name + "=" + QuoteEnv(v.Value)));
    }

    private static string QuoteEnv(string value) => value.Contains('\n') || value.Contains('\r') || value.Contains('#') || value.Contains('"')
        ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\""
        : value;

    private static string StatusMessage(string state, string platform) => state switch
    {
        "QUEUED" or "INITIALIZING" => "Menunggu antrean build " + platform + "…",
        "BUILDING" => "Build sedang berjalan di " + platform + "…",
        "READY" => "Build berhasil di " + platform + ".",
        "ERROR" or "CANCELED" => "Build " + state.ToLowerInvariant() + " di " + platform + ".",
        _ => "Memeriksa status deployment di " + platform + "…"
    };

    private static string NormalizePlatform(string platform) => platform.ToLowerInvariant() switch
    {
        "cloudflare" or "cloudflare pages" => "Cloudflare Pages",
        "railway" => "Railway",
        "vercel" => "Vercel",
        _ => platform
    };

    /// <summary>Memetakan status riwayat cloud ke istilah yang sudah dipakai UI Projects (Building / Gagal Deploy / ...).</summary>
    private static string NormalizeStatus(string status) => status.ToLowerInvariant() switch
    {
        "ready" or "success" => "Deployed",
        "error" or "failed" => "Gagal Deploy",
        "building" or "deploying" or "queued" => "Building",
        _ => "Tersinkronkan"
    };

    private static void SetIfPresent(JObject target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
    }

    private void RegisterProtocolHandler()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("Lokasi Depup Code tidak dapat ditemukan.");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\depup", true)
            ?? throw new InvalidOperationException("Windows tidak mengizinkan pendaftaran tautan login Depup.");
        var existing = key.OpenSubKey("shell\\open\\command")?.GetValue(null)?.ToString();
        var expected = $"\"{executable}\" \"%1\"";
        if (!string.IsNullOrWhiteSpace(existing) && !existing.Contains("DepupCode", StringComparison.OrdinalIgnoreCase) &&
            !existing.Contains(executable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Tautan depup:// sudah dipakai aplikasi lain. Pengaturan Windows tidak diubah.");
        key.SetValue(null, "URL:Depup Code login");
        key.SetValue("URL Protocol", "");
        using var command = key.CreateSubKey(@"shell\open\command", true);
        command?.SetValue(null, expected);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            var key = split < 0 ? part : part[..split];
            var value = split < 0 ? "" : part[(split + 1)..];
            result[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        return result;
    }

    public async Task<DepupTunnelSession> StartTunnelAsync(int port, string? customName = null, int ttlMinutes = 120,
        CancellationToken ct = default)
    {
        EnsureOnlineMode();
        await StopTunnelAsync();

        var baseUrl = GetBaseUrl();
        EmitTunnelLog($"[Link Gaib] Menghubungi {baseUrl}/api/tunnel/create untuk localhost:{port}…");

        var createPayload = new JObject { ["port"] = port, ["ttlMinutes"] = ttlMinutes };
        if (!string.IsNullOrWhiteSpace(customName))
            createPayload["name"] = Slug(customName);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/tunnel/create")
        {
            Content = new StringContent(createPayload.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        AttachAuth(req);

        using var resp = await _http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Tunnel gagal dibuat (HTTP {(int)resp.StatusCode}): {ExtractError(text)}");

        var json = JObject.Parse(text);
        if (json["ok"]?.Value<bool>() == false)
            throw new InvalidOperationException(ExtractError(text));

        var data = json["data"] as JObject ?? json;
        var session = new DepupTunnelSession
        {
            Id = (string?)data["id"] ?? throw new InvalidOperationException("Server tidak mengembalikan tunnel ID."),
            Token = (string?)data["token"] ?? "",
            PublicBase = (string?)data["publicBase"] ?? baseUrl,
            ExpiresAt = (string?)data["expiresAt"] ?? DateTime.UtcNow.AddMinutes(ttlMinutes).ToString("O"),
            Url = (string?)data["url"] ?? $"{baseUrl}/m/{(string?)data["id"]}",
            LocalPort = port
        };

        _activeTunnel = session;
        _tunnelCts = new CancellationTokenSource();
        TunnelStateChanged?.Invoke(this, session);
        EmitTunnelLog($"✓ Link Gaib aktif! URL Publik: {session.Url}");
        EmitTunnelLog($"  Tunnel ID : {session.Id}  |  Target: http://localhost:{port}  |  Expires: {session.ExpiresAt}");

        _ = Task.Run(() => RunPollLoopAsync(baseUrl, session, _tunnelCts.Token));
        return session;
    }

    private async Task RunPollLoopAsync(string baseUrl, DepupTunnelSession session, CancellationToken ct)
    {
        var hot = 0;
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pollUrl = $"{baseUrl}/api/tunnel/poll?id={Uri.EscapeDataString(session.Id)}&hot={hot}";
                using var req = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                if (!string.IsNullOrWhiteSpace(session.Token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);

                using var resp = await _pollHttp.SendAsync(req, ct);
                if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Unauthorized)
                {
                    EmitTunnelLog($"[Link Gaib] Sesi tunnel berakhir di server (HTTP {(int)resp.StatusCode}).");
                    break;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                consecutiveErrors = 0;
                if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(body))
                {
                    hot = 0;
                    await Task.Delay(600, ct);
                    continue;
                }

                var parsed = JToken.Parse(body);
                var items = parsed is JArray arr
                    ? arr
                    : (parsed["data"] as JArray ?? parsed["requests"] as JArray ?? new JArray());

                if (items.Count == 0)
                {
                    hot = 0;
                    continue;
                }

                hot = 1;
                foreach (var item in items.OfType<JObject>())
                {
                    _ = Task.Run(() => HandleTunnelRequestAsync(baseUrl, session, item, ct), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                hot = 0;
                if (consecutiveErrors <= 3)
                    EmitTunnelLog($"[Link Gaib] Reconnecting poll ({ex.Message})…");
                await Task.Delay(Math.Min(5000, 800 * consecutiveErrors), ct);
            }
        }

        if (_activeTunnel == session)
        {
            _activeTunnel = null;
            TunnelStateChanged?.Invoke(this, null);
        }
    }

    private async Task HandleTunnelRequestAsync(string baseUrl, DepupTunnelSession session, JObject reqItem, CancellationToken ct)
    {
        var rid = (string?)reqItem["rid"] ?? (string?)reqItem["id"] ?? "";
        if (string.IsNullOrWhiteSpace(rid)) return;
        var method = (string?)reqItem["method"] ?? "GET";
        var path = (string?)reqItem["path"] ?? "/";
        if (!path.StartsWith('/')) path = "/" + path;
        var reqBody = (string?)reqItem["body"] ?? "";

        var localUrl = $"http://localhost:{session.LocalPort}{path}";
        var status = 502;
        var respHeaders = new JObject();
        var respBody = "";

        try
        {
            using var localReq = new HttpRequestMessage(new HttpMethod(method), localUrl);
            if (reqItem["headers"] is JObject headersObj)
            {
                foreach (var prop in headersObj.Properties())
                {
                    if (prop.Name.Equals("host", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("content-length", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var val = prop.Value.ToString();
                    if (!localReq.Headers.TryAddWithoutValidation(prop.Name, val))
                    {
                        localReq.Content ??= new StringContent(reqBody);
                        localReq.Content.Headers.TryAddWithoutValidation(prop.Name, val);
                    }
                }
            }

            if (!string.IsNullOrEmpty(reqBody) && method is not ("GET" or "HEAD"))
            {
                var contentType = (string?)reqItem["headers"]?["content-type"] ?? "application/json";
                localReq.Content = new StringContent(reqBody, Encoding.UTF8);
                localReq.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var mt)
                    ? mt
                    : new MediaTypeHeaderValue("text/plain");
            }

            using var localResp = await _localHttp.SendAsync(localReq, ct);
            status = (int)localResp.StatusCode;
            foreach (var h in localResp.Headers)
                respHeaders[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            foreach (var h in localResp.Content.Headers)
                respHeaders[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);

            respBody = await localResp.Content.ReadAsStringAsync(ct);
            EmitTunnelLog($"  {method} {path} → {status} ({respBody.Length} B)");
        }
        catch (Exception ex)
        {
            status = 502;
            respHeaders["content-type"] = "text/plain; charset=utf-8";
            respBody = $"Link Gaib Bad Gateway: localhost:{session.LocalPort} tidak merespons ({ex.Message})";
            EmitTunnelLog($"  {method} {path} → 502 Bad Gateway ({ex.Message})");
        }

        try
        {
            var respondUrl = $"{baseUrl}/api/tunnel/respond?id={Uri.EscapeDataString(session.Id)}";
            var respondPayload = new JObject
            {
                ["rid"] = rid,
                ["seq"] = 0,
                ["last"] = true,
                ["status"] = status,
                ["headers"] = respHeaders,
                ["body"] = respBody
            };
            using var respondReq = new HttpRequestMessage(HttpMethod.Post, respondUrl)
            {
                Content = new StringContent(respondPayload.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(session.Token))
                respondReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
            using var _ = await _http.SendAsync(respondReq, ct);
        }
        catch { }
    }

    public async Task StopTunnelAsync()
    {
        var session = _activeTunnel;
        var cts = _tunnelCts;
        _activeTunnel = null;
        _tunnelCts = null;

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        if (session is not null)
        {
            TunnelStateChanged?.Invoke(this, null);
            if (_settings.Load().IsOfflineMode)
            {
                EmitTunnelLog($"[Link Gaib] Koneksi lokal tunnel {session.Id} dihentikan. Permintaan close ke cloud dijeda oleh Mode Offline.");
                return;
            }
            try
            {
                var baseUrl = GetBaseUrl();
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    $"{baseUrl}/api/tunnel/close?id={Uri.EscapeDataString(session.Id)}");
                if (!string.IsNullOrWhiteSpace(session.Token))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Token);
                using var _ = await _http.SendAsync(req);
                EmitTunnelLog($"[Link Gaib] Tunnel {session.Id} ditutup dengan aman.");
            }
            catch { }
        }
    }

    private void EmitTunnelLog(string message) => TunnelLog?.Invoke(this, message);

    private static string ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "Unknown error";
        try
        {
            var json = JObject.Parse(body);
            return (string?)json["error"] ?? (string?)json["message"] ?? body;
        }
        catch { return body.Length > 200 ? body[..200] : body; }
    }

    private static string Slug(string value)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');
        return slug.Length == 0 ? "depup-app" : slug[..Math.Min(slug.Length, 48)];
    }

    public void Dispose()
    {
        try { _tunnelCts?.Cancel(); } catch { }
        _http.Dispose();
        _pollHttp.Dispose();
        _localHttp.Dispose();
    }
}
