using System.Text.RegularExpressions;

namespace DepupCode.Services;

/// <summary>Local path deny rules applied before project files are read, searched, or written by an AI agent.</summary>
public sealed class AiWorkspaceExclusionService
{
    private const string RulesFileName = ".depup-aiignore";
    private readonly string _root;
    private readonly Regex[] _rules;

    public AiWorkspaceExclusionService(string workspaceRoot)
    {
        _root = string.IsNullOrWhiteSpace(workspaceRoot) ? "" : Path.GetFullPath(workspaceRoot);
        _rules = LoadRules(_root);
    }

    public bool IsExcluded(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (IsAlwaysExcluded(normalized)) return true;
        return _rules.Any(rule => rule.IsMatch(normalized));
    }

    private static Regex[] LoadRules(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return [];
        var path = Path.Combine(root, RulesFileName);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return [];
            return File.ReadLines(path).Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith('!'))
                .Take(128).Select(ToRegex).Where(regex => regex is not null).Cast<Regex>().ToArray();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static Regex? ToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0) return null;
        var anyDepthPrefix = normalized.StartsWith("**/", StringComparison.Ordinal);
        var explicitPath = normalized.Contains('/') && !anyDepthPrefix;
        if (anyDepthPrefix) normalized = normalized[3..];
        var subtree = normalized.EndsWith("/**", StringComparison.Ordinal);
        if (subtree) normalized = normalized[..^3];
        var hasPatternPath = normalized.Contains('/');
        var expression = Regex.Escape(normalized)
            .Replace("\\*\\*", ".*", StringComparison.Ordinal)
            .Replace("\\*", "[^/]*", StringComparison.Ordinal)
            .Replace("\\?", "[^/]", StringComparison.Ordinal);
        var suffix = subtree ? "(?:/.*)?" : "";
        var pathAnchored = explicitPath || anyDepthPrefix && hasPatternPath;
        var prefix = anyDepthPrefix && pathAnchored ? "^(?:.*/)?" : "^";
        var anchored = pathAnchored ? prefix + expression + suffix + "$" : "(?:^|/)" + expression + suffix + "(?:$|/)";
        return new Regex(anchored, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsAlwaysExcluded(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(name =>
            name.Equals(RulesFileName, StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".pypirc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".jks", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".keystore", StringComparison.OrdinalIgnoreCase));
}
