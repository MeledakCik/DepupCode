using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;

namespace DepupCode.Models;

public sealed class FileNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public ObservableCollection<FileNode> Children { get; } = [];

    public Visibility FolderIconVisibility => IsDirectory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FileBadgeVisibility => IsDirectory ? Visibility.Collapsed : Visibility.Visible;

    public Geometry FolderGeometry
    {
        get
        {
            var lower = Name.ToLowerInvariant();
            if (lower is "supabase" or "database" or "db" or "prisma")
            {
                // Database cylinder + folder geometry
                return Geometry.Parse("M3,6 C3,4.895 3.895,4 5,4 L9.17,4 C9.7,4 10.21,4.21 10.59,4.59 L12,6 L19,6 C20.1,6 21,6.9 21,8 L21,18 C21,19.1 20.1,20 19,20 L5,20 C3.895,20 3,19.1 3,18 Z");
            }
            return Geometry.Parse("M3,6 C3,4.895 3.895,4 5,4 L9.17,4 C9.7,4 10.21,4.21 10.59,4.59 L12,6 L19,6 C20.1,6 21,6.9 21,8 L21,18 C21,19.1 20.1,20 19,20 L5,20 C3.895,20 3,19.1 3,18 Z");
        }
    }

    public string IconGlyph
    {
        get
        {
            if (IsDirectory) return "";
            var lower = Name.ToLowerInvariant();
            if (lower.StartsWith(".env", StringComparison.Ordinal)) return "ENV";
            if (lower == "package.json") return "NPM";
            if (lower.StartsWith(".git", StringComparison.Ordinal)) return "GIT";
            if (lower.Contains("capacitor.config", StringComparison.Ordinal)) return "⚡";
            var ext = Path.GetExtension(lower);
            return ext switch
            {
                ".ts" => "TS",
                ".tsx" => "TSX",
                ".js" or ".mjs" or ".cjs" => "JS",
                ".jsx" => "JSX",
                ".json" => "{ }",
                ".cs" or ".csproj" => "C#",
                ".py" => "PY",
                ".html" or ".htm" => "</>",
                ".css" or ".scss" => "CSS",
                ".md" => "MD",
                ".sql" => "SQL",
                ".yml" or ".yaml" or ".toml" => "CFG",
                ".sh" or ".ps1" or ".bat" => "SH",
                _ => "FILE"
            };
        }
    }

    public Brush IconBrush
    {
        get
        {
            var lower = Name.ToLowerInvariant();
            if (IsDirectory)
            {
                return Make(lower switch
                {
                    "android" or "supabase" or "scripts" => "#34D399",
                    "app" or "src" or "components" => "#A78BFA",
                    "public" or "assets" => "#38BDF8",
                    "cli" or "lib" => "#C084FC",
                    _ => "#8B5CF6"
                });
            }
            if (lower.StartsWith(".env", StringComparison.Ordinal)) return Make("#FBBF24");
            if (lower == "package.json") return Make("#F87171");
            if (lower.StartsWith(".git", StringComparison.Ordinal)) return Make("#FB923C");
            if (lower.Contains("capacitor.config", StringComparison.Ordinal)) return Make("#A855F7");
            var ext = Path.GetExtension(lower);
            return Make(ext switch
            {
                ".ts" or ".tsx" => "#38BDF8",
                ".js" or ".jsx" or ".mjs" or ".cjs" => "#FACC15",
                ".json" => "#F59E0B",
                ".cs" or ".csproj" => "#A855F7",
                ".py" => "#34D399",
                ".html" or ".htm" => "#FB923C",
                ".css" or ".scss" => "#60A5FA",
                ".md" => "#C4B5FD",
                ".sql" => "#2DD4BF",
                _ => "#9490B8"
            });
        }
    }

    public Brush BadgeBackground
    {
        get
        {
            if (IsDirectory) return Brushes.Transparent;
            var lower = Name.ToLowerInvariant();
            if (lower.StartsWith(".env", StringComparison.Ordinal)) return Make("#2A2112");
            if (lower == "package.json") return Make("#2B1419");
            var ext = Path.GetExtension(lower);
            return Make(ext switch
            {
                ".ts" or ".tsx" => "#10263B",
                ".js" or ".jsx" or ".mjs" => "#2B2412",
                ".json" => "#281E12",
                ".cs" or ".csproj" => "#23153C",
                ".py" => "#0F2922",
                _ => "#1D1736"
            });
        }
    }

    private static Brush Make(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }
}

public sealed class GitSnapshot
{
    public string Branch { get; set; } = "No Git";
    public string ShortCommit { get; set; } = "";
    public int ChangedFiles { get; set; }
}
