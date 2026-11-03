namespace DepupCode.Models;

public sealed class RecentWorkspaceItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public string Framework { get; set; } = "Next.js";
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AiCustomProvider
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
}

public sealed class AppSettings
{
    public string DepupCloudUrl { get; set; } = "https://depup.cloud";
    public string DepupSessionToken { get; set; } = "";
    public string DepupLoginVerifier { get; set; } = "";
    public string DepupEmail { get; set; } = "";
    public string GitHubToken { get; set; } = "";
    public string GitHubUsername { get; set; } = "";
    public string VercelToken { get; set; } = "";
    public string CloudflareToken { get; set; } = "";
    public string CloudflareAccountId { get; set; } = "";
    public string RailwayToken { get; set; } = "";
    public string AiApiKey { get; set; } = "";
    public string AiGeminiApiKey { get; set; } = "";
    public string AiProvider { get; set; } = "OpenAI";
    // ID preset (mis. "groq", "ollama") atau provider manual ("custom-..."); kosong = pengaturan lama, dicocokkan lewat URL.
    public string AiPresetId { get; set; } = "";
    // API key per provider OpenAI-compatible (terenkripsi DPAPI), supaya berpindah provider tidak menimpa key lain.
    public Dictionary<string, string> AiProviderKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AiCustomProvider> AiCustomProviders { get; set; } = [];
    public string AiApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AiModel { get; set; } = "gpt-4.1-mini";
    public string AiLanguage { get; set; } = "Indonesian";
    public List<AiCustomAgent> AiCustomAgents { get; set; } = [];
    public string LastFolder { get; set; } = "";
    public string DefaultProjectsPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Projects");
    public string DefaultPlatform { get; set; } = "Railway";
    public bool WordWrap { get; set; }
    public bool ShowMinimap { get; set; } = true;
    public bool AutoOpenHtmlPreviewOnServerStart { get; set; } = true;
    public double EditorFontSize { get; set; } = 13;
    public int FormatterIndentSize { get; set; } = 2;
    public bool FormatterUseTabs { get; set; }
    public double UiZoomScale { get; set; } = 1.0;
    public double SidebarWidth { get; set; } = 268;
    public string AppTheme { get; set; } = "";
    public bool HasAcceptedPrivacyPolicy { get; set; }
    public string AcceptedPrivacyPolicyVersion { get; set; } = "";
    public bool HasCompletedFirstRunSetup { get; set; }
    // Kept so older settings.json files and prior app builds remain compatible.
    public string EditorColorTheme { get; set; } = "Depup Dark";
    public bool EnableSourceControl { get; set; } = true;
    public bool EnableDeployFeature { get; set; } = true;
    public bool EnableDomainsFeature { get; set; } = true;
    public bool EnableEnvironmentFeature { get; set; } = true;
    public bool EnableLinkGaibFeature { get; set; } = true;
    public bool IsOfflineMode { get; set; }

    // ---- Local PHP/Apache server (tanpa XAMPP) ----
    public int LocalServerPortStart { get; set; } = 8000;
    public int LocalServerPortEnd { get; set; } = 8099;
    public string PhpRuntimePath { get; set; } = "";
    public string ApacheRuntimePath { get; set; } = "";
    public string LocalServerPhpVersion { get; set; } = "8.4.27";
    public bool LocalServerUsePhpFallback { get; set; } = true;
    public Dictionary<string, string> LocalServerDocumentRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<RecentWorkspaceItem> RecentWorkspaces { get; set; } = [];

    // ---- Field lama dari build "Codex". Hanya dibaca saat upgrade (dimigrasikan di SettingsService.Load),
    // ---- tidak dipakai lagi oleh kode baru. Dibiarkan supaya settings.json lama tidak kehilangan sesi/URL.
    public string DepupBaseUrl { get; set; } = "";
    public string DepupSessionCookie { get; set; } = "";
    public string DepupToken { get; set; } = "";
    public List<string> RecentFolders { get; set; } = [];
}
