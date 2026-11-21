using AuroraOperator.Core.Agent;

namespace AuroraOperator.Core.Tools;

/// <summary>
/// Filesystem tool ("filesystem"): search, create-folder, organize, rename,
/// copy, move, list, plus write-file. All operations use System.IO.
/// No delete action is offered: deletion is approval-gated elsewhere and is
/// never performed silently by this tool.
/// </summary>
public sealed class FilesystemTools : IAgentTool
{
    public string Name => "filesystem";
    public string Description => "Search, list, create folders, organise by extension, rename, copy, move and write files.";

    public Task<ToolResult> ExecuteAsync(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var action = (Get(args, "action") ?? "list").Trim().ToLowerInvariant();
        var path = Get(args, "path") ?? ".";
        var pattern = Get(args, "pattern") ?? "*.*";
        var destination = Get(args, "destination");

        try
        {
            ct.ThrowIfCancellationRequested();
            // A path containing a NUL byte can never be a real file path:
            // reject it with a clear failure instead of letting System.IO
            // throw an ArgumentException that would read as a generic error.
            // (There is no sandboxed base directory in this app — the user
            // operates their own machine, so traversal outside a base dir
            // is not applicable; see LIMITATIONS.md. Copy/move/rename never
            // overwrite: copy uses overwrite:false, move/rename throw when
            // the destination exists, and writing over an existing file is
            // classified SensitiveFileModification and approval-gated by
            // the engine before this tool is ever called.)
            if (path.Contains('\0') || (destination is not null && destination.Contains('\0')))
            {
                return Task.FromResult(new ToolResult(false,
                    "Filesystem action rejected: the path contains a null character and is not a valid file path."));
            }
            switch (action)
            {
                case "search":
                {
                    var found = SearchFiles(path, pattern);
                    return Task.FromResult(new ToolResult(true,
                        $"Found {found.Count} file(s) matching '{pattern}' under {path}.",
                        Details: string.Join("\n", found.Take(500)),
                        Artifacts: found.Take(500).ToList()));
                }
                case "create-folder":
                {
                    var created = CreateFolder(path);
                    return Task.FromResult(new ToolResult(true, $"Folder created: {created}",
                        OutputPath: created, Artifacts: new[] { created }));
                }
                case "organize":
                {
                    var moved = OrganizeByExtension(path);
                    return Task.FromResult(new ToolResult(true,
                        $"Organised {moved.Count} file(s) in {path} into subfolders by extension.",
                        Details: string.Join("\n", moved),
                        Artifacts: moved.ToList()));
                }
                case "list":
                {
                    var entries = ListEntries(path);
                    return Task.FromResult(new ToolResult(true,
                        $"Listed {entries.Count} entr(ies) in {path}.",
                        Details: string.Join("\n", entries.Take(500)),
                        Artifacts: entries.Take(500).ToList()));
                }
                case "rename":
                {
                    if (destination is null)
                        return Task.FromResult(new ToolResult(false, "Rename needs a 'destination' argument (new name or path)."));
                    Rename(path, destination);
                    return Task.FromResult(new ToolResult(true, $"Renamed {path} to {destination}.",
                        OutputPath: destination, Artifacts: new[] { destination }));
                }
                case "copy":
                {
                    if (destination is null)
                        return Task.FromResult(new ToolResult(false, "Copy needs a 'destination' argument."));
                    Copy(path, destination);
                    return Task.FromResult(new ToolResult(true, $"Copied {path} to {destination}.",
                        OutputPath: destination, Artifacts: new[] { destination }));
                }
                case "move":
                {
                    if (destination is null)
                        return Task.FromResult(new ToolResult(false, "Move needs a 'destination' argument."));
                    Move(path, destination);
                    return Task.FromResult(new ToolResult(true, $"Moved {path} to {destination}.",
                        OutputPath: destination, Artifacts: new[] { destination }));
                }
                case "write-file":
                {
                    // Honest failure, never an empty/garbage file: a write
                    // with no text to write is a caller error (the Planner
                    // omits "text" when ExtractTextToWrite found nothing).
                    if (Get(args, "text") is null && Get(args, "content") is null)
                        return Task.FromResult(new ToolResult(false,
                            "no text provided: write-file needs a 'text' argument and none was supplied, so nothing was written."));
                    var text = Get(args, "text") ?? Get(args, "content") ?? string.Empty;
                    if (text.Length == 0)
                        return Task.FromResult(new ToolResult(false,
                            "no text provided: write-file was given empty text, so nothing was written."));
                    var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                    return Task.FromResult(new ToolResult(true, $"Wrote {text.Length} characters to {path}.",
                        OutputPath: path, Artifacts: new[] { path }));
                }
                default:
                    return Task.FromResult(new ToolResult(false,
                        $"Unknown filesystem action '{action}'. Supported: search, create-folder, organize, rename, copy, move, list, write-file."));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult(false, $"Filesystem action '{action}' failed: {ex.Message}", ex.ToString()));
        }
    }

    private static string? Get(IReadOnlyDictionary<string, string> args, string key)
    {
        foreach (var kv in args)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return null;
    }

    /// <summary>Recursively finds files under <paramref name="root"/> matching <paramref name="pattern"/> (e.g. "*.pdf"). Inaccessible folders are skipped.</summary>
    public static IReadOnlyList<string> SearchFiles(string root, string pattern)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(root)) root = ".";
        if (string.IsNullOrWhiteSpace(pattern)) pattern = "*.*";
        if (File.Exists(root))
        {
            results.Add(Path.GetFullPath(root));
            return results;
        }
        if (!Directory.Exists(root)) return results;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchType = MatchType.Simple,
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, options))
                results.Add(file);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Fall back to a manual walk that skips unreadable folders.
            results.Clear();
            Walk(root, pattern, results);
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private static void Walk(string dir, string pattern, List<string> results)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(dir, pattern); }
        catch (Exception) { return; }
        results.AddRange(files);
        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch (Exception) { return; }
        foreach (var sub in subdirs) Walk(sub, pattern, results);
    }

    /// <summary>Creates the folder (including parents) and returns its full path.</summary>
    public static string CreateFolder(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>
    /// Moves each file in the top level of <paramref name="folder"/> into a subfolder
    /// named after its (lower-case) extension, or "no-extension". Name collisions get
    /// a numeric suffix. Returns the destination paths of the moved files.
    /// </summary>
    public static IReadOnlyList<string> OrganizeByExtension(string folder)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"Folder not found: {folder}");
        var moved = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            var subName = string.IsNullOrEmpty(ext) ? "no-extension" : ext;
            var targetDir = Path.Combine(folder, subName);
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, Path.GetFileName(file));
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                continue;
            target = UniquePath(target);
            File.Move(file, target);
            moved.Add(target);
        }
        return moved;
    }

    public static IReadOnlyList<string> ListEntries(string path)
    {
        if (File.Exists(path)) return new[] { Path.GetFullPath(path) };
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Path not found: {path}");
        var entries = new List<string>();
        entries.AddRange(Directory.EnumerateDirectories(path).Select(d => d + Path.DirectorySeparatorChar));
        entries.AddRange(Directory.EnumerateFiles(path));
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    public static void Rename(string path, string newPath)
    {
        var target = Path.IsPathRooted(newPath)
            ? newPath
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", newPath);
        if (Directory.Exists(path)) Directory.Move(path, target);
        else if (File.Exists(path)) File.Move(path, target);
        else throw new FileNotFoundException($"Nothing to rename at: {path}");
    }

    public static void Copy(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, destination);
            return;
        }
        if (!File.Exists(source)) throw new FileNotFoundException($"Nothing to copy at: {source}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(source, destination, overwrite: false);
    }

    public static void Move(string source, string destination)
    {
        if (Directory.Exists(source)) { Directory.Move(source, destination); return; }
        if (!File.Exists(source)) throw new FileNotFoundException($"Nothing to move at: {source}");
        var dir = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Move(source, destination);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    private static string UniquePath(string target)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var dir = Path.GetDirectoryName(target)!;
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
