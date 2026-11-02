namespace DepupCode.Models;

public enum ProjectKind
{
    Unknown,
    Static,
    Node,
    Python,
    Docker
}

public sealed class PlannedFile
{
    public string Path { get; set; } = "";
    public string Action { get; set; } = "create";
    public string Reason { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class RepoAnalysis
{
    public string Owner { get; set; } = "";
    public string Repository { get; set; } = "";
    public long GitHubRepositoryId { get; set; }
    public string Branch { get; set; } = "main";
    public string RepositoryUrl { get; set; } = "";
    public string LocalSourcePath { get; set; } = "";
    public ProjectKind Kind { get; set; }
    public string Framework { get; set; } = "Unknown";
    public string RootDirectory { get; set; } = "";
    public string StaticDirectory { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> SupportedPlatforms { get; set; } = [];
    public List<PlannedFile> Fixes { get; set; } = [];
}

public sealed class UploadResult
{
    public string RepositoryUrl { get; set; } = "";
    public int UploadedFiles { get; set; }
    public List<EnvironmentVariable> EnvironmentVariables { get; set; } = [];
}
