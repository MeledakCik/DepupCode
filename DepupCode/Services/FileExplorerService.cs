using DepupCode.Models;
using System.IO;

namespace DepupCode.Services;

public sealed class FileExplorerService : IDisposable
{
    private FileSystemWatcher? _watcher;
    private string? _root;
    private readonly object _gate = new();
    private Timer? _debounce;
    public event EventHandler? Changed;

    public string? RootPath => _root;

    public void Watch(string folder)
    {
        _watcher?.Dispose();
        _root = folder;
        if (!Directory.Exists(folder)) return;
        _watcher = new FileSystemWatcher(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
    }

    public List<FileNode> ReadTree(string folder)
    {
        var result = new List<FileNode>();
        if (!Directory.Exists(folder)) return result;
        try
        {
            result.AddRange(Directory.EnumerateDirectories(folder)
                .Where(d => !IgnoredDirectory(Path.GetFileName(d)))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(d => BuildNode(d, true, 0)));
            result.AddRange(Directory.EnumerateFiles(folder)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(f => BuildNode(f, false, 0)));
        }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    private FileNode BuildNode(string path, bool directory, int depth)
    {
        var node = new FileNode { Name = Path.GetFileName(path), FullPath = path, IsDirectory = directory };
        if (!directory || depth >= 6) return node;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path)
                         .Where(d => !IgnoredDirectory(Path.GetFileName(d)))
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(BuildNode(dir, true, depth + 1));
            foreach (var file in Directory.EnumerateFiles(path)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                node.Children.Add(BuildNode(file, false, depth + 1));
        }
        catch (UnauthorizedAccessException) { }
        return node;
    }

    private static bool IgnoredDirectory(string name) =>
        name is ".git" or "node_modules" or "bin" or "obj" or ".next" or ".venv" or "venv" or ".vs";

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, 350, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _watcher?.Dispose();
    }
}
