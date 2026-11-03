using DepupCode.Models;
using Newtonsoft.Json;
using System.Net.Http;

namespace DepupCode.Services;

public sealed class SettingsService
{
    public const string PrivacyPolicyVersion = "2026-11-03";

#if DEBUG
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepupCode-Development", "settings.json");
#else
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DepupCode", "settings.json");
#endif

    public string SettingsDirectory => Path.GetDirectoryName(_path)!;

    public string SettingsPath => _path;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
                MigrateLegacy(loaded);
                return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    /// <summary>
    /// Upgrade-safe: settings.json dari build Codex (DepupBaseUrl, DepupSessionCookie, DepupToken, RecentFolders)
    /// dipetakan ke field yang dipakai versi gabungan. Nilai rahasia memakai SecretStore yang sama (DPAPI + entropy
    /// yang sama), jadi bisa disalin apa adanya tanpa dekripsi ulang. Hanya mengisi field kosong; tidak menimpa data baru.
    /// </summary>
    private static void MigrateLegacy(AppSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.AppTheme))
            s.AppTheme = string.IsNullOrWhiteSpace(s.EditorColorTheme) ? "Depup Dark" : s.EditorColorTheme;

        var defaultUrl = "https://depup.cloud";
        if (!string.IsNullOrWhiteSpace(s.DepupBaseUrl) &&
            (string.IsNullOrWhiteSpace(s.DepupCloudUrl) || s.DepupCloudUrl == defaultUrl) &&
            !string.Equals(s.DepupBaseUrl.TrimEnd('/'), defaultUrl, StringComparison.OrdinalIgnoreCase))
            s.DepupCloudUrl = s.DepupBaseUrl;

        if (string.IsNullOrWhiteSpace(s.DepupSessionToken))
        {
            if (!string.IsNullOrWhiteSpace(s.DepupSessionCookie)) s.DepupSessionToken = s.DepupSessionCookie;
            else if (!string.IsNullOrWhiteSpace(s.DepupToken)) s.DepupSessionToken = s.DepupToken;
        }

        if (s.RecentWorkspaces.Count == 0 && s.RecentFolders is { Count: > 0 })
        {
            foreach (var folder in s.RecentFolders.Where(f => !string.IsNullOrWhiteSpace(f)).Take(12))
            {
                try
                {
                    var full = Path.GetFullPath(folder);
                    s.RecentWorkspaces.Add(new RecentWorkspaceItem
                    {
                        Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        FullPath = full,
                        Framework = "Workspace",
                        LastOpenedUtc = DateTime.UtcNow
                    });
                }
                catch { }
            }
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(settings, Formatting.Indented));
        File.Move(temp, _path, true);
    }

    public void AddRecentWorkspace(string folder, string framework = "Next.js")
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var s = Load();
        var full = Path.GetFullPath(folder);
        var name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        s.RecentWorkspaces.RemoveAll(w => string.Equals(w.FullPath, full, StringComparison.OrdinalIgnoreCase) ||
                                          string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        s.RecentWorkspaces.Insert(0, new RecentWorkspaceItem
        {
            Name = name,
            FullPath = full,
            Framework = string.IsNullOrWhiteSpace(framework) || framework == "Unknown" ? "Workspace" : framework,
            LastOpenedUtc = DateTime.UtcNow
        });
        if (s.RecentWorkspaces.Count > 12)
            s.RecentWorkspaces = s.RecentWorkspaces.Take(12).ToList();
        s.LastFolder = full;
        Save(s);
    }

    public string GetToken(string provider)
    {
        var s = Load();
        return SecretStore.Unprotect(provider.ToLowerInvariant() switch
        {
            "depup" or "depupcloud" => s.DepupSessionToken,
            "github" => s.GitHubToken,
            "vercel" => s.VercelToken,
            "cloudflare" => s.CloudflareToken,
            "railway" => s.RailwayToken,
            "gemini" or "google" => s.AiGeminiApiKey,
            "ai" or "openai" => s.AiApiKey,
            _ => ""
        });
    }

    public void SaveToken(string provider, string value)
    {
        var s = Load();
        var encrypted = SecretStore.Protect(value.Trim());
        switch (provider.ToLowerInvariant())
        {
            case "depup":
            case "depupcloud":
                s.DepupSessionToken = encrypted;
                s.DepupSessionCookie = "";
                s.DepupToken = "";
                break;
            case "github": s.GitHubToken = encrypted; break;
            case "vercel": s.VercelToken = encrypted; break;
            case "cloudflare": s.CloudflareToken = encrypted; break;
            case "railway": s.RailwayToken = encrypted; break;
            case "gemini":
            case "google": s.AiGeminiApiKey = encrypted; break;
            case "ai":
            case "openai": s.AiApiKey = encrypted; break;
        }
        Save(s);
    }

    // ------------------------------------------------------------------
    // Sesi Depup Cloud (login Google). Disimpan di slot token "depup" yang sama dengan
    // tempel-token manual, supaya satu mekanisme auth dipakai oleh seluruh DepupCloudService.
    // ------------------------------------------------------------------

    /// <summary>Nilai cookie depup_session saja (tanpa prefix "depup_session=" dan atribut ;).</summary>
    public string GetDepupSessionCookie()
    {
        var raw = GetToken("depup").Trim();
        if (raw.Length == 0) return "";
        if (raw.StartsWith("depup_session=", StringComparison.OrdinalIgnoreCase))
            raw = raw["depup_session=".Length..];
        var semi = raw.IndexOf(';');
        return (semi >= 0 ? raw[..semi] : raw).Trim();
    }

    public string GetDepupLoginVerifier() => SecretStore.Unprotect(Load().DepupLoginVerifier);

    public void SaveDepupLoginVerifier(string verifier)
    {
        var s = Load();
        s.DepupLoginVerifier = SecretStore.Protect(verifier);
        Save(s);
    }

    public void SaveDepupSession(string cookie, string email)
    {
        var s = Load();
        s.DepupSessionToken = SecretStore.Protect(cookie.Trim());
        if (!string.IsNullOrWhiteSpace(email)) s.DepupEmail = email.Trim();
        s.DepupLoginVerifier = "";
        // Bersihkan field lama supaya MigrateLegacy tidak menghidupkan kembali sesi yang sudah dihapus.
        s.DepupSessionCookie = "";
        s.DepupToken = "";
        Save(s);
    }

    public void ClearDepupSession()
    {
        var s = Load();
        s.DepupSessionToken = "";
        s.DepupSessionCookie = "";
        s.DepupToken = "";
        s.DepupLoginVerifier = "";
        s.DepupEmail = "";
        Save(s);
    }

    public async Task<bool> TestConnectionAsync(string provider, string token, string? accountId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOnlineMode();
        if (provider.Equals("depup", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("depupcloud", StringComparison.OrdinalIgnoreCase))
        {
            var depup = new DepupCloudService(this);
            var info = await depup.CheckSessionAsync(token, accountId, cancellationToken);
            return info.IsConnected;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        switch (provider.ToLowerInvariant())
        {
            case "github":
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DepupCode/2.5.0");
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return (await http.GetAsync("https://api.github.com/user", cancellationToken)).IsSuccessStatusCode;
            case "vercel":
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return (await http.GetAsync("https://api.vercel.com/v2/user", cancellationToken)).IsSuccessStatusCode;
            case "cloudflare":
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                return (await http.GetAsync("https://api.cloudflare.com/client/v4/user/tokens/verify", cancellationToken)).IsSuccessStatusCode;
            case "railway":
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/graphql/v2"))
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Content = new StringContent("{\"query\":\"query { me { id } }\"}", System.Text.Encoding.UTF8, "application/json");
                    return (await http.SendAsync(request, cancellationToken)).IsSuccessStatusCode;
                }
            default:
                return false;
        }
    }

    public void EnsureOnlineMode()
    {
        if (Load().IsOfflineMode)
            throw new InvalidOperationException("Mode Offline aktif. Aksi jaringan dinonaktifkan; ubah ke Mode Online untuk melanjutkan.");
    }
}
