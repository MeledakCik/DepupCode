using System.Windows;
using System.Windows.Media;
using Newtonsoft.Json;

namespace DepupCode.Models;

public sealed class ProjectRecord
{
    public string Name { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string CommitHash { get; set; } = "";
    public string Author { get; set; } = "";
    public string Framework { get; set; } = "Unknown";
    public string Platform { get; set; } = "Belum terhubung platform";
    public string PlatformSub { get; set; } = "";
    public string SiteUrl { get; set; } = "";
    public string ProviderProjectId { get; set; } = "";
    public string ProviderServiceId { get; set; } = "";
    public string ProviderEnvironmentId { get; set; } = "";
    public string Status { get; set; } = "Hanya Lokal";
    public int BuildProgress { get; set; }
    public string IconKind { get; set; } = "folder"; // bolt, cloud, folder
    public bool IsCurrentWorkspace { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string DisplayPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(LocalPath))
            {
                var normalized = LocalPath.Replace('\\', '/');
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/').TrimEnd('/');
                if (normalized.StartsWith(home + "/", StringComparison.OrdinalIgnoreCase)) return "~" + normalized[home.Length..];
                return normalized;
            }
            if (!string.IsNullOrWhiteSpace(RepositoryUrl))
            {
                return RepositoryUrl
                    .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');
            }
            return "~/Projects/" + Name;
        }
    }

    [JsonIgnore]
    public string BranchAndCommit
    {
        get
        {
            var b = string.IsNullOrWhiteSpace(Branch) ? "main" : Branch;
            if (string.IsNullOrWhiteSpace(CommitHash)) return b;
            return CommitHash.StartsWith("commit", StringComparison.OrdinalIgnoreCase)
                ? $"{b} ({CommitHash})"
                : $"{b} (commit {CommitHash})";
        }
    }

    [JsonIgnore]
    public string IconGlyph => IconKind switch
    {
        "cloud" => "☁",
        "folder" => "📁",
        _ => "⚡"
    };

    [JsonIgnore]
    public Brush IconForeground => MakeBrush(IconKind switch
    {
        "cloud" => "#C084FC",
        "folder" => "#FBBF24",
        _ => "#F97316"
    });

    [JsonIgnore]
    public Brush IconBackground => MakeBrush(IconKind switch
    {
        "cloud" => "#23173E",
        "folder" => "#271F16",
        _ => "#1F1638"
    });

    [JsonIgnore]
    public Brush IconBorder => MakeBrush(IconKind switch
    {
        "cloud" => "#4C2E85",
        "folder" => "#54401C",
        _ => "#43307A"
    });

    [JsonIgnore]
    public Visibility OpenBadgeVisibility => IsCurrentWorkspace ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public bool IsLocalOnly =>
        Status.Contains("Lokal", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("Belum dideploy", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("Preparing deployment", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("Deploy gagal", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("Gagal", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("Local", StringComparison.OrdinalIgnoreCase) ||
        Platform.Contains("Belum terhubung", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsBuilding =>
        Status.Contains("Building", StringComparison.OrdinalIgnoreCase) ||
        Status.Contains("Deploying", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public Brush PlatformDotBrush => MakeBrush(
        IsLocalOnly ? "#6C6794" :
        Platform.Contains("Railway", StringComparison.OrdinalIgnoreCase) ? "#A855F7" :
        Platform.Contains("Depup", StringComparison.OrdinalIgnoreCase) ? "#3B82F6" :
        Platform.Contains("Vercel", StringComparison.OrdinalIgnoreCase) ? "#38BDF8" :
        Platform.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase) ? "#F59E0B" : "#34D399");

    [JsonIgnore]
    public Visibility PlatformDotVisibility => IsLocalOnly ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Brush PlatformTitleBrush => MakeBrush(IsLocalOnly ? "#9490B8" : "#F8FAFC");

    [JsonIgnore]
    public FontStyle PlatformFontStyle => IsLocalOnly ? FontStyles.Italic : FontStyles.Normal;

    [JsonIgnore]
    public Brush StatusTextBrush => MakeBrush(
        IsBuilding ? "#FBBF24" :
        Status.Contains("Gagal", StringComparison.OrdinalIgnoreCase) ? "#F87171" :
        IsLocalOnly ? "#F59E0B" : "#34D399");

    [JsonIgnore]
    public Visibility NormalStatusVisibility => IsLocalOnly ? Visibility.Collapsed : Visibility.Visible;

    [JsonIgnore]
    public Visibility LocalBadgeVisibility => IsLocalOnly ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility ProgressVisibility => IsBuilding ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility SiteUrlVisibility =>
        !IsBuilding && !IsLocalOnly && !string.IsNullOrWhiteSpace(SiteUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;

    [JsonIgnore]
    public string DisplaySiteUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SiteUrl)) return "";
            if (SiteUrl.Contains("depup.cloud", StringComparison.OrdinalIgnoreCase))
                return SiteUrl + " ↗";
            return SiteUrl.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
        }
    }

    [JsonIgnore]
    public string RelativeUpdatedText
    {
        get
        {
            var span = DateTime.UtcNow - UpdatedAt.ToUniversalTime();
            if (span.TotalMinutes < 1) return "Baru saja";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} menit lalu";
            if (span.TotalHours < 24 && UpdatedAt.ToLocalTime().Date == DateTime.Now.Date)
                return $"{(int)span.TotalHours} jam lalu";
            if (span.TotalHours < 48)
                return $"Kemarin, {UpdatedAt.ToLocalTime():HH:mm}";
            return $"{(int)Math.Max(1, span.TotalDays)} hari lalu";
        }
    }

    [JsonIgnore]
    public string UpdatedSubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Author))
            {
                return Author.StartsWith("commit", StringComparison.OrdinalIgnoreCase) ||
                       Author.StartsWith("oleh ", StringComparison.OrdinalIgnoreCase)
                    ? Author
                    : "oleh " + Author;
            }
            return string.IsNullOrWhiteSpace(CommitHash) ? "commit belum tersedia" : "commit " + CommitHash;
        }
    }

    [JsonIgnore]
    public string PrimaryActionLabel
    {
        get
        {
            if (IsCurrentWorkspace) return "Buka Editor";
            if (IsLocalOnly) return "●  Deploy Sekarang";
            if (string.IsNullOrWhiteSpace(LocalPath) && !string.IsNullOrWhiteSpace(RepositoryUrl))
                return "Clone Lokal";
            return "Buka Folder";
        }
    }

    [JsonIgnore]
    public bool UsePrimaryAccentButton => IsCurrentWorkspace;

    [JsonIgnore]
    public bool UseDeployOutlineButton => IsLocalOnly;

    [JsonIgnore]
    public string? PrimaryActionCloudTag =>
        !IsCurrentWorkspace && (IsLocalOnly || (string.IsNullOrWhiteSpace(LocalPath) || !Directory.Exists(LocalPath)) && !string.IsNullOrWhiteSpace(RepositoryUrl))
            ? "RequiresCloud"
            : null;

    private static Brush MakeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
