using System.IO.Compression;

namespace DepupCode.Services;

/// <summary>
/// Ekstraksi ZIP yang membatasi hasil ke folder tujuan: menolak zip-slip,
/// path absolut, entri symlink, dan total ukuran yang tidak wajar.
/// </summary>
public static class SafeZipExtractor
{
    private const long DefaultMaxExpandedBytes = 1_500_000_000;

    public static void ExtractToDirectory(string archivePath, string destination, long maxExpandedBytes = DefaultMaxExpandedBytes)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Arsip runtime tidak ditemukan.", archivePath);
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Folder tujuan wajib diisi.", nameof(destination));

        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var archive = ZipFile.OpenRead(archivePath);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = (entry.FullName ?? "").Replace('\\', '/');
            if (name.Length == 0) continue;
            if (name.StartsWith('/') || name.StartsWith("//", StringComparison.Ordinal) ||
                (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':'))
                throw new InvalidDataException($"Arsip berisi path absolut yang ditolak: {entry.FullName}");

            var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;
            if (segments.Any(segment => segment is "." or ".." || segment.Contains(':')))
                throw new InvalidDataException($"Arsip berisi path tidak aman yang ditolak: {entry.FullName}");
            if (IsSymbolicLink(entry))
                throw new InvalidDataException($"Arsip berisi symlink yang ditolak: {entry.FullName}");

            expanded = checked(expanded + Math.Max(0, entry.Length));
            if (expanded > maxExpandedBytes)
                throw new InvalidDataException("Isi arsip melebihi batas ukuran yang diizinkan.");

            var target = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Entri arsip keluar dari folder tujuan: {entry.FullName}");

            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        try
        {
            // Unix mode lives in the high 16 bits of ExternalAttributes.
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            return mode == 0xA000;
        }
        catch { return false; }
    }
}
