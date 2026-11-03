using DepupCode.Models;
using Newtonsoft.Json;

namespace DepupCode.Services;

public sealed class LocalDataStore
{
    private readonly string _root = new SettingsService().SettingsDirectory;
    private string ProjectsPath => Path.Combine(_root, "projects.json");
    private string DomainsPath => Path.Combine(_root, "domains.json");

    public List<ProjectRecord> LoadProjects()
    {
        try
        {
            if (File.Exists(ProjectsPath))
            {
                var loaded = JsonConvert.DeserializeObject<List<ProjectRecord>>(File.ReadAllText(ProjectsPath)) ?? [];
                return loaded;
            }
        }
        catch { }

        return [];
    }

    public static List<ProjectRecord> CreateDefaultCatalog()
    {
        return [];
    }

    public void SaveProjects(IEnumerable<ProjectRecord> projects)
    {
        Directory.CreateDirectory(_root);
        AtomicWrite(ProjectsPath, JsonConvert.SerializeObject(projects, Formatting.Indented));
    }

    public void UpsertProject(ProjectRecord project)
    {
        var items = LoadProjects();
        var index = items.FindIndex(p =>
            (!string.IsNullOrWhiteSpace(project.RepositoryUrl) && string.Equals(p.RepositoryUrl, project.RepositoryUrl, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(project.LocalPath) && string.Equals(p.LocalPath, project.LocalPath, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) items.RemoveAt(index);
        items.Insert(0, project);
        SaveProjects(items);
    }

    public void DeleteProject(ProjectRecord project)
    {
        var items = LoadProjects();
        items.RemoveAll(p =>
            string.Equals(p.Name, project.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Platform, project.Platform, StringComparison.OrdinalIgnoreCase));
        Directory.CreateDirectory(_root);
        AtomicWrite(ProjectsPath, JsonConvert.SerializeObject(items, Formatting.Indented));
    }

    public List<DomainRecord> LoadDomains()
    {
        try
        {
            if (File.Exists(DomainsPath))
                return JsonConvert.DeserializeObject<List<DomainRecord>>(File.ReadAllText(DomainsPath)) ?? [];
        }
        catch { }
        return [];
    }

    public void SaveDomains(IEnumerable<DomainRecord> domains)
    {
        Directory.CreateDirectory(_root);
        AtomicWrite(DomainsPath, JsonConvert.SerializeObject(domains, Formatting.Indented));
    }

    public void UpsertDomain(DomainRecord domain)
    {
        var items = LoadDomains();
        var existing = items.FindIndex(item =>
            item.Id.Equals(domain.Id, StringComparison.OrdinalIgnoreCase) ||
            (item.Domain.Equals(domain.Domain, StringComparison.OrdinalIgnoreCase) &&
             item.Project.Equals(domain.Project, StringComparison.OrdinalIgnoreCase)));
        if (existing >= 0) items.RemoveAt(existing);
        items.Insert(0, domain);
        SaveDomains(items);
    }

    public List<EnvironmentVariable> LoadEnvironment(string projectKey)
    {
        var path = EnvironmentPath(projectKey);
        try
        {
            if (File.Exists(path))
            {
                var encrypted = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<EnvironmentVariable>>(SecretStore.Unprotect(encrypted)) ?? [];
                return list;
            }
        }
        catch { }

        return [];
    }

    public void SaveEnvironment(string projectKey, IEnumerable<EnvironmentVariable> values)
    {
        Directory.CreateDirectory(_root);
        var json = JsonConvert.SerializeObject(values, Formatting.Indented);
        AtomicWrite(EnvironmentPath(projectKey), SecretStore.Protect(json));
    }

    public void ImportDotEnv(string projectKey, IEnumerable<EnvironmentVariable> values)
    {
        var existing = LoadEnvironment(projectKey);
        foreach (var value in values)
        {
            existing.RemoveAll(v => v.Name.Equals(value.Name, StringComparison.OrdinalIgnoreCase));
            existing.Add(value);
        }
        SaveEnvironment(projectKey, existing);
    }

    private string EnvironmentPath(string projectKey)
    {
        var safe = string.Concat(projectKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_root, "env-" + safe + ".dpapi");
    }

    private static void AtomicWrite(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, true);
    }
}
