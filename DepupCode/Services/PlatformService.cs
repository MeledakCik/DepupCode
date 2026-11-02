using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Runtime.ExceptionServices;
using DepupCode.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DepupCode.Services;

public enum DeployPlatform { DepupCloud, Vercel, CloudflarePages, Railway }

public sealed class DeploymentResult
{
    public string Url { get; set; } = "";
    public string Id { get; set; } = "";
    public string ProviderProjectId { get; set; } = "";
    public string ProviderServiceId { get; set; } = "";
    public string ProviderEnvironmentId { get; set; } = "";
    public string Message { get; set; } = "";
    public string Status { get; set; } = "Deployed";
}

public sealed class PlatformService
{
    private readonly SettingsService _settings;
    private readonly GitHubService _github;
    private readonly LocalDataStore _store;
    private readonly DepupCloudService _depup;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(35) };

    public PlatformService(SettingsService settings, GitHubService? github = null, LocalDataStore? store = null)
    {
        _settings = settings;
        _github = github ?? new GitHubService();
        _store = store ?? new LocalDataStore();
        _depup = new DepupCloudService(settings);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DepupCode/2.5.0");
    }

    public async Task<DeploymentResult> DeployAsync(RepoAnalysis repo, DeployPlatform platform, string projectName,
        IProgress<(int Percent, string Message)>? progress = null, CancellationToken cancellationToken = default)
    {
        _settings.EnsureOnlineMode();
        IReadOnlyCollection<EnvironmentVariable> environmentVariables = string.IsNullOrWhiteSpace(repo.RepositoryUrl)
            ? _store.LoadEnvironment(projectName)
            : _store.LoadEnvironment(repo.RepositoryUrl);

        if (platform == DeployPlatform.DepupCloud)
        {
            progress?.Report((10, "Menyiapkan bundle & pipeline Depup Cloud (depup.cloud)…"));
            return await _depup.DeployToDepupCloudAsync(repo, projectName, environmentVariables, progress, cancellationToken);
        }

        var provider = platform == DeployPlatform.CloudflarePages ? "cloudflare" : platform.ToString().ToLowerInvariant();
        var token = _settings.GetToken(provider);
        if (string.IsNullOrWhiteSpace(token))
        {
            // Token provider lokal kosong tapi sesi Depup Cloud ada: pakai token yang tersimpan di akun cloud
            // (alur deploy + polling status dari build Codex), bukan mengirim token kosong ke /api/deploy.
            var depupToken = _settings.GetToken("depup");
            if (!string.IsNullOrWhiteSpace(depupToken))
            {
                progress?.Report((14, $"Token {provider} lokal kosong; memakai token dari akun Depup Cloud…"));
                return await _depup.DeployAsync(repo, provider, projectName, environmentVariables, progress, cancellationToken);
            }
            throw new InvalidOperationException($"Token {provider} belum disimpan di Settings (atau gunakan target Depup Edge CDN).");
        }

        progress?.Report((8, "Memeriksa konfigurasi dan kredensial…"));
        if (platform == DeployPlatform.Railway && repo.Fixes.Count > 0 && !string.IsNullOrWhiteSpace(repo.Owner))
        {
            var githubToken = _settings.GetToken("github");
            if (!string.IsNullOrWhiteSpace(githubToken))
            {
                progress?.Report((16, $"Menerapkan {repo.Fixes.Count} pelengkap file ke GitHub…"));
                try
                {
                    var files = repo.Fixes.Select(file => new PlannedFile
                    {
                        Path = string.IsNullOrWhiteSpace(repo.RootDirectory) ? file.Path :
                            repo.RootDirectory.Trim('/') + "/" + file.Path.TrimStart('/'),
                        Action = file.Action,
                        Reason = file.Reason,
                        Content = file.Content
                    }).ToList();
                    await _github.CommitPlannedFilesAsync(repo.Owner, repo.Repository, repo.Branch, files, githubToken, cancellationToken);
                    progress?.Report((27, "Pelengkap file selesai dalam satu commit."));
                }
                catch (Exception ex) { progress?.Report((24, "Auto-fix gagal, deploy tetap dilanjutkan: " + ex.Message)); }
            }
            else progress?.Report((24, "GitHub Token belum diisi; deploy dilanjutkan tanpa auto-fix."));
        }
        return platform switch
        {
            DeployPlatform.Vercel => await DeployVercelAsync(repo, projectName, token, environmentVariables, progress, cancellationToken),
            DeployPlatform.CloudflarePages => await DeployCloudflareAsync(repo, projectName, token, environmentVariables, progress, cancellationToken),
            _ => await DeployRailwayAsync(repo, projectName, token, environmentVariables, progress, cancellationToken)
        };
    }

    public Task<bool> TestConnectionAsync(DeployPlatform platform, string token, CancellationToken ct = default) =>
        _settings.TestConnectionAsync(
            platform == DeployPlatform.CloudflarePages ? "cloudflare" :
            platform == DeployPlatform.DepupCloud ? "depup" : platform.ToString(),
            token,
            platform == DeployPlatform.DepupCloud ? _settings.Load().DepupCloudUrl : _settings.Load().CloudflareAccountId, ct);

    public async Task<List<ProjectRecord>> ListProjectsAsync(string provider, CancellationToken ct = default)
    {
        _settings.EnsureOnlineMode();
        if (provider.Equals("depup", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("depupcloud", StringComparison.OrdinalIgnoreCase))
        {
            return await _depup.ListSitesAsync(ct);
        }

        var token = _settings.GetToken(provider);
        if (string.IsNullOrWhiteSpace(token)) return [];
        SetBearer(token);
        if (provider.Equals("vercel", StringComparison.OrdinalIgnoreCase))
        {
            var result = await GetJsonAsync("https://api.vercel.com/v9/projects?limit=100", ct);
            return (result["projects"] as JArray ?? new JArray()).OfType<JObject>().Select(p => new ProjectRecord
            {
                Name = (string?)p["name"] ?? "Vercel project",
                Platform = "Vercel",
                PlatformSub = "Project Vercel",
                Framework = (string?)p["framework"] ?? "Unknown",
                SiteUrl = "",
                Status = "Tersinkronkan",
                IconKind = "cloud",
                RepositoryUrl = GitHubUrl(p["link"] as JObject),
                ProviderProjectId = (string?)p["id"] ?? ""
            }).ToList();
        }
        if (provider.Equals("cloudflare", StringComparison.OrdinalIgnoreCase))
        {
            var account = _settings.Load().CloudflareAccountId;
            if (string.IsNullOrWhiteSpace(account)) return [];
            var result = await GetJsonAsync($"https://api.cloudflare.com/client/v4/accounts/{account}/pages/projects", ct);
            return (result["result"] as JArray ?? new JArray()).OfType<JObject>().Select(p => new ProjectRecord
            {
                Name = (string?)p["name"] ?? "Pages project",
                Platform = "Cloudflare Pages",
                PlatformSub = "Project Pages",
                Framework = "Pages Static / Edge",
                SiteUrl = $"https://{(string?)p["subdomain"] ?? (string?)p["name"]}.pages.dev",
                Status = "Tersinkronkan",
                IconKind = "cloud",
                RepositoryUrl = GitHubUrl(p["source"]?["config"] as JObject)
            }).ToList();
        }
        if (provider.Equals("railway", StringComparison.OrdinalIgnoreCase))
        {
            const string query = "query { projects { edges { node { id name services { edges { node { id name } } } environments { edges { node { id name } } } } } } }";
            var data = await RailwayGraphQlAsync(token, query, new JObject(), ct);
            return (data["projects"]?["edges"] as JArray ?? new JArray()).OfType<JObject>()
                .SelectMany(p =>
                {
                    var node = p["node"] as JObject ?? new JObject();
                    var environmentId = (string?)node["environments"]?["edges"]?.FirstOrDefault()?["node"]?["id"] ?? "";
                    var projectId = (string?)node["id"] ?? "";
                    return (node["services"]?["edges"] as JArray ?? new JArray()).OfType<JObject>().Select(s =>
                    {
                        var service = s["node"] as JObject ?? new JObject();
                        return new ProjectRecord
                        {
                            Name = (string?)service["name"] ?? (string?)node["name"] ?? "Railway service",
                            Platform = "Railway",
                            PlatformSub = "Railway service",
                            Framework = "Unknown",
                            SiteUrl = "",
                            Status = "Terhubung",
                            IconKind = "bolt",
                            ProviderProjectId = projectId,
                            ProviderServiceId = (string?)service["id"] ?? "",
                            ProviderEnvironmentId = environmentId
                        };
                    });
                }).ToList();
        }
        return [];
    }

    public async Task<DeploymentResult> RedeployRailwayAsync(ProjectRecord project, CancellationToken ct = default)
    {
        _settings.EnsureOnlineMode();
        var token = _settings.GetToken("railway");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Token Railway belum disimpan di Settings.");
        if (string.IsNullOrWhiteSpace(project.ProviderServiceId) || string.IsNullOrWhiteSpace(project.ProviderEnvironmentId))
            throw new InvalidOperationException("Service atau environment Railway tidak tersimpan. Sinkronkan kembali project.");
        const string query = "mutation ServiceInstanceRedeploy($serviceId: String!, $environmentId: String!) { serviceInstanceRedeploy(serviceId: $serviceId, environmentId: $environmentId) }";
        var data = await RailwayGraphQlAsync(token, query, new JObject
        {
            ["serviceId"] = project.ProviderServiceId,
            ["environmentId"] = project.ProviderEnvironmentId
        }, ct);
        return new DeploymentResult
        {
            Id = data["serviceInstanceRedeploy"]?.ToString() ?? project.ProviderServiceId,
            ProviderProjectId = project.ProviderProjectId,
            ProviderServiceId = project.ProviderServiceId,
            ProviderEnvironmentId = project.ProviderEnvironmentId,
            Url = project.SiteUrl,
            Message = "Redeploy Railway dimulai."
        };
    }

    private async Task<DeploymentResult> DeployVercelAsync(RepoAnalysis repo, string name, string token,
        IReadOnlyCollection<EnvironmentVariable> environmentVariables,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        SetBearer(token);
        var safeName = Slug(name);
        var rootDirectory = string.IsNullOrWhiteSpace(repo.RootDirectory) ? repo.StaticDirectory : repo.RootDirectory;
        if (repo.GitHubRepositoryId <= 0) throw new InvalidOperationException("ID repository GitHub tidak ditemukan. Analisis ulang URL repository.");
        progress?.Report((35, "Menghubungkan repository dan membuat project Vercel…"));
        var projectPayload = new JObject
        {
            ["name"] = safeName,
            ["gitRepository"] = new JObject { ["type"] = "github", ["repo"] = repo.Owner + "/" + repo.Repository }
        };
        if (!string.IsNullOrWhiteSpace(rootDirectory)) projectPayload["rootDirectory"] = rootDirectory;
        JObject project;
        try
        {
            project = await SendJsonAsync(HttpMethod.Post, "https://api.vercel.com/v11/projects", projectPayload, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("409", StringComparison.Ordinal))
        {
            project = await GetJsonAsync($"https://api.vercel.com/v9/projects/{Uri.EscapeDataString(safeName)}", ct);
            var link = project["link"] as JObject;
            var linkedRepo = (string?)link?["repo"] ?? "";
            if (!linkedRepo.Contains('/')) linkedRepo = ((string?)link?["org"] ?? "") + "/" + linkedRepo;
            if (!linkedRepo.Equals(repo.Owner + "/" + repo.Repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Nama project Vercel '{safeName}' sudah digunakan oleh repository lain.");
            if (!string.IsNullOrWhiteSpace(rootDirectory))
            {
                var currentRoot = (string?)project["rootDirectory"] ?? "";
                if (!currentRoot.Trim('/').Equals(rootDirectory.Trim('/'), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Project Vercel yang ada memakai Root Directory berbeda. Ubah di Vercel atau gunakan nama project lain.");
            }
        }
        var projectId = (string?)project["id"] ?? (string?)project["projectId"] ??
            throw new InvalidOperationException("Vercel tidak mengembalikan project id.");
        if (environmentVariables.Count > 0)
        {
            progress?.Report((44, $"Menyinkronkan {environmentVariables.Count} environment variable ke Vercel…"));
            var variables = new JArray(environmentVariables.Select(v => new JObject
            {
                ["key"] = v.Name,
                ["value"] = v.Value,
                ["type"] = "encrypted",
                ["target"] = VercelTargets(v.Target)
            }));
            var sync = await SendJsonAsync(HttpMethod.Post,
                $"https://api.vercel.com/v10/projects/{Uri.EscapeDataString(projectId)}/env?upsert=true", variables, ct);
            if (sync["failed"] is JArray { Count: > 0 } failed)
                throw new InvalidOperationException("Vercel menolak sebagian environment variable: " +
                    string.Join(", ", failed.OfType<JObject>().Select(x => (string?)x["error"]?["message"] ?? (string?)x["key"] ?? "unknown")));
        }
        progress?.Report((48, "Membuat deployment produksi Vercel…"));
        var payload = new JObject
        {
            ["name"] = safeName,
            ["project"] = projectId,
            ["target"] = "production",
            ["gitSource"] = new JObject { ["type"] = "github", ["repoId"] = repo.GitHubRepositoryId, ["ref"] = repo.Branch }
        };
        var response = await SendJsonAsync(HttpMethod.Post, "https://api.vercel.com/v13/deployments", payload, ct);
        var id = (string?)response["id"] ?? (string?)response["uid"] ?? "";
        var url = (string?)response["url"] ?? $"https://{safeName}.vercel.app";
        var state = (string?)response["readyState"] ?? "BUILDING";
        progress?.Report((62, "Vercel sedang membangun project…"));
        if (id.Length > 0)
        {
            var end = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < end && state is not ("READY" or "ERROR" or "CANCELED"))
            {
                await Task.Delay(TimeSpan.FromSeconds(4), ct);
                var poll = await GetJsonAsync($"https://api.vercel.com/v13/deployments/{Uri.EscapeDataString(id)}", ct);
                state = (string?)poll["readyState"] ?? state;
                url = (string?)poll["url"] ?? url;
                progress?.Report((Math.Min(94, 65 + DateTime.UtcNow.Second % 25), "Vercel status: " + state));
            }
        }
        if (state == "ERROR") throw new InvalidOperationException("Vercel build gagal. Periksa build log di dashboard Vercel.");
        progress?.Report((100, "Deployment Vercel siap."));
        return new DeploymentResult { Id = id, ProviderProjectId = projectId, Url = NormalizeUrl(url), Message = "Vercel menerima deployment." };
    }

    private async Task<DeploymentResult> DeployCloudflareAsync(RepoAnalysis repo, string name, string token,
        IReadOnlyCollection<EnvironmentVariable> environmentVariables,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        var account = _settings.Load().CloudflareAccountId.Trim();
        if (account.Length == 0) throw new InvalidOperationException("Isi Cloudflare Account ID di Settings.");
        SetBearer(token);
        progress?.Report((38, "Membuat project Cloudflare Pages…"));
        var buildConfig = new JObject
        {
            ["root_dir"] = (string.IsNullOrWhiteSpace(repo.RootDirectory) ? repo.StaticDirectory : repo.RootDirectory).Length == 0
                ? "/" : "/" + (string.IsNullOrWhiteSpace(repo.RootDirectory) ? repo.StaticDirectory : repo.RootDirectory).Trim('/')
        };
        if (!string.IsNullOrWhiteSpace(BuildCommand(repo))) buildConfig["build_command"] = BuildCommand(repo);
        if (!string.IsNullOrWhiteSpace(OutputDirectory(repo))) buildConfig["destination_dir"] = OutputDirectory(repo);
        var payload = new JObject
        {
            ["name"] = Slug(name), ["production_branch"] = repo.Branch,
            ["source"] = new JObject { ["type"] = "github", ["config"] = new JObject
                { ["owner"] = repo.Owner, ["repo_name"] = repo.Repository, ["production_branch"] = repo.Branch,
                  ["repo_id"] = repo.GitHubRepositoryId.ToString(), ["pr_comments_enabled"] = true,
                  ["deployments_enabled"] = true } },
            ["build_config"] = buildConfig
        };
        if (environmentVariables.Count > 0)
        {
            JObject VarsFor(string target) => new(environmentVariables
                .Where(v => v.Target.Equals(target, StringComparison.OrdinalIgnoreCase) || v.Target.Equals("All", StringComparison.OrdinalIgnoreCase))
                .Select(v => new JProperty(v.Name, new JObject { ["type"] = "secret_text", ["value"] = v.Value })));
            payload["deployment_configs"] = new JObject
            {
                ["production"] = new JObject { ["env_vars"] = VarsFor("Production") },
                ["preview"] = new JObject { ["env_vars"] = VarsFor("Preview") }
            };
        }
        var safeName = Slug(name);
        var accountPath = $"https://api.cloudflare.com/client/v4/accounts/{Uri.EscapeDataString(account)}/pages/projects";
        JObject project;
        try
        {
            var created = await SendJsonAsync(HttpMethod.Post, accountPath, payload, ct);
            project = created["result"] as JObject ?? created;
        }
        catch (HttpRequestException createError)
        {
            JObject existing;
            try
            {
                var found = await GetJsonAsync(accountPath + "/" + Uri.EscapeDataString(safeName), ct);
                existing = found["result"] as JObject ?? found;
            }
            catch
            {
                ExceptionDispatchInfo.Capture(createError).Throw();
                throw;
            }
            var source = existing["source"]?["config"] as JObject;
            var linkedRepo = ((string?)source?["owner"] ?? "") + "/" + ((string?)source?["repo_name"] ?? "");
            if (!linkedRepo.Equals(repo.Owner + "/" + repo.Repository, StringComparison.OrdinalIgnoreCase))
                ExceptionDispatchInfo.Capture(createError).Throw();
            progress?.Report((43, "Memperbarui konfigurasi Cloudflare Pages yang sudah ada…"));
            var update = (JObject)payload.DeepClone();
            update.Remove("source");
            var updated = await SendJsonAsync(new HttpMethod("PATCH"), accountPath + "/" + Uri.EscapeDataString(safeName), update, ct);
            project = updated["result"] as JObject ?? existing;
        }
        progress?.Report((72, "Memulai deployment Cloudflare Pages…"));
        var deployment = await SendJsonAsync(HttpMethod.Post,
            accountPath + "/" + Uri.EscapeDataString(safeName) + "/deployments",
            new JObject { ["branch"] = repo.Branch }, ct);
        var result = deployment["result"] as JObject ?? deployment;
        var projectId = (string?)project["id"] ?? (string?)result["project_id"] ?? safeName;
        var domain = (string?)result["url"] ?? (string?)result["aliases"]?.FirstOrDefault() ??
            (string?)project["subdomain"] ?? safeName + ".pages.dev";
        progress?.Report((100, "Cloudflare Pages menerima deployment."));
        return new DeploymentResult { Id = (string?)result["id"] ?? projectId, ProviderProjectId = projectId,
            Url = NormalizeUrl(domain), Message = "Cloudflare Pages deployment dimulai." };
    }

    private async Task<DeploymentResult> DeployRailwayAsync(RepoAnalysis repo, string name, string token,
        IReadOnlyCollection<EnvironmentVariable> environmentVariables,
        IProgress<(int Percent, string Message)>? progress, CancellationToken ct)
    {
        progress?.Report((36, "Membuat Railway project…"));
        const string projectQuery = "mutation ProjectCreate($input: ProjectCreateInput!) { projectCreate(input: $input) { id name environments { edges { node { id name } } } } }";
        var projectData = await RailwayGraphQlAsync(token, projectQuery,
            new JObject { ["input"] = new JObject { ["name"] = name } }, ct);
        var project = projectData["projectCreate"] as JObject ?? throw new InvalidOperationException("Railway tidak mengembalikan project.");
        var projectId = (string?)project["id"] ?? throw new InvalidOperationException("Railway tidak mengembalikan project id.");
        var envId = (string?)project["environments"]?["edges"]?.FirstOrDefault()?["node"]?["id"];
        if (string.IsNullOrWhiteSpace(envId)) throw new InvalidOperationException("Railway tidak mengembalikan environment id.");
        progress?.Report((58, "Menghubungkan repository GitHub ke Railway…"));
        const string serviceQuery = "mutation ServiceCreate($input: ServiceCreateInput!) { serviceCreate(input: $input) { id name } }";
        var serviceData = await RailwayGraphQlAsync(token, serviceQuery, new JObject
        {
            ["input"] = new JObject
            {
                ["projectId"] = projectId, ["environmentId"] = envId, ["name"] = name,
                ["source"] = new JObject { ["repo"] = repo.Owner + "/" + repo.Repository }
            }
        }, ct);
        var service = serviceData["serviceCreate"] as JObject ?? throw new InvalidOperationException("Railway tidak mengembalikan service.");
        var serviceId = (string?)service["id"] ?? throw new InvalidOperationException("Railway tidak mengembalikan service id.");
        if (environmentVariables.Count > 0)
        {
            progress?.Report((70, $"Menyinkronkan {environmentVariables.Count} environment variable ke Railway…"));
            const string variablesQuery = "mutation VariableCollectionUpsert($input: VariableCollectionUpsertInput!) { variableCollectionUpsert(input: $input) }";
            var variables = new JObject();
            foreach (var value in environmentVariables) variables[value.Name] = value.Value;
            await RailwayGraphQlAsync(token, variablesQuery, new JObject
            {
                ["input"] = new JObject { ["projectId"] = projectId, ["environmentId"] = envId,
                    ["serviceId"] = serviceId, ["variables"] = variables, ["skipDeploys"] = true }
            }, ct);
        }
        if (!string.IsNullOrWhiteSpace(repo.RootDirectory))
        {
            progress?.Report((76, "Mengatur Root Directory Railway…"));
            const string updateQuery = "mutation ServiceInstanceUpdate($serviceId: String!, $environmentId: String!, $input: ServiceInstanceUpdateInput!) { serviceInstanceUpdate(serviceId: $serviceId, environmentId: $environmentId, input: $input) }";
            await RailwayGraphQlAsync(token, updateQuery, new JObject
            {
                ["serviceId"] = serviceId, ["environmentId"] = envId,
                ["input"] = new JObject { ["rootDirectory"] = repo.RootDirectory }
            }, ct);
        }
        progress?.Report((90, "Memulai deployment Railway…"));
        const string deployQuery = "mutation ServiceInstanceDeploy($serviceId: String!, $environmentId: String!) { serviceInstanceDeployV2(serviceId: $serviceId, environmentId: $environmentId) }";
        var deploymentData = await RailwayGraphQlAsync(token, deployQuery,
            new JObject { ["serviceId"] = serviceId, ["environmentId"] = envId }, ct);
        var deploymentId = deploymentData["serviceInstanceDeployV2"]?.ToString() ?? serviceId;
        var rootSuffix = repo.RootDirectory.Length == 0 ? "" : " (Root Directory: " + repo.RootDirectory + ")";
        progress?.Report((100, "Railway service siap." + rootSuffix));
        return new DeploymentResult
        {
            Id = deploymentId, Url = $"https://railway.com/project/{projectId}",
            ProviderProjectId = projectId, ProviderServiceId = serviceId, ProviderEnvironmentId = envId,
            Message = "Railway service dibuat. Buka dashboard untuk melihat build pertama." + rootSuffix
        };
    }

    private async Task<JObject> RailwayGraphQlAsync(string token, string query, JObject variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://backboard.railway.com/graphql/v2");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonConvert.SerializeObject(new { query, variables }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, ct);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!response.IsSuccessStatusCode || body["errors"] is JArray)
            throw new InvalidOperationException("Railway API: " + (body["errors"]?.FirstOrDefault()?["message"]?.ToString() ?? response.ReasonPhrase));
        return body["data"] as JObject ?? body;
    }

    private async Task<JObject> SendJsonAsync(HttpMethod method, string url, JToken body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"API {(int)response.StatusCode}: {ParseError(text)}");
        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }

    private async Task<JObject> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"API {(int)response.StatusCode}: {ParseError(text)}");
        return JObject.Parse(text);
    }

    private void SetBearer(string token)
    {
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static string ParseError(string body)
    {
        try
        {
            var json = JObject.Parse(body);
            return (string?)json["error"]?["message"] ?? (string?)json["message"] ??
                   (string?)json["errors"]?.FirstOrDefault()?["message"] ?? body;
        }
        catch { return body; }
    }

    private static string Slug(string value)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');
        return slug.Length == 0 ? "depup-project" : slug[..Math.Min(slug.Length, 52)];
    }

    private static string NormalizeUrl(string value) =>
        value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? value : "https://" + value.TrimStart('/');

    private static string GitHubUrl(JObject? link)
    {
        if (link is null) return "";
        var owner = (string?)link["owner"] ?? (string?)link["org"] ?? "";
        var repository = (string?)link["repo"] ?? (string?)link["repo_name"] ?? "";
        if (repository.Contains('/')) return "https://github.com/" + repository.Trim('/');
        return owner.Length > 0 && repository.Length > 0 ? $"https://github.com/{owner}/{repository}" : "";
    }

    private static JArray VercelTargets(string? target)
    {
        var targets = (target ?? "Production").Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mapped = targets.Select(t => t.ToLowerInvariant() switch
            { "production" => "production", "preview" => "preview", "development" => "development", "all" => "all", _ => "" })
            .Where(t => t.Length > 0).Distinct().ToArray();
        return new JArray(mapped.Length == 0 ? new[] { "production" } : mapped);
    }

    private static string BuildCommand(RepoAnalysis repo) => repo.Framework switch
    {
        "Vite" or "Create React App" or "Astro" => "npm run build", _ => ""
    };
    private static string OutputDirectory(RepoAnalysis repo) => repo.Framework switch
    {
        "Vite" or "Vue" or "Svelte" or "Astro" => "dist", "Create React App" => "build", "Angular" => "dist", _ => ""
    };
}
