using System.Diagnostics;
using System.Windows.Media;
using DepupCode.Models;
using LibGit2Sharp;

namespace DepupCode.Services;

public sealed class GitChange
{
    public string RelativePath { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string Status { get; init; } = "M";
    public string FileName => System.IO.Path.GetFileName(RelativePath);
    public string DirectoryHint
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(RelativePath);
            return string.IsNullOrEmpty(dir) ? "" : dir.Replace('\\', '/');
        }
    }
    public string Tag
    {
        get
        {
            var ext = System.IO.Path.GetExtension(RelativePath).TrimStart('.').ToUpperInvariant();
            if (FileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) return "ENV";
            return ext switch { "" => "•", "JSON" => "{}", "MD" => "MD", _ when ext.Length > 3 => ext[..3], _ => ext };
        }
    }
    public Brush TagBrush => Make(Tag switch
    {
        "TS" or "TSX" => "#60A5FA", "JS" or "JSX" => "#FBBF24", "{}" => "#FBBF24", "ENV" => "#F87171", "CSS" => "#38BDF8", "MD" => "#9490B8", _ => "#A78BFA"
    });
    public Brush StatusBrush => Make(Status switch { "U" or "A" => "#34D399", "D" => "#F87171", "C" => "#F472B6", _ => "#FB923C" });

    private static Brush Make(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed class GitPanelInfo
{
    public bool IsRepository { get; set; }
    public string Branch { get; set; } = "No Git";
    public string ShortCommit { get; set; } = "";
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public List<GitChange> Changes { get; } = [];
    public List<GitChange> Ignored { get; } = [];
}

public static class GitService
{
    public static GitSnapshot ReadStatus(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new GitSnapshot();
        try
        {
            var repositoryPath = Repository.Discover(folder);
            if (repositoryPath is null) return new GitSnapshot();
            using var repository = new Repository(repositoryPath);
            var sha = repository.Head.Tip?.Sha ?? "";
            return new GitSnapshot
            {
                Branch = repository.Head.FriendlyName,
                ShortCommit = sha.Length >= 7 ? sha[..7] : sha,
                ChangedFiles = repository.RetrieveStatus().Count(s => s.State != FileStatus.Ignored && s.State != FileStatus.Unaltered)
            };
        }
        catch { return new GitSnapshot(); }
    }

    public static GitPanelInfo ReadPanel(string? folder)
    {
        var info = new GitPanelInfo();
        if (string.IsNullOrWhiteSpace(folder)) return info;
        try
        {
            var repositoryPath = Repository.Discover(folder);
            if (repositoryPath is null) return info;
            using var repository = new Repository(repositoryPath);
            var workdir = repository.Info.WorkingDirectory;
            var sha = repository.Head.Tip?.Sha ?? "";
            info.IsRepository = true;
            info.Branch = repository.Head.FriendlyName;
            info.ShortCommit = sha.Length >= 7 ? sha[..7] : sha;
            info.Ahead = repository.Head.TrackingDetails.AheadBy ?? 0;
            info.Behind = repository.Head.TrackingDetails.BehindBy ?? 0;
            foreach (var entry in repository.RetrieveStatus(new StatusOptions { IncludeIgnored = true, IncludeUntracked = true, RecurseUntrackedDirs = true }))
            {
                var state = entry.State;
                if (state == FileStatus.Unaltered || state == FileStatus.Nonexistent) continue;
                var change = new GitChange
                {
                    RelativePath = entry.FilePath.Replace('\\', '/'),
                    FullPath = System.IO.Path.Combine(workdir, entry.FilePath),
                    Status = ToLetter(state)
                };
                if (state.HasFlag(FileStatus.Ignored)) info.Ignored.Add(change);
                else info.Changes.Add(change);
            }
            info.Changes.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
            info.Ignored.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        }
        catch { }
        return info;
    }

    private static string ToLetter(FileStatus s)
    {
        if (s.HasFlag(FileStatus.Conflicted)) return "C";
        if (s.HasFlag(FileStatus.NewInWorkdir)) return "U";
        if (s.HasFlag(FileStatus.NewInIndex)) return "A";
        if (s.HasFlag(FileStatus.DeletedFromWorkdir) || s.HasFlag(FileStatus.DeletedFromIndex)) return "D";
        return "M";
    }

    /// <summary>Text of the file at HEAD, or null when the file is not tracked yet.</summary>
    public static string? ReadHeadText(string? folder, string relativePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var path = Repository.Discover(folder);
            if (path is null) return null;
            using var repository = new Repository(path);
            var entry = repository.Head.Tip?[relativePath];
            return (entry?.Target as Blob)?.GetContentText();
        }
        catch { return null; }
    }

    public static string? ReadOriginUrl(string? folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            var path = Repository.Discover(folder);
            if (path is null) return null;
            using var repository = new Repository(path);
            var url = repository.Network.Remotes["origin"]?.Url;
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) url = "https://github.com/" + url["git@github.com:".Length..];
            return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url[..^4] : url;
        }
        catch { return null; }
    }

    public static void Initialize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("Buka folder project terlebih dahulu.");
        Repository.Init(folder);
    }

    public static async Task<string> CloneRepositoryAsync(string repoUrl, string parentDirectory, string? token = null)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
            throw new InvalidOperationException("Masukkan URL Git Remote yang valid.");
        var cleanUrl = repoUrl.Trim();
        if (!cleanUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !cleanUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            cleanUrl = "https://" + cleanUrl.TrimStart('/');

        var repoName = Path.GetFileNameWithoutExtension(new Uri(cleanUrl).AbsolutePath.Trim('/'));
        if (string.IsNullOrWhiteSpace(repoName)) repoName = "cloned-project";

        Directory.CreateDirectory(parentDirectory);
        var destination = Path.Combine(parentDirectory, repoName);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            destination = Path.Combine(parentDirectory, $"{repoName}-{DateTime.Now:HHmmss}");

        await Task.Run(() =>
        {
            try
            {
                var options = new CloneOptions();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    options.FetchOptions.CredentialsProvider = (_, _, _) =>
                        new UsernamePasswordCredentials { Username = token, Password = string.Empty };
                }
                Repository.Clone(cleanUrl, destination, options);
            }
            catch
            {
                // Fallback to git CLI if installed
                var psi = new ProcessStartInfo("git", $"clone \"{cleanUrl}\" \"{destination}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Gagal memulai git clone.");
                proc.WaitForExit(60_000);
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("Git clone gagal: " + proc.StandardError.ReadToEnd());
            }
        });

        return destination;
    }

    /// <summary>Creates a GitHub repository, initializes git when needed, commits and pushes. Returns the repository URL.</summary>
    public static async Task<string> PublishToGitHubAsync(string? folder, string token, bool isPrivate)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("Buka folder project terlebih dahulu.");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Isi token GitHub di Settings terlebih dahulu.");
        var name = new string(System.IO.Path.GetFileName(folder.TrimEnd('\\', '/')).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        if (name.Length == 0) name = "depup-project";

        var client = new Octokit.GitHubClient(new Octokit.ProductHeaderValue("DepupCode")) { Credentials = new Octokit.Credentials(token) };
        Octokit.Repository created;
        try { created = await client.Repository.Create(new Octokit.NewRepository(name) { Private = isPrivate }); }
        catch (Exception ex) { throw new InvalidOperationException("GitHub menolak pembuatan repository: " + ex.Message); }

        await Task.Run(() =>
        {
            if (Repository.Discover(folder) is null) Repository.Init(folder);
            using var repository = new Repository(Repository.Discover(folder)!);
            Commands.Stage(repository, "*");
            var signature = repository.Config.BuildSignature(DateTimeOffset.Now) ?? new Signature("Depup Code", "depup@local", DateTimeOffset.Now);
            if (repository.Head.Tip is null || repository.RetrieveStatus().IsDirty)
                repository.Commit("Initial commit", signature, signature, new CommitOptions { AllowEmptyCommit = repository.Head.Tip is null });
            if (repository.Head.FriendlyName == "master") repository.Branches.Rename(repository.Head, "main");
            var remote = repository.Network.Remotes["origin"] ?? repository.Network.Remotes.Add("origin", created.CloneUrl);
            repository.Branches.Update(repository.Head, b => { b.Remote = remote.Name; b.UpstreamBranch = repository.Head.CanonicalName; });
            repository.Network.Push(repository.Head, new PushOptions
            {
                CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = token, Password = string.Empty }
            });
        });
        return created.HtmlUrl;
    }

    /// <summary>Stages everything, commits and (when a token and remote exist) pushes. Returns a user-facing message.</summary>
    public static string CommitAndPush(string? folder, string message, string? token)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new InvalidOperationException("Tulis commit message terlebih dahulu.");
        var path = string.IsNullOrWhiteSpace(folder) ? null : Repository.Discover(folder);
        if (path is null) throw new InvalidOperationException("Folder ini bukan repository Git.");
        using var repository = new Repository(path);
        Commands.Stage(repository, "*");
        if (!repository.RetrieveStatus().IsDirty) throw new InvalidOperationException("Tidak ada perubahan untuk di-commit.");
        var signature = repository.Config.BuildSignature(DateTimeOffset.Now) ?? new Signature("Depup Code", "depup@local", DateTimeOffset.Now);
        repository.Commit(message.Trim(), signature, signature);

        var remote = repository.Network.Remotes["origin"];
        if (remote is null) return "Commit dibuat. Remote 'origin' tidak ditemukan, push dilewati.";
        if (string.IsNullOrWhiteSpace(token)) return "Commit dibuat. Token GitHub belum diisi di Settings, push dilewati.";
        if (!repository.Head.IsTracking)
            repository.Branches.Update(repository.Head, b => { b.Remote = remote.Name; b.UpstreamBranch = repository.Head.CanonicalName; });
        repository.Network.Push(repository.Head, new PushOptions
        {
            CredentialsProvider = (_, _, _) => new UsernamePasswordCredentials { Username = token, Password = string.Empty }
        });
        return "Commit & push berhasil.";
    }
}
