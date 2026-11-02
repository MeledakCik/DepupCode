using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using DepupCode.Models;
using Newtonsoft.Json.Linq;
using Octokit;

namespace DepupCode.Services;

public sealed class GitHubService
{
    private static GitHubClient Client(string? token)
    {
        var client = new GitHubClient(new Octokit.ProductHeaderValue("DepupCode"));
        if (!string.IsNullOrWhiteSpace(token)) client.Credentials = new Credentials(token);
        return client;
    }

    public static (string Owner, string Repository) ParseRepositoryUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Masukkan URL repository GitHub yang valid.");
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2) throw new ArgumentException("URL harus berisi owner dan nama repository.");
        return (parts[0], Path.GetFileNameWithoutExtension(parts[1]));
    }

    public async Task<(Repository Repository, IReadOnlyList<RepositoryContent> Root)> GetRepositoryAsync(
        string url, string? token, CancellationToken cancellationToken = default)
    {
        new SettingsService().EnsureOnlineMode();
        var (owner, name) = ParseRepositoryUrl(url);
        var client = Client(token);
        var repo = await client.Repository.Get(owner, name);
        var root = await client.Repository.Content.GetAllContents(owner, name);
        cancellationToken.ThrowIfCancellationRequested();
        return (repo, root);
    }

    public async Task<IReadOnlyList<RepositoryContent>> GetDirectoryContentsAsync(string owner, string repository,
        string path, string? token, CancellationToken cancellationToken = default)
    {
        new SettingsService().EnsureOnlineMode();
        cancellationToken.ThrowIfCancellationRequested();
        return await Client(token).Repository.Content.GetAllContents(owner, repository, path);
    }

    public async Task<UploadResult> UploadFolderAsync(string folder, string name, bool isPrivate, string token,
        CancellationToken cancellationToken = default)
    {
        new SettingsService().EnsureOnlineMode();
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Simpan GitHub Token dengan izin repo di Settings.");
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !ShouldSkip(Path.GetRelativePath(folder, f)))
            .Select(f => (Full: f, Relative: Path.GetRelativePath(folder, f).Replace('\\', '/')))
            .ToList();
        if (files.Count > 3000) throw new InvalidOperationException("Upload dibatasi sampai 3.000 file.");
        var totalBytes = files.Sum(f => new FileInfo(f.Full).Length);
        if (totalBytes > 150L * 1024 * 1024) throw new InvalidOperationException("Ukuran upload melewati batas 150 MB.");
        if (files.Any(f => new FileInfo(f.Full).Length > 50L * 1024 * 1024))
            throw new InvalidOperationException("Satu file melewati batas 50 MB.");

        var dotenv = File.Exists(Path.Combine(folder, ".env"))
            ? ParseDotEnv(await File.ReadAllTextAsync(Path.Combine(folder, ".env"), cancellationToken))
            : [];

        var client = Client(token);
        var created = await client.Repository.Create(new NewRepository(name)
        {
            Private = isPrivate,
            AutoInit = true,
            Description = "Uploaded from Depup Code"
        });
        var treeItems = new List<NewTreeItem>(files.Count);
        var semaphore = new SemaphoreSlim(8);
        try
        {
            var tasks = files.Select(async file =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var bytes = await File.ReadAllBytesAsync(file.Full, cancellationToken);
                    var blob = await client.Git.Blob.Create(created.Owner.Login, created.Name, new NewBlob
                    {
                        Encoding = EncodingType.Base64,
                        Content = Convert.ToBase64String(bytes)
                    });
                    lock (treeItems)
                    {
                        treeItems.Add(new NewTreeItem
                        {
                            Path = file.Relative,
                            Mode = "100644",
                            Type = TreeType.Blob,
                            Sha = blob.Sha
                        });
                    }
                }
                finally { semaphore.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        finally { semaphore.Dispose(); }

        var branch = string.IsNullOrWhiteSpace(created.DefaultBranch) ? "main" : created.DefaultBranch;
        var reference = await client.Git.Reference.Get(created.Owner.Login, created.Name, "heads/" + branch);
        var parentSha = reference.Object.Sha;
        var parentCommit = await client.Git.Commit.Get(created.Owner.Login, created.Name, parentSha);
        var treeInput = new NewTree { BaseTree = parentCommit.Tree.Sha };
        foreach (var item in treeItems) treeInput.Tree.Add(item);
        var tree = await client.Git.Tree.Create(created.Owner.Login, created.Name, treeInput);
        var commit = await client.Git.Commit.Create(created.Owner.Login, created.Name,
            new NewCommit("Upload from Depup Code", tree.Sha, parentSha));
        await client.Git.Reference.Update(created.Owner.Login, created.Name, "heads/" + branch,
            new ReferenceUpdate(commit.Sha));

        return new UploadResult
        {
            RepositoryUrl = created.HtmlUrl,
            UploadedFiles = files.Count,
            EnvironmentVariables = dotenv
        };
    }

    public async Task<string> CommitPlannedFilesAsync(string owner, string repository, string branch,
        IReadOnlyCollection<PlannedFile> files, string token, CancellationToken cancellationToken = default)
    {
        new SettingsService().EnsureOnlineMode();
        if (files.Count == 0) return "";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DepupCode/1.0");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        var baseUrl = $"https://api.github.com/repos/{owner}/{repository}/git";
        var branchPath = Uri.EscapeDataString(branch);
        var reference = JObject.Parse(await GetText(http, $"{baseUrl}/ref/heads/{branchPath}", cancellationToken));
        var parentSha = (string?)reference["object"]?["sha"] ?? throw new InvalidOperationException("Branch GitHub tidak ditemukan.");
        var parent = JObject.Parse(await GetText(http, $"{baseUrl}/commits/{parentSha}", cancellationToken));
        var baseTree = (string?)parent["tree"]?["sha"] ?? throw new InvalidOperationException("Tree repository tidak ditemukan.");
        var treeJson = new JObject
        {
            ["base_tree"] = baseTree,
            ["tree"] = new JArray(files.Select(file => new JObject
            {
                ["path"] = file.Path.Replace('\\', '/'),
                ["mode"] = "100644",
                ["type"] = "blob",
                ["content"] = file.Content
            }))
        };
        var treeResponse = JObject.Parse(await PostJson(http, $"{baseUrl}/trees", treeJson, cancellationToken));
        var treeSha = (string?)treeResponse["sha"] ?? throw new InvalidOperationException("GitHub gagal membuat tree.");
        var commitResponse = JObject.Parse(await PostJson(http, $"{baseUrl}/commits", new JObject
        {
            ["message"] = "Depup: lengkapi konfigurasi deploy Railway",
            ["tree"] = treeSha,
            ["parents"] = new JArray(parentSha)
        }, cancellationToken));
        var commitSha = (string?)commitResponse["sha"] ?? throw new InvalidOperationException("GitHub gagal membuat commit.");
        var update = new JObject { ["sha"] = commitSha, ["force"] = false };
        await PatchJson(http, $"{baseUrl}/refs/heads/{branchPath}", update, cancellationToken);
        return commitSha;
    }

    public static List<EnvironmentVariable> ParseDotEnv(string text)
    {
        var result = new List<EnvironmentVariable>();
        foreach (var line in text.Split('\n'))
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.StartsWith("export ", StringComparison.Ordinal)) value = value[7..].TrimStart();
            var split = value.IndexOf('=');
            if (split <= 0) continue;
            var key = value[..split].Trim();
            var content = value[(split + 1)..].Trim();
            if (content.Length >= 2 && ((content[0] == '"' && content[^1] == '"') || (content[0] == '\'' && content[^1] == '\'')))
                content = content[1..^1];
            if (key.Length > 0)
                result.Add(new EnvironmentVariable { Name = key, Value = content });
        }
        return result;
    }

    private static bool ShouldSkip(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        if (parts.Any(p => p is ".git" or "node_modules" or "bin" or "obj" or ".next" or ".venv" or "venv" or "coverage"))
            return true;
        var name = parts[^1];
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
               (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && !name.Equals(".env.example", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> GetText(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub API {(int)response.StatusCode}: {ExtractMessage(text)}");
        return text;
    }

    private static async Task<string> PostJson(HttpClient http, string url, JObject body, CancellationToken cancellationToken) =>
        await SendJson(http, HttpMethod.Post, url, body, cancellationToken);

    private static async Task<string> PatchJson(HttpClient http, string url, JObject body, CancellationToken cancellationToken) =>
        await SendJson(http, HttpMethod.Patch, url, body, cancellationToken);

    private static async Task<string> SendJson(HttpClient http, HttpMethod method, string url, JObject body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json")
        };
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GitHub API {(int)response.StatusCode}: {ExtractMessage(text)}");
        return text;
    }

    private static string ExtractMessage(string text)
    {
        try { return (string?)JObject.Parse(text)["message"] ?? text; } catch { return text; }
    }
}
