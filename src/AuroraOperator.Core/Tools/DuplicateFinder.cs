using System.Security.Cryptography;

namespace AuroraOperator.Core.Tools;

/// <summary>A group of files with identical content.</summary>
public sealed record DuplicateGroup(string Hash, long SizeBytes, IReadOnlyList<string> Paths);

/// <summary>
/// Finds duplicate files by grouping on size first, then SHA256 hash.
/// Read-only: it never deletes, moves or modifies anything.
/// </summary>
public static class DuplicateFinder
{
    public static IReadOnlyList<DuplicateGroup> Find(string root, CancellationToken ct)
    {
        var groups = new List<DuplicateGroup>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return groups;

        var bySize = new Dictionary<long, List<string>>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*", options).ToList(); }
        catch (Exception) { return groups; }

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            long size;
            try { size = new FileInfo(file).Length; }
            catch (Exception) { continue; }
            if (!bySize.TryGetValue(size, out var list)) bySize[size] = list = new List<string>();
            list.Add(file);
        }

        foreach (var (size, candidates) in bySize)
        {
            if (candidates.Count < 2) continue;
            var byHash = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var hash = TryHash(file);
                if (hash is null) continue;
                if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<string>();
                list.Add(file);
            }
            foreach (var (hash, paths) in byHash)
            {
                if (paths.Count < 2) continue;
                paths.Sort(StringComparer.OrdinalIgnoreCase);
                groups.Add(new DuplicateGroup(hash, size, paths));
            }
        }

        groups.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        return groups;
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch (Exception)
        {
            // Unreadable / locked / vanished files are skipped, not treated as duplicates.
            return null;
        }
    }
}
