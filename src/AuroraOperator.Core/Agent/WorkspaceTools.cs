using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuroraOperator.Core.Security;

namespace AuroraOperator.Core.Agent;

internal sealed class WorkspaceTools
{
    internal sealed record Tool(string Name, string Description, RiskLevel Risk,
        IReadOnlyDictionary<string, int> Fields, TimeSpan Timeout,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>> Execute);

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private const int MaxEntries = 1000;
    private readonly string _root;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal IReadOnlyDictionary<string, Tool> Registry { get; }

    internal WorkspaceTools(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var tools = new[]
        {
            Register("list", "List files and folders directly inside a folder. Use . for the workspace.", RiskLevel.ReadOnly, List, ("path", 260)),
            Register("duplicates", "Find identical top-level files by SHA-256; report only. Use . for the workspace.", RiskLevel.ReadOnly, Duplicates, ("path", 260)),
            Register("read-text", "Read a small UTF-8 text file for inspection.", RiskLevel.ReadOnly, ReadText, ("path", 260)),
            Register("create-folder", "Create one folder whose parent already exists.", RiskLevel.SensitiveFileModification, CreateFolder, ("path", 260)),
            Register("move-file", "Move one file to an existing folder. Destination includes the filename and must be unused.", RiskLevel.SensitiveFileModification, MoveFile, ("path", 260), ("destination", 260)),
            Register("write-new-text", "Create and read back a new UTF-8 text file. Parent must exist; existing files are refused.", RiskLevel.SensitiveFileModification, WriteText, ("path", 260), ("text", 12000)),
            Register("open-in-notepad", "Open an existing .txt file in Windows Notepad for the user.", RiskLevel.SensitiveFileModification, OpenInNotepad, ("path", 260)),
        };
        Registry = new ReadOnlyDictionary<string, Tool>(tools.ToDictionary(t => t.Name, StringComparer.Ordinal));
    }

    private static Tool Register(string name, string description, RiskLevel risk,
        Func<IReadOnlyDictionary<string, string>, CancellationToken, Task<ToolResult>> handler,
        params (string Name, int Limit)[] fields) =>
        new(name, description, risk, fields.ToDictionary(f => f.Name, f => f.Limit, StringComparer.Ordinal),
            TimeSpan.FromSeconds(30), handler);

    internal PlanStep Bind(string name, JsonElement input, int index)
    {
        if (!Registry.TryGetValue(name, out var tool) || input.ValueKind != JsonValueKind.Object)
            throw new FormatException("Unknown tool or invalid arguments object.");
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in input.EnumerateObject())
        {
            if (!tool.Fields.TryGetValue(field.Name, out int limit) || field.Value.ValueKind != JsonValueKind.String)
                throw new FormatException("Unexpected argument or non-string argument.");
            string value = field.Value.GetString()!;
            if (value.Length == 0 || value.Length > limit || value.Contains('\0'))
                throw new FormatException("Argument is empty or too large.");
            if (field.Name is "path" or "destination")
                value = Resolve(value, name is "list" or "duplicates");
            if (!args.TryAdd(field.Name, value))
                throw new FormatException("Duplicate argument.");
        }
        if (args.Count != tool.Fields.Count)
            throw new FormatException("Missing required argument.");
        var description = string.Join(Environment.NewLine, args.Select(arg => $"{arg.Key}: {arg.Value}"));
        return new PlanStep($"workspace-{index}", name, name, description, tool.Risk != RiskLevel.ReadOnly)
        {
            Args = new ReadOnlyDictionary<string, string>(args),
            WorkspaceTool = tool,
        };
    }

    private string Resolve(string relative, bool allowRoot)
    {
        if (relative == "." && allowRoot)
        {
            CheckLocalPath(_root);
            return _root;
        }
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new FormatException("Use a relative path in the selected workspace.");
        foreach (var component in relative.Split(['\\', '/']))
        {
            if (component.Length == 0 || component.EndsWith('.') || component.EndsWith(' ') ||
                component.Any(c => char.IsControl(c) || "<>\"|?*".Contains(c)))
                throw new FormatException("Invalid workspace path component.");
            string stem = component.Split('.')[0].TrimEnd().ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) &&
                "0123456789¹²³".Contains(stem[3]))
                throw new FormatException("Windows device paths are refused.");
        }
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        var prefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, _comparison))
            throw new FormatException("Path escapes the selected workspace.");
        CheckLocalPath(full);
        return full;
    }

    private static void CheckLocalPath(string path)
    {
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network))
            throw new FormatException("Network and device paths are refused.");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new FormatException("Symbolic links, junctions and other reparse points are refused.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string Checked(IReadOnlyDictionary<string, string> args, string key = "path")
    {
        string path = args[key];
        CheckLocalPath(path);
        return path;
    }

    private static Task<ToolResult> List(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var names = Enumerate(Checked(args), "*", ct);
        return Task.FromResult(new ToolResult(true, $"Listed {names.Count} workspace entries.",
            string.Join("\n", names.Select(p => Path.GetFileName(p) + (Directory.Exists(p) ? "/" : "")))));
    }

    private static async Task<ToolResult> Duplicates(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        var files = Enumerate(Checked(args), "*", ct).Where(File.Exists).Select(p => new FileInfo(p));
        var groups = new List<string>();
        long bytesRead = 0;
        foreach (var size in files.GroupBy(info => info.Length).Where(g => g.Count() > 1))
        {
            var hashes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var file in size)
            {
                ct.ThrowIfCancellationRequested();
                if (file.Length > 256L * 1024 * 1024 - bytesRead)
                    return new ToolResult(false, "Duplicate scan exceeded its 256 MiB read budget; select a smaller folder.");
                bytesRead += file.Length;
                CheckLocalPath(file.FullName);
                await using var stream = File.OpenRead(file.FullName);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
                if (!hashes.TryGetValue(hash, out var names))
                    hashes.Add(hash, names = []);
                names.Add(file.Name);
            }
            groups.AddRange(hashes.Values.Where(names => names.Count > 1).Select(names => JsonSerializer.Serialize(names)));
        }
        return new ToolResult(true, $"Found {groups.Count} duplicate groups.", string.Join("\n", groups));
    }

    private static List<string> Enumerate(string path, string pattern, CancellationToken ct)
    {
        var options = new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        var entries = new List<string>();
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, pattern, options))
        {
            ct.ThrowIfCancellationRequested();
            if (entries.Count == MaxEntries)
                throw new IOException("Folder exceeds the 1,000-entry limit; select a smaller folder.");
            entries.Add(entry);
        }
        entries.Sort(StringComparer.OrdinalIgnoreCase);
        return entries;
    }

    private static async Task<ToolResult> ReadText(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string path = Checked(args);
        await using var stream = File.OpenRead(path);
        var buffer = new byte[16385];
        int length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct).ConfigureAwait(false);
        if (length == buffer.Length)
            return new ToolResult(false, "Text file exceeds the 16 KiB inspection limit.");
        var content = Utf8.GetString(buffer, 0, length);
        if (content.Contains('\0'))
            return new ToolResult(false, "The file is not supported UTF-8 text.");
        return new ToolResult(true, $"Read {length} bytes from {Path.GetFileName(path)}.", content);
    }

    private static Task<ToolResult> CreateFolder(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string path = Checked(args);
        RequireParent(path);
        ct.ThrowIfCancellationRequested();
        bool existed = Directory.Exists(path);
        Directory.CreateDirectory(path);
        return Task.FromResult(Directory.Exists(Checked(args))
            ? new ToolResult(true, existed ? $"Folder already existed: {path}" : $"Created and verified folder: {path}", OutputPath: existed ? null : path)
            : new ToolResult(false, "The folder could not be verified."));
    }

    private static Task<ToolResult> MoveFile(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string source = Checked(args);
        string destination = Checked(args, "destination");
        RequireParent(destination);
        ct.ThrowIfCancellationRequested();
        File.Move(source, destination, overwrite: false);
        return Task.FromResult(File.Exists(Checked(args, "destination")) && !File.Exists(Checked(args))
            ? new ToolResult(true, $"Moved {source} to {destination} and verified both locations.", OutputPath: destination)
            : new ToolResult(false, "The move could not be verified."));
    }

    private static async Task<ToolResult> WriteText(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string path = Checked(args);
        string parent = RequireParent(path);
        string temporary = Path.Combine(parent, $".aurora-{Guid.NewGuid():N}.tmp");
        byte[] bytes = Utf8.GetBytes(args["text"]);
        bool created = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4096, FileOptions.Asynchronous))
            {
                created = true;
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                stream.Position = 0;
                var readback = new byte[bytes.Length];
                await stream.ReadExactlyAsync(readback, ct).ConfigureAwait(false);
                if (!bytes.AsSpan().SequenceEqual(readback))
                    return new ToolResult(false, "Written bytes did not match the requested text.");
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, Checked(args), overwrite: false);
            if (!File.Exists(Checked(args)))
                return new ToolResult(false, "The destination could not be verified.");
            return new ToolResult(true, $"Created {path} and verified {bytes.Length} bytes.", OutputPath: path);
        }
        finally
        {
            if (created) File.Delete(temporary);
        }
    }

    private static Task<ToolResult> OpenInNotepad(IReadOnlyDictionary<string, string> args, CancellationToken ct)
    {
        string path = Checked(args);
        if (!OperatingSystem.IsWindows() || !File.Exists(path) || !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new ToolResult(false, "Opening in Notepad requires an existing .txt file on Windows."));
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "notepad.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
        return Task.FromResult(process is null
            ? new ToolResult(false, "Windows did not start Notepad.")
            : new ToolResult(true, $"Windows accepted the Notepad launch for {path}. The editor window was not inspected."));
    }

    private static string RequireParent(string path)
    {
        string parent = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent))
            throw new DirectoryNotFoundException("The target's parent folder must already exist.");
        return parent;
    }
}
