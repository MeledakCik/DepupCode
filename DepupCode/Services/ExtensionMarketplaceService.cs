using System.IO.Compression;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using DepupCode.Models;

namespace DepupCode.Services;

public sealed record ExtensionMarketplaceItem(
    string Namespace,
    string Name,
    string DisplayName,
    string Description,
    string Version,
    string Publisher,
    long DownloadCount,
    string? IconUrl,
    string? DownloadUrl,
    IReadOnlyList<string> Categories,
    bool IsInstalled = false,
    bool HasActivationEntryPoint = false)
{
    public string Identifier => $"{Namespace}.{Name}";
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    public string IconText
    {
        get
        {
            var words = Title.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 1) return $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}";
            var compact = new string(Title.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return compact.ToUpperInvariant();
        }
    }
    public string DownloadsText => DownloadCount >= 1_000_000
        ? $"{DownloadCount / 1_000_000d:0.#}M downloads"
        : DownloadCount >= 1_000 ? $"{DownloadCount / 1_000d:0.#}K downloads" : $"{DownloadCount} downloads";
    public bool HasNativeCategory => Categories.Any(category =>
        category.Equals("Themes", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("Snippets", StringComparison.OrdinalIgnoreCase));
}

public sealed record ExtensionTheme(string Name, string ExtensionId, IReadOnlyDictionary<string, string> Palette);

public sealed record ExtensionSnippet(string Prefix, string Body, string Description, IReadOnlyList<string> Languages);

/// <summary>
/// Connects to the vendor-neutral Open VSX registry and safely installs VSIX contents.
/// </summary>
public sealed class ExtensionMarketplaceService
{
    private const string Registry = "https://open-vsx.org";
    private static readonly HttpClient Http = CreateClient();
    private readonly string _extensionsRoot = Path.Combine(new SettingsService().SettingsDirectory, "extensions");

    public static string ExtensionsRoot => Path.Combine(new SettingsService().SettingsDirectory, "extensions");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DepupCode/2.5 (Open VSX extension browser)");
        return client;
    }

    public async Task<IReadOnlyList<ExtensionMarketplaceItem>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        query = string.IsNullOrWhiteSpace(query) ? "themes" : query.Trim();
        var url = $"{Registry}/api/-/search?query={Uri.EscapeDataString(query)}&size=36&sortBy=downloadCount&sortOrder=desc";
        using var response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = JObject.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var installed = GetInstalled().ToDictionary(item => item.Identifier, StringComparer.OrdinalIgnoreCase);
        var installedIds = installed.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (payload["extensions"] as JArray ?? [])
            .OfType<JObject>()
            .Select(item => ParseSearchItem(item, installedIds))
            .Where(item => item is not null)
            .Cast<ExtensionMarketplaceItem>()
            .Select(item => installed.TryGetValue(item.Identifier, out var local)
                ? item with { IsInstalled = true, HasActivationEntryPoint = local.HasActivationEntryPoint, Categories = local.Categories }
                : item)
            .ToArray();
    }

    public IReadOnlyList<ExtensionMarketplaceItem> GetInstalled()
    {
        if (!Directory.Exists(_extensionsRoot)) return [];
        var results = new List<ExtensionMarketplaceItem>();
        foreach (var folder in Directory.EnumerateDirectories(_extensionsRoot))
        {
            try
            {
                var manifestPath = Path.Combine(folder, "package.json");
                if (!File.Exists(manifestPath)) continue;
                var manifest = JObject.Parse(File.ReadAllText(manifestPath));
                var metadataPath = Path.Combine(folder, ".depup-marketplace.json");
                var metadata = File.Exists(metadataPath) ? JObject.Parse(File.ReadAllText(metadataPath)) : new JObject();
                var ns = StringValue(metadata["namespace"], StringValue(manifest["publisher"], "unknown"));
                var name = StringValue(metadata["name"], StringValue(manifest["name"], Path.GetFileName(folder)));
                var categories = (manifest["categories"] as JArray ?? [])
                    .Values<string>()
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var contributions = manifest["contributes"] as JObject;
                if (contributions?["themes"] is JArray { Count: > 0 }) categories.Add("Themes");
                if (contributions?["snippets"] is JArray { Count: > 0 }) categories.Add("Snippets");
                var activationPath = SafeContributionPath(StringValue(manifest["main"], ""));
                var hasActivation = activationPath is not null && File.Exists(ResolveInside(folder, activationPath));
                if (hasActivation) categories.Add("Activation");
                results.Add(new ExtensionMarketplaceItem(
                    ns, name,
                    StringValue(metadata["displayName"], StringValue(manifest["displayName"], name)),
                    StringValue(metadata["description"], StringValue(manifest["description"], "")),
                    StringValue(metadata["version"], StringValue(manifest["version"], "")),
                    StringValue(metadata["publisher"], ns),
                    LongValue(metadata["downloadCount"]), null, null,
                    categories.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray(), true, hasActivation));
            }
            catch { }
        }
        return results.OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ExtensionMarketplaceItem> InstallAsync(ExtensionMarketplaceItem item, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(item.DownloadUrl) ||
            !Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            downloadUri.Scheme != Uri.UriSchemeHttps || !downloadUri.Host.Equals("open-vsx.org", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Alamat paket extension dari Open VSX tidak valid.");

        using var response = await Http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 150 * 1024 * 1024)
            throw new InvalidOperationException("Paket extension melebihi batas 150 MB.");
        var packageBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (packageBytes.Length > 150 * 1024 * 1024)
            throw new InvalidOperationException("Paket extension melebihi batas 150 MB.");

        using var memory = new MemoryStream(packageBytes);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read);
        var packageEntry = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals("extension/package.json", StringComparison.OrdinalIgnoreCase));
        if (packageEntry is null)
            throw new InvalidDataException("Manifest extension tidak ditemukan di dalam paket VSIX.");

        JObject manifest;
        await using (var stream = packageEntry.Open())
        using (var reader = new StreamReader(stream))
            manifest = JObject.Parse(await reader.ReadToEndAsync(cancellationToken));

        var contributions = manifest["contributes"] as JObject ?? new JObject();
        var relativeFiles = GetDeclarativeContributionFiles(contributions);
        var activationPath = SafeContributionPath(StringValue(manifest["main"], ""));
        var hasActivation = activationPath is not null && archive.GetEntry("extension/" + activationPath.Replace('\\', '/')) is not null;
        if (relativeFiles.Count == 0 && !hasActivation)
            throw new NotSupportedException("Extension ini tidak memiliki tema, snippets, atau entry point Node.js yang didukung. Extension browser-only belum didukung.");

        var destination = Path.Combine(_extensionsRoot, SafeSegment(item.Namespace) + "." + SafeSegment(item.Name));
        var staging = destination + ".installing";
        var installedHasActivation = false;
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            long extractedBytes = 0;
            var files = archive.Entries
                .Where(entry => entry.FullName.StartsWith("extension/", StringComparison.OrdinalIgnoreCase))
                .Where(entry => !entry.FullName.EndsWith('/'))
                .ToArray();
            if (files.Length > 20_000)
                throw new InvalidDataException("Paket extension memiliki terlalu banyak file (maksimum 20.000).");
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long actualExtractedBytes = 0;

            foreach (var entry in files)
            {
                var relative = entry.FullName["extension/".Length..];
                var safeRelative = SafeContributionPath(relative);
                if (safeRelative is null || !seenPaths.Add(safeRelative))
                    throw new InvalidDataException("Paket extension mengandung jalur file yang tidak valid.");
                var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (fileType == 0xA000)
                    throw new InvalidDataException("Paket extension mengandung symbolic link yang tidak didukung.");
                if (entry.Length > 80 * 1024 * 1024 || extractedBytes + entry.Length > 300 * 1024 * 1024)
                    throw new InvalidDataException("Isi paket extension melebihi batas aman (maksimum 300 MB).");
                extractedBytes += entry.Length;
                var target = ResolveInside(staging, safeRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = File.Create(target);
                var buffer = new byte[81920];
                long fileBytes = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    fileBytes += read;
                    actualExtractedBytes += read;
                    if (fileBytes > 80 * 1024 * 1024 || actualExtractedBytes > 300 * 1024 * 1024)
                        throw new InvalidDataException("Isi extension melebihi batas ukuran aman.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            if (!File.Exists(Path.Combine(staging, "package.json")))
                throw new InvalidDataException("Manifest extension tidak dapat dipasang.");
            var extractedManifest = JObject.Parse(await File.ReadAllTextAsync(Path.Combine(staging, "package.json"), cancellationToken));
            var extractedActivation = SafeContributionPath(StringValue(extractedManifest["main"], ""));
            var extractedHasActivation = extractedActivation is not null && File.Exists(ResolveInside(staging, extractedActivation));
            installedHasActivation = extractedHasActivation;
            if (GetDeclarativeContributionFiles(extractedManifest["contributes"] as JObject ?? new JObject()).Count == 0 && !extractedHasActivation)
                throw new NotSupportedException("Paket ini tidak membawa tema, snippets, atau entry point Node.js yang didukung.");

            var metadata = new JObject
            {
                ["namespace"] = item.Namespace,
                ["name"] = item.Name,
                ["displayName"] = item.Title,
                ["description"] = item.Description,
                ["version"] = item.Version,
                ["publisher"] = item.Publisher,
                ["downloadCount"] = item.DownloadCount
            };
            await File.WriteAllTextAsync(Path.Combine(staging, ".depup-marketplace.json"), metadata.ToString(), cancellationToken);
            Directory.CreateDirectory(_extensionsRoot);
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            Directory.Move(staging, destination);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }

        return item with { IsInstalled = true, HasActivationEntryPoint = installedHasActivation };
    }

    public void Uninstall(ExtensionMarketplaceItem item)
    {
        var destination = Path.Combine(_extensionsRoot, SafeSegment(item.Namespace) + "." + SafeSegment(item.Name));
        if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
    }

    public string GetInstalledDirectory(ExtensionMarketplaceItem item) =>
        Path.Combine(_extensionsRoot, SafeSegment(item.Namespace) + "." + SafeSegment(item.Name));

    public IReadOnlyList<ExtensionTheme> LoadInstalledThemes()
    {
        UiThemePalette.ClearCustomThemes();
        var themes = new List<ExtensionTheme>();
        foreach (var folder in InstalledPackageFolders())
        {
            try
            {
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
                var metadataPath = Path.Combine(folder, ".depup-marketplace.json");
                var metadata = File.Exists(metadataPath) ? JObject.Parse(File.ReadAllText(metadataPath)) : new JObject();
                var extensionId = $"{StringValue(metadata["namespace"], StringValue(manifest["publisher"], "extension"))}.{StringValue(metadata["name"], StringValue(manifest["name"], "package"))}";
                foreach (var contribution in (manifest["contributes"]?["themes"] as JArray ?? []).OfType<JObject>())
                {
                    var label = StringValue(contribution["label"], "Extension Theme");
                    var path = SafeContributionPath(StringValue(contribution["path"], ""));
                    if (path is null) continue;
                    var themePath = ResolveInside(folder, path);
                    if (!File.Exists(themePath)) continue;
                    var themeJson = JObject.Parse(File.ReadAllText(themePath));
                    var palette = MapThemePalette(themeJson["colors"] as JObject);
                    if (palette.Count == 0) continue;
                    var uniqueName = $"{label} · {extensionId}";
                    var syntax = MapSyntaxColors(themeJson["tokenColors"] as JArray);
                    UiThemePalette.RegisterCustomTheme(uniqueName, palette, syntax);
                    themes.Add(new ExtensionTheme(uniqueName, extensionId, palette));
                }
            }
            catch { }
        }
        return themes;
    }

    public IReadOnlyList<ExtensionSnippet> LoadInstalledSnippets()
    {
        var snippets = new List<ExtensionSnippet>();
        foreach (var folder in InstalledPackageFolders())
        {
            try
            {
                var manifest = JObject.Parse(File.ReadAllText(Path.Combine(folder, "package.json")));
                foreach (var entry in (manifest["contributes"]?["snippets"] as JArray ?? []))
                {
                    var pathValue = entry.Type == JTokenType.String ? entry.Value<string>() : StringValue(entry["path"], "");
                    var path = SafeContributionPath(pathValue ?? "");
                    if (path is null) continue;
                    var fullPath = ResolveInside(folder, path);
                    if (!File.Exists(fullPath)) continue;
                    var snippetInfo = entry as JObject;
                    var languageToken = snippetInfo?["language"];
                    var languages = (languageToken?.Type == JTokenType.Array
                            ? languageToken.Values<string>()
                            : new[] { StringValue(languageToken, "") })
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray();
                    var document = JObject.Parse(File.ReadAllText(fullPath));
                    foreach (var pair in document.Properties())
                    {
                        var definition = pair.Value as JObject;
                        if (definition is null) continue;
                        var prefix = definition["prefix"];
                        var prefixes = prefix?.Type == JTokenType.Array
                            ? prefix.Values<string>().Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
                            : new[] { prefix?.Value<string>() ?? "" };
                        var bodyToken = definition["body"];
                        var body = bodyToken?.Type == JTokenType.Array
                            ? string.Join(Environment.NewLine, bodyToken.Values<string>())
                            : bodyToken?.Value<string>() ?? "";
                        foreach (var value in prefixes.Where(value => value.Length > 0))
                            snippets.Add(new ExtensionSnippet(value, body, StringValue(definition["description"], pair.Name), languages));
                    }
                }
            }
            catch { }
        }
        return snippets;
    }

    public static void RegisterInstalledThemes()
    {
        _ = new ExtensionMarketplaceService().LoadInstalledThemes();
    }

    private IEnumerable<string> InstalledPackageFolders() =>
        Directory.Exists(_extensionsRoot) ? Directory.EnumerateDirectories(_extensionsRoot).Where(folder => File.Exists(Path.Combine(folder, "package.json"))) : [];

    private static ExtensionMarketplaceItem? ParseSearchItem(JObject item, ISet<string> installed)
    {
        var ns = StringValue(item["namespace"], "");
        var name = StringValue(item["name"], "");
        if (ns.Length == 0 || name.Length == 0) return null;
        var files = item["files"] as JObject;
        var publisher = StringValue(item["publishedBy"]?["displayName"], ns);
        var categories = (item["categories"] as JArray ?? []).Values<string>().Where(value => value is not null).Cast<string>().ToArray();
        return new ExtensionMarketplaceItem(
            ns, name, StringValue(item["displayName"], name), StringValue(item["description"], ""),
            StringValue(item["version"], ""), publisher, LongValue(item["downloadCount"]),
            StringValue(files?["icon"], "") is { Length: > 0 } icon ? icon : null,
            StringValue(files?["download"], "") is { Length: > 0 } download ? download : null,
            categories, installed.Contains(ns + "." + name));
    }

    private static HashSet<string> GetDeclarativeContributionFiles(JObject contributes)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in (contributes["themes"] as JArray ?? []).OfType<JObject>())
            AddContributionPath(files, StringValue(theme["path"], ""));
        foreach (var snippet in (contributes["snippets"] as JArray ?? []))
        {
            var path = snippet.Type == JTokenType.String ? snippet.Value<string>() : StringValue(snippet["path"], "");
            AddContributionPath(files, path ?? "");
        }
        return files;
    }

    private static void AddContributionPath(ISet<string> files, string raw)
    {
        var safe = SafeContributionPath(raw);
        if (safe is not null) files.Add(safe);
    }

    private static string? SafeContributionPath(string path)
    {
        path = path.Trim().Replace('\\', '/');
        if (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        if (path.Length == 0 || Path.IsPathRooted(path)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..")) return null;
        return Path.Combine(segments);
    }

    private static string ResolveInside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relative));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Paket extension mencoba menulis di luar direktorinya.");
        return fullPath;
    }

    private static string SafeSegment(string text)
    {
        var clean = new string(text.Where(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-').Take(100).ToArray());
        if (clean is "." or "..") clean = clean.Replace('.', '_');
        return clean.Length == 0 ? "extension" : clean;
    }

    private static Dictionary<string, string> MapThemePalette(JObject? colors)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["window"] = "titleBar.activeBackground", ["title"] = "titleBar.activeBackground",
            ["activity"] = "activityBar.background", ["sidebar"] = "sideBar.background",
            ["panel"] = "panel.background", ["editor"] = "editor.background", ["terminal"] = "terminal.background",
            ["surface"] = "editorGroupHeader.tabsBackground", ["surfaceHover"] = "list.hoverBackground",
            ["input"] = "input.background", ["border"] = "contrastBorder", ["borderLight"] = "input.border",
            ["separator"] = "panel.border", ["status"] = "statusBar.background", ["popup"] = "editorWidget.background",
            ["menu"] = "menu.background", ["selectedPanel"] = "panelTitle.activeBackground",
            ["primary"] = "editor.foreground", ["secondary"] = "sideBar.foreground", ["soft"] = "editor.foreground",
            ["muted"] = "descriptionForeground", ["dim"] = "editorLineNumber.foreground",
            ["activityIdle"] = "activityBar.inactiveForeground", ["accent"] = "focusBorder",
            ["accentHover"] = "button.hoverBackground", ["accentSoft"] = "textLink.activeForeground",
            ["lineNumber"] = "editorLineNumber.foreground", ["surfaceAlt"] = "sideBarSectionHeader.background",
            ["activeNav"] = "list.activeSelectionBackground", ["activeSurface"] = "list.activeSelectionBackground",
            ["buttonSurface"] = "button.background"
        };
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (colors is null) return result;
        foreach (var (role, sourceKey) in map)
        {
            var value = StringValue(colors[sourceKey], "");
            if (IsHexColor(value)) result[role] = value;
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> MapSyntaxColors(JArray? tokens)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens?.OfType<JObject>() ?? [])
        {
            var scopes = token["scope"]?.Type == JTokenType.Array
                ? string.Join(" ", token["scope"]!.Values<string>())
                : StringValue(token["scope"], "");
            var color = StringValue(token["settings"]?["foreground"], "");
            if (!IsHexColor(color)) continue;
            var role = scopes.Contains("comment", StringComparison.OrdinalIgnoreCase) ? "comment"
                : scopes.Contains("string", StringComparison.OrdinalIgnoreCase) ? "text"
                : scopes.Contains("number", StringComparison.OrdinalIgnoreCase) || scopes.Contains("constant", StringComparison.OrdinalIgnoreCase) ? "number"
                : scopes.Contains("keyword", StringComparison.OrdinalIgnoreCase) || scopes.Contains("storage", StringComparison.OrdinalIgnoreCase) ? "keyword"
                : scopes.Contains("type", StringComparison.OrdinalIgnoreCase) || scopes.Contains("class", StringComparison.OrdinalIgnoreCase) ? "type"
                : scopes.Contains("function", StringComparison.OrdinalIgnoreCase) || scopes.Contains("method", StringComparison.OrdinalIgnoreCase) ? "function"
                : scopes.Contains("punctuation", StringComparison.OrdinalIgnoreCase) ? "punctuation"
                : null;
            if (role is not null) result.TryAdd(role, color);
        }
        return result;
    }

    private static bool IsHexColor(string value) => value.Length is 7 or 9 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);
    private static string StringValue(JToken? token, string fallback) => token?.Type == JTokenType.String ? token.Value<string>() ?? fallback : fallback;
    private static long LongValue(JToken? token) => long.TryParse(token?.ToString(), out var value) ? value : 0;
}
