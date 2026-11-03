using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DepupCode.Models;

namespace DepupCode.Services;

/// <summary>Preset provider OpenAI-compatible yang tampil di pengaturan AI Agent.</summary>
public sealed record AiProviderPreset(
    string Id, string DisplayName, string BaseUrl, string DefaultModel,
    bool RequiresKey, string Note, string KeyUrl);

/// <summary>
/// Katalog provider AI, resolusi provider aktif, serta helper jaringan (muat daftar model dan tes koneksi).
/// Semua provider di sini memakai format OpenAI Chat Completions. ID model dan kuota gratis berubah dari waktu
/// ke waktu, jadi default hanyalah saran; pengguna dapat memuat daftar model langsung dari provider.
/// </summary>
public static class AiProviderCatalog
{
    public const string CustomIdPrefix = "custom-";
    public const string ManualPresetId = "manual";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static IReadOnlyList<AiProviderPreset> Presets { get; } =
    [
        new("openrouter", "OpenRouter (ada model gratis)", "https://openrouter.ai/api/v1", "openrouter/free", true,
            "Model 'openrouter/free' atau berakhiran ':free' tidak dikenai biaya, tetapi ada batas pemakaian dan dukungan tool bervariasi. " +
            "Prompt pada model gratis dapat dicatat provider; jangan kirim kode rahasia. Buat API key gratis di akun OpenRouter.",
            "https://openrouter.ai/keys"),
        new("groq", "Groq (tier gratis)", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true,
            "Groq menyediakan tier gratis dengan batas rate. Daftar model aktif berubah; tekan 'Muat daftar model' untuk melihat model yang tersedia di akun Anda.",
            "https://console.groq.com/keys"),
        new("github", "GitHub Models (token GitHub)", "https://models.github.ai/inference", "openai/gpt-4.1-mini", true,
            "Memakai token GitHub (PAT) dengan izin Models. Ada batas pemakaian gratis; periksa dokumentasi GitHub Models.",
            "https://github.com/settings/personal-access-tokens"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true,
            "Mistral menyediakan tier percobaan dengan batas; periksa konsol Mistral.",
            "https://console.mistral.ai/api-keys"),
        new("ollama", "Ollama (lokal, gratis)", "http://localhost:11434/v1", "qwen2.5-coder:7b", false,
            "Berjalan di komputer Anda tanpa API key dan tanpa mengirim prompt ke internet. Jalankan Ollama dan unduh model dulu (mis. `ollama pull qwen2.5-coder:7b`). " +
            "Mode Agent membutuhkan model yang mendukung tool-calling.",
            "https://ollama.com/download"),
        new("lmstudio", "LM Studio (lokal, gratis)", "http://localhost:1234/v1", "local-model", false,
            "Aktifkan Local Server di LM Studio, lalu tekan 'Muat daftar model' untuk memilih model yang sedang dimuat.",
            "https://lmstudio.ai"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini", true,
            "Berbayar sesuai pemakaian.", "https://platform.openai.com/api-keys"),
        new(ManualPresetId, "Lainnya / manual (OpenAI-compatible)", "", "", true,
            "Isi URL API dan nama model sendiri untuk provider mana pun yang kompatibel dengan OpenAI Chat Completions.", ""),
    ];

    public static AiProviderPreset? FindPreset(string? id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static AiCustomProvider? FindCustom(AppSettings settings, string? id) =>
        (settings.AiCustomProviders ?? []).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ID provider yang sedang aktif. Pengaturan lama (tanpa AiPresetId) dicocokkan lewat URL API supaya
    /// pengguna yang sudah memakai Groq/OpenAI/Ollama tidak perlu mengatur ulang.
    /// </summary>
    public static string ResolveCurrentId(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AiPresetId)) return settings.AiPresetId.Trim().ToLowerInvariant();
        var url = NormalizeBaseUrl(settings.AiApiBaseUrl);
        var match = Presets.FirstOrDefault(p => p.BaseUrl.Length > 0 &&
            string.Equals(NormalizeBaseUrl(p.BaseUrl), url, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? ManualPresetId;
    }

    public static string DisplayNameFor(AppSettings settings)
    {
        if (string.Equals(settings.AiProvider, "Gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini";
        if (string.Equals(settings.AiProvider, "Copilot", StringComparison.OrdinalIgnoreCase)) return "Copilot";
        var id = ResolveCurrentId(settings);
        return FindCustom(settings, id)?.Name ?? FindPreset(id)?.DisplayName.Split(" (")[0] ?? "OpenAI-compatible";
    }

    public static string KeySlot(string presetId) => "ai:" + presetId.Trim().ToLowerInvariant();

    public static string NewCustomId(string name, IEnumerable<string> existingIds)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "provider";
        if (slug.Length > 32) slug = slug[..32].TrimEnd('-');
        var baseId = CustomIdPrefix + slug;
        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++) id = baseId + "-" + n;
        return id;
    }

    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var value = (baseUrl ?? "").Trim().TrimEnd('/');
        foreach (var suffix in new[] { "/chat/completions", "/models" })
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) value = value[..^suffix.Length].TrimEnd('/');
        return value;
    }

    public static string BuildChatEndpoint(string? baseUrl) => NormalizeBaseUrl(baseUrl) + "/chat/completions";

    public static string BuildModelsEndpoint(string? baseUrl) => NormalizeBaseUrl(baseUrl) + "/models";

    public static bool IsLocalEndpoint(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>Mengambil ID model dari respons GET /models ({"data":[{"id":"..."}]}).</summary>
    public static List<string> ParseModelIds(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? data
            : root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array ? models
            : default;
        if (array.ValueKind != JsonValueKind.Array) return [];

        var ids = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            string? id = item.ValueKind == JsonValueKind.String ? item.GetString()
                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            // Gemini mengembalikan "models/gemini-2.5-flash"; request chat memakai nama tanpa prefix.
            if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];
            ids.Add(id.Trim());
        }
        return ids.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string ExtractErrorMessage(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? fallback;
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.String)
                    return message.GetString() ?? fallback;
            }
        }
        catch (JsonException) { }
        return fallback;
    }

    /// <summary>Heuristik: apakah error provider berarti model tidak mendukung tool/function calling.</summary>
    public static bool LooksLikeToolsUnsupported(int statusCode, string detail)
    {
        if (statusCode is not (400 or 404 or 422)) return false;
        var text = detail.ToLowerInvariant();
        return text.Contains("tool") || text.Contains("function calling") || text.Contains("function_call");
    }

    public static async Task<List<string>> FetchModelIdsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        var url = BuildModelsEndpoint(baseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("URL API tidak valid.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {ExtractErrorMessage(body, response.ReasonPhrase ?? "gagal")}");
        try { return ParseModelIds(body); }
        catch (JsonException) { throw new InvalidOperationException("Respons daftar model bukan JSON yang dikenali."); }
    }

    /// <summary>Tes nyata: kirim satu pesan sederhana ke provider dan kembalikan balasannya.</summary>
    public static async Task<string> TestChatAsync(string? baseUrl, string? apiKey, string model, CancellationToken cancellationToken = default)
    {
        var url = BuildChatEndpoint(baseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new InvalidOperationException("URL API tidak valid.");
        if (string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("Nama model belum diisi.");
        var payload = JsonSerializer.Serialize(new
        {
            model = model.Trim(),
            messages = new[] { new { role = "user", content = "Balas hanya dengan: DEPUP AI OK" } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await Http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {ExtractErrorMessage(body, response.ReasonPhrase ?? "gagal")}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            return content.ValueKind == JsonValueKind.String ? content.GetString()?.Trim() ?? "" : "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new InvalidOperationException("Provider membalas, tetapi format respons bukan OpenAI Chat Completions.");
        }
    }
}
