using System.Text.RegularExpressions;

namespace AuroraOperator.Core.Agent;

/// <summary>Keyword-based understanding of a user request. No LLM is needed.</summary>
public sealed record UserIntent(
    string DesiredOutcome,
    IReadOnlyList<string> Applications,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Risks,
    string CompletionCondition);

/// <summary>
/// Keyword-based intent parsing. Detects: write/notepad, folder, pdf, csv/excel,
/// duplicates, browser/research, python/project, diagnostics/slow, organize,
/// powerpoint, word, delete. Also extracts file paths and file names from the request.
/// </summary>
public static class IntentParser
{
    private static readonly Regex PathRegex = new(
        @"(?:[A-Za-z]:\\|\/|\~\/)[^\s""'<>|]+",
        RegexOptions.Compiled);

    private static readonly Regex FileNameRegex = new(
        @"[\w\-.]+\.(?:pdf|csv|xlsx|xls|pptx|ppt|docx|doc|txt|py|md|json|png|jpg|jpeg)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuotedRegex = new(
        @"""([^""]+)""|'([^']+)'",
        RegexOptions.Compiled);

    public static UserIntent Parse(string request)
    {
        var text = (request ?? string.Empty).Trim();
        var lower = text.ToLowerInvariant();
        var kinds = DetectKinds(lower);

        var applications = new List<string>();
        var resources = new List<string>();
        var risks = new List<string>();
        var outcomeParts = new List<string>();
        string completion;

        foreach (var path in ExtractPaths(text))
        {
            if (!resources.Contains(path, StringComparer.OrdinalIgnoreCase))
                resources.Add(path);
        }
        foreach (var file in ExtractFileNames(text))
        {
            if (!resources.Contains(file, StringComparer.OrdinalIgnoreCase))
                resources.Add(file);
        }

        if (kinds.Contains("write"))
        {
            applications.Add("Notepad");
            outcomeParts.Add("write the requested text/document");
            completion = "The requested text is written to a file and its location is reported.";
        }
        else if (kinds.Contains("powerpoint"))
        {
            applications.Add("PowerPoint");
            outcomeParts.Add("create a presentation");
            completion = "A .pptx file is created and its location is reported.";
        }
        else if (kinds.Contains("word"))
        {
            applications.Add("Word");
            outcomeParts.Add("create a Word report/document");
            completion = "A .docx file is created and its location is reported.";
        }
        else
        {
            completion = "The requested work is done and the result is reported back.";
        }

        if (kinds.Contains("folder"))
        {
            outcomeParts.Add("create the requested folder(s)");
            completion = "The requested folder exists and its location is reported.";
        }
        if (kinds.Contains("pdf"))
        {
            applications.Add("PDF reader");
            outcomeParts.Add("read / summarise the PDF");
            completion = "The PDF text is read and summarised.";
        }
        if (kinds.Contains("csv"))
        {
            applications.Add("Excel");
            outcomeParts.Add("convert the CSV data into an Excel workbook");
            completion = "An .xlsx workbook is created from the CSV and its location is reported.";
        }
        if (kinds.Contains("duplicates"))
        {
            outcomeParts.Add("find duplicate files");
            completion = "A list of duplicate file groups (same size and hash) is reported; nothing is deleted.";
        }
        if (kinds.Contains("browser"))
        {
            applications.Add("Browser");
            outcomeParts.Add("research the topic on the web");
            completion = "A sourced research summary is produced.";
        }
        if (kinds.Contains("python"))
        {
            applications.Add("Python");
            outcomeParts.Add("create / test a Python project");
            completion = "The Python project exists with a passing test run reported.";
        }
        if (kinds.Contains("diagnostics"))
        {
            outcomeParts.Add("diagnose the PC (CPU, RAM, disk, processes, network)");
            completion = "A diagnostics report with slow-PC findings is produced.";
        }
        if (kinds.Contains("organize"))
        {
            outcomeParts.Add("organise files by type");
            completion = "Files are grouped into folders by extension and the result is reported.";
        }
        if (kinds.Contains("delete"))
        {
            outcomeParts.Add("delete the specified files/folders");
            risks.Add("Deleting files is irreversible; only the exact scope the user named may be removed.");
            completion = "Only the explicitly named files/folders are deleted, after approval.";
        }

        if (kinds.Contains("delete-all"))
        {
            risks.Add("Broad delete requested ('delete all' / 'delete everything'): refused pending a specific scope.");
            completion = "No files are deleted until the user names a specific scope.";
        }
        if (kinds.Contains("browser"))
            risks.Add("Web research needs network access; pages may be blocked by CAPTCHA or login walls.");
        if (kinds.Contains("python"))
            risks.Add("Running code executes it on this PC; only the project's own tests are run.");
        if (resources.Count == 0 && kinds.Contains("pdf"))
            risks.Add("No PDF path was named in the request; the user will need to provide one.");
        if (resources.Count == 0 && kinds.Contains("csv"))
            risks.Add("No CSV path was named in the request; the user will need to provide one.");

        if (outcomeParts.Count == 0)
        {
            outcomeParts.Add("answer / carry out the request: " + (text.Length > 120 ? text[..120] + "…" : text));
        }

        var desired = string.Join("; ", outcomeParts.Distinct(StringComparer.OrdinalIgnoreCase));
        return new UserIntent(
            DesiredOutcome: desired,
            Applications: applications.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Resources: resources,
            Risks: risks.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            CompletionCondition: completion);
    }

    /// <summary>Ordered list of intent kinds detected in the request (lower-cased input).</summary>
    internal static IReadOnlyList<string> DetectKinds(string lower)
    {
        var kinds = new List<string>();
        void Add(string kind, bool condition)
        {
            if (condition && !kinds.Contains(kind)) kinds.Add(kind);
        }

        Add("delete-all",
            lower.Contains("delete all") || lower.Contains("delete everything") ||
            lower.Contains("remove all") || lower.Contains("remove everything") ||
            lower.Contains("delete every") || lower.Contains("wipe everything") ||
            lower.Contains("format") || lower.Contains("rm -rf"));
        Add("delete",
            lower.Contains("delete") || lower.Contains("remove ") || lower.Contains("erase") ||
            lower.Contains("trash") || kinds.Contains("delete-all"));
        Add("write",
            lower.Contains("notepad") || lower.Contains("write") || lower.Contains("note") ||
            lower.Contains("draft") || lower.Contains("letter") || lower.Contains("type out") ||
            lower.Contains("jot down"));
        Add("folder",
            lower.Contains("folder") || lower.Contains("directory") || lower.Contains("create a new") ||
            lower.Contains("make a folder") || lower.Contains("new folder"));
        Add("pdf",
            lower.Contains("pdf") || lower.Contains("summarise") || lower.Contains("summarize"));
        Add("csv",
            lower.Contains("csv") || lower.Contains("excel") || lower.Contains("spreadsheet") ||
            lower.Contains("workbook") || lower.Contains(".xlsx"));
        Add("duplicates",
            lower.Contains("duplicate") || lower.Contains("duplicates") || lower.Contains("same file") ||
            lower.Contains("copies of"));
        Add("browser",
            lower.Contains("browser") || lower.Contains("research") || lower.Contains("search the web") ||
            lower.Contains("google") || lower.Contains("look up") || lower.Contains("find online") ||
            lower.Contains("on the internet") || lower.Contains("web search"));
        Add("python",
            lower.Contains("python") || lower.Contains("pytest") || lower.Contains("run tests") ||
            lower.Contains("run the tests") || lower.Contains("project") || lower.Contains("code") ||
            lower.Contains("script"));
        Add("diagnostics",
            lower.Contains("slow") || lower.Contains("diagnos") || lower.Contains("performance") ||
            lower.Contains("cpu") || lower.Contains("memory") || lower.Contains("ram") ||
            lower.Contains("disk space") || lower.Contains("why is my pc") || lower.Contains("lagging") ||
            lower.Contains("freezing"));
        Add("organize",
            lower.Contains("organize") || lower.Contains("organise") || lower.Contains("sort") ||
            lower.Contains("tidy") || lower.Contains("clean up") || lower.Contains("cleanup") ||
            lower.Contains("group by"));
        Add("powerpoint",
            lower.Contains("powerpoint") || lower.Contains("presentation") || lower.Contains("slides") ||
            lower.Contains("pptx") || lower.Contains("slide deck") || lower.Contains("deck"));
        Add("word",
            lower.Contains("word document") || lower.Contains("word report") || lower.Contains(".docx") ||
            lower.Contains("report document"));
        return kinds;
    }

    /// <summary>
    /// Extracts the actual text the user asked to write, preserving Unicode
    /// (Chinese/Urdu), newlines and punctuation. Handles: quoted text
    /// ("write \"Hello World\""), a colon ("write the following: Hello"),
    /// and bare phrasing ("write Hello World", "Open Notepad and write
    /// Hello World", "写你好世界"). Returns null when the request names no
    /// actual text (e.g. "write a file to /tmp/x.txt") — callers must fail
    /// honestly with "no text provided" instead of writing empty/garbage.
    /// </summary>
    public static string? ExtractTextToWrite(string request)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        var text = request.Trim();

        // Index of the write verb, so quoted text / colons before it (e.g. a
        // quoted file path) are not mistaken for the text to write.
        var verbIndex = IndexOfWriteVerb(text);

        // Urdu word order puts the content BEFORE the verb
        // ("Notepad میں السلام علیکم لکھیں"): -2 marks that case.
        if (verbIndex == -2)
        {
            var urdu = text.IndexOf("لکھ", StringComparison.Ordinal);
            var before = CleanExtractedText(text[..urdu]);
            return string.IsNullOrWhiteSpace(before) || IsGenericPlaceholder(before) ? null : before;
        }

        // 1. Quoted text after the write verb. A quoted value that is only a
        // path / file name is a destination, not text.
        foreach (Match m in QuotedRegex.Matches(text))
        {
            var quoted = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (quoted.Length == 0 || LooksLikePathOnly(quoted)) continue;
            if (verbIndex >= 0 && m.Index < verbIndex) continue;
            if (verbIndex < 0 && !text.Contains("notepad", StringComparison.OrdinalIgnoreCase)) continue;
            return quoted;
        }
        // Curly quotes (“…”).
        var curly = Regex.Match(text, "“([^”]+)”", RegexOptions.Singleline);
        if (curly.Success && (verbIndex < 0 || curly.Index > verbIndex))
        {
            var quoted = curly.Groups[1].Value.Trim();
            if (quoted.Length > 0 && !LooksLikePathOnly(quoted)) return quoted;
        }

        string? remainder = null;
        if (verbIndex >= 0)
        {
            var afterVerb = text[(verbIndex + WriteVerbLength(text, verbIndex))..];
            // 2. Colon form: "write ... : <text>" / "写：<text>".
            var colon = afterVerb.IndexOfAny(new[] { ':', '：' });
            if (colon >= 0)
                remainder = afterVerb[(colon + 1)..];
            else
                remainder = afterVerb;
        }
        else
        {
            // No English/Chinese write verb: a colon after "notepad" still
            // introduces text ("Notepad: Hello").
            var colon = text.IndexOf(':');
            if (colon >= 0 && text[..colon].Contains("notepad", StringComparison.OrdinalIgnoreCase))
                remainder = text[(colon + 1)..];
        }

        if (remainder is null) return null;
        var cleaned = CleanExtractedText(remainder);
        return string.IsNullOrWhiteSpace(cleaned) || IsGenericPlaceholder(cleaned) ? null : cleaned;
    }

    private static int IndexOfWriteVerb(string text)
    {
        var lower = text.ToLowerInvariant();
        string[] verbs = { "jot down", "type out", "write", "type", "draft" };
        var best = -1;
        foreach (var verb in verbs)
        {
            var i = lower.IndexOf(verb, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        // Chinese 写 / 输入 are positional verb markers.
        foreach (var ch in new[] { '写', '输' })
        {
            var i = text.IndexOf(ch);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }
        if (best < 0)
        {
            // Urdu: text before "لکھیں/لکھو" is the content in Urdu word
            // order ("Notepad میں السلام علیکم لکھیں").
            var urdu = text.IndexOf("لکھ", StringComparison.Ordinal);
            if (urdu > 0)
            {
                var before = CleanExtractedText(text[..urdu]);
                if (!string.IsNullOrWhiteSpace(before) && !IsGenericPlaceholder(before))
                    return -2; // marker: caller handles via the Urdu branch below
            }
        }
        return best;
    }

    private static int WriteVerbLength(string text, int verbIndex)
    {
        var rest = text[verbIndex..].ToLowerInvariant();
        foreach (var verb in new[] { "jot down", "type out", "write", "type", "draft" })
            if (rest.StartsWith(verb, StringComparison.Ordinal)) return verb.Length;
        return 1; // single Chinese character verb
    }

    /// <summary>Strips a trailing destination ("to /path", "and save it to …", "in Notepad") and surrounding quotes from extracted text.</summary>
    private static string CleanExtractedText(string raw)
    {
        var s = (raw ?? string.Empty).Trim();
        // Trailing "and save (it) (to/in …)" clause.
        s = Regex.Replace(s, @"\s+and\s+save(?:\s+it)?(?:\s+(?:to|in|into)\s+\S.*)?$", string.Empty, RegexOptions.IgnoreCase | RegexOptions.Singleline).Trim();
        // Trailing destination: "to/in/into <path or file>".
        s = Regex.Replace(s, @"\s+(?:to|in|into)\s+(?:the\s+)?(?:file\s+)?(?:[A-Za-z]:\\|/|\~/|[\w\-.]+(?:\.(?:txt|md|docx?|pdf|csv)))\S*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        // Trailing "in/to Notepad".
        s = Regex.Replace(s, @"\s+(?:in|to|into|on)\s+notepad\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        // Leading "in/to Notepad" left over from "write in Notepad: …" phrasing.
        s = Regex.Replace(s, @"^(?:in|to|into|on)\s+notepad\s*[:：]?\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        // Surrounding quotes (only when they wrap the whole remainder).
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'') || (s[0] == '“' && s[^1] == '”')))
            s = s[1..^1].Trim();
        return s;
    }

    private static bool LooksLikePathOnly(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) return false;
        if (v.Contains('\\') || v.Contains('/')) return true;
        return Regex.IsMatch(v, @"^[\w\-. ]+\.(?:txt|md|docx?|pdf|csv|xlsx?|pptx?)$", RegexOptions.IgnoreCase)
            && !v.Contains(' ', StringComparison.Ordinal);
    }

    private static bool IsGenericPlaceholder(string value)
    {
        var lower = value.Trim().ToLowerInvariant();
        if (LooksLikePathOnly(value)) return true;
        string[] generic =
        {
            "a file", "the file", "a note", "the note", "a document", "the document",
            "some text", "the text", "a letter", "the letter", "something", "it",
            "a message", "the message", "a text file", "the text file", "a new file",
        };
        return generic.Contains(lower);
    }

    internal static IReadOnlyList<string> ExtractPaths(string request)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(request)) return found;
        foreach (Match m in PathRegex.Matches(request))
        {
            var p = m.Value.TrimEnd('.', ',', ';', ')', ']');
            if (!found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(p);
        }
        foreach (Match m in QuotedRegex.Matches(request))
        {
            var q = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (q.Length > 0 && (q.Contains('\\') || q.Contains('/') || q.Contains('.')) &&
                !found.Contains(q, StringComparer.OrdinalIgnoreCase))
                found.Add(q);
        }
        return found;
    }

    internal static IReadOnlyList<string> ExtractFileNames(string request)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(request)) return found;
        foreach (Match m in FileNameRegex.Matches(request))
        {
            if (!found.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) found.Add(m.Value);
        }
        return found;
    }

    internal static string? FirstPathOrDefault(string request, string? fallback = null)
    {
        var paths = ExtractPaths(request);
        if (paths.Count > 0) return paths[0];
        var files = ExtractFileNames(request);
        if (files.Count > 0) return files[0];
        return fallback;
    }
}
