using System.Text.RegularExpressions;

namespace DepupCode.Services;

/// <summary>Discovers OpenCode and Agent Skills files without loading their full instructions into every prompt.</summary>
public sealed class OpenCodeSkillCatalog
{
    private const int MaxSkills = 100;
    private const int MaxSkillFileBytes = 64 * 1024;
    private const int MaxLoadedCharacters = 8_000;
    private static readonly Regex ValidName = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly string[] ProjectSkillFolders = [".opencode/skills", ".claude/skills", ".agents/skills"];
    private readonly Dictionary<string, SkillEntry> _skills = new(StringComparer.OrdinalIgnoreCase);

    public OpenCodeSkillCatalog(string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot))
            DiscoverProjectSkills(Path.GetFullPath(workspaceRoot));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            DiscoverDirectory(Path.Combine(home, ".config", "opencode", "skills"), "global");
            DiscoverDirectory(Path.Combine(home, ".claude", "skills"), "global");
            DiscoverDirectory(Path.Combine(home, ".agents", "skills"), "global");
        }
    }

    public int Count => _skills.Count;

    public string DescribeAvailableSkills()
    {
        if (_skills.Count == 0) return "Belum ada skill yang terdeteksi.";
        var entries = _skills.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).Take(20)
            .Select(skill => $"- {skill.Name}: {skill.Description}").ToList();
        if (_skills.Count > entries.Count) entries.Add($"- Plus {_skills.Count - entries.Count} skill lain, tersedia untuk dimuat berdasarkan nama.");
        return string.Join("\n", entries);
    }

    public string Load(string name)
    {
        if (!_skills.TryGetValue(name, out var skill)) return "Skill tidak ditemukan. Periksa nama pada daftar skill.";
        try
        {
            ValidateSkillFile(skill.Path);
            var content = File.ReadAllText(skill.Path);
            var bodyStart = content.IndexOf("---", 3, StringComparison.Ordinal);
            if (content.StartsWith("---", StringComparison.Ordinal) && bodyStart >= 0)
                content = content[(bodyStart + 3)..].TrimStart('\r', '\n', ' ');
            return content.Length > MaxLoadedCharacters
                ? content[..MaxLoadedCharacters] + "\n[Instruksi skill dipotong agar konteks tetap ringkas.]"
                : content;
        }
        catch (Exception ex)
        {
            return "Skill tidak dapat dibaca: " + ex.Message;
        }
    }

    private void DiscoverProjectSkills(string workspace)
    {
        var current = new DirectoryInfo(workspace);
        for (var level = 0; current is not null && level < 12; level++, current = current.Parent)
        {
            foreach (var folder in ProjectSkillFolders)
                DiscoverDirectory(Path.Combine(current.FullName, folder.Replace('/', Path.DirectorySeparatorChar)), "project");

            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                break;
        }
    }

    private void DiscoverDirectory(string skillsRoot, string scope)
    {
        try
        {
            if (!Directory.Exists(skillsRoot) || HasReparsePointInPath(skillsRoot)) return;
            foreach (var folder in Directory.EnumerateDirectories(skillsRoot).Take(MaxSkills))
            {
                if (IsReparsePoint(folder)) continue;
                var folderName = Path.GetFileName(folder);
                var skillPath = Path.Combine(folder, "SKILL.md");
                if (!ValidName.IsMatch(folderName) || !File.Exists(skillPath)) continue;
                try
                {
                    ValidateSkillFile(skillPath);
                    var text = File.ReadAllText(skillPath);
                    var (name, description) = ReadFrontmatter(text);
                    if (!string.Equals(name, folderName, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(description)) continue;
                    _skills.TryAdd(name, new SkillEntry(name, Trim(description, 300), Path.GetFullPath(skillPath), scope));
                    if (_skills.Count >= MaxSkills) return;
                }
                catch { }
            }
        }
        catch { }
    }

    private static (string Name, string Description) ReadFrontmatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return ("", "");
        var end = text.IndexOf("---", 3, StringComparison.Ordinal);
        if (end < 0) return ("", "");
        var name = "";
        var description = "";
        foreach (var line in text[3..end].Split('\n'))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Trim('"', '\'');
            if (key.Equals("name", StringComparison.Ordinal)) name = value;
            else if (key.Equals("description", StringComparison.Ordinal)) description = value;
        }
        return (name, description);
    }

    private static void ValidateSkillFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxSkillFileBytes) throw new InvalidOperationException("File SKILL.md tidak valid atau melebihi 64 KB.");
        if (HasReparsePointInPath(path)) throw new InvalidOperationException("Akses symbolic link diblokir.");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparsePointInPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var current = Path.GetPathRoot(fullPath) ?? "";
        var remainder = fullPath[current.Length..];
        foreach (var part in remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && IsReparsePoint(current)) return true;
        }
        return false;
    }

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record SkillEntry(string Name, string Description, string Path, string Scope);
}
