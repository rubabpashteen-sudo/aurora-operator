using AuroraOperator.Core.Tools;

namespace AuroraOperator.Core.Agent;

/// <summary>
/// Builds a 3–10 step plan from the keyword intent of a task.
/// Machine data lives in each step's structured <see cref="PlanStep.Args"/>
/// dictionary (action, path, destination, name, text, verify, …); the
/// human-readable Description stays prose and is never relied on for
/// parameters (AgentEngine only falls back to scanning it for steps built
/// before Args existed). A broad delete ("delete all" / "delete
/// everything") is never planned as a destructive step: its first step
/// requires approval and refuses the broad scope.
/// </summary>
public static class Planner
{
    public static TaskPlan Plan(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var request = task.UserRequest ?? string.Empty;
        var lower = request.ToLowerInvariant();
        var kinds = IntentParser.DetectKinds(lower);
        var intent = IntentParser.Parse(request);
        var steps = new List<PlanStep>();
        var n = 0;
        string NextId() => "step-" + (++n);

        PlanStep Step(string title, string tool, string description,
            IReadOnlyDictionary<string, string>? args = null,
            bool approval = false, string? precondition = null, string? completion = null) =>
            new(NextId(), title, tool, description, approval, precondition, completion)
            {
                Args = args is null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(args, StringComparer.OrdinalIgnoreCase),
            };

        static Dictionary<string, string> A(params (string Key, string Value)[] pairs)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in pairs) d[key] = value;
            return d;
        }

        var defaultPath = IntentParser.FirstPathOrDefault(request, ".") ?? ".";

        if (kinds.Contains("delete-all"))
        {
            steps.Add(Step(
                "Confirm scope — broad delete refused pending specific scope",
                "filesystem",
                "Broad delete requested. No files will be deleted until the user names a specific file, folder or pattern. Ask the user for the exact scope.",
                A(("action", "list"), ("path", defaultPath)),
                approval: true,
                precondition: "User asked to delete all / everything.",
                completion: "User provides a specific delete scope, or the request is abandoned with nothing deleted."));
            steps.Add(Step(
                "List candidate files in scope (no deletion)",
                "filesystem",
                "List the files that would be in scope once a specific scope is given, so the user can confirm them.",
                A(("action", "list"), ("path", defaultPath)),
                precondition: "A specific scope has been named by the user.",
                completion: "A file list is shown to the user for confirmation."));
            steps.Add(Step(
                "Verify nothing was deleted and report",
                "filesystem",
                "Confirm no broad deletion happened and report the confirmed scope (or that the request was refused).",
                A(("action", "list"), ("path", defaultPath)),
                completion: "Report states exactly what was (not) deleted."));
            return Finish(task, intent, steps);
        }

        steps.Add(Step(
            "Understand the request",
            "filesystem",
            "Confirm the goal, the files/folders involved and the completion condition before changing anything. Resources named: " +
            (intent.Resources.Count > 0 ? string.Join(", ", intent.Resources) : "none named yet") + ".",
            A(("action", "list"), ("path", defaultPath)),
            completion: "Goal and resources are identified."));

        var documentsDir = GetDocumentsDir();

        foreach (var kind in kinds)
        {
            switch (kind)
            {
                case "folder":
                {
                    // The folder NAME ("Business Reports") and the CONTAINING
                    // path ("/tmp/...", "Desktop") are different things: the
                    // first path in the request is the container, not the
                    // folder to create. Combine them; never create the
                    // container itself as the "new" folder.
                    var folderName = ExtractFolderName(request);
                    var container = IntentParser.FirstPathOrDefault(request);
                    string path;
                    if (folderName is not null && container is not null &&
                        !container.EndsWith(folderName, StringComparison.OrdinalIgnoreCase))
                        path = Path.Combine(container, folderName);
                    else if (folderName is not null && lower.Contains("desktop"))
                        path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folderName);
                    else if (folderName is not null)
                        path = Path.Combine(documentsDir, folderName);
                    else
                        path = container ?? Path.Combine(documentsDir, "New Folder");
                    steps.Add(Step("Create folder", "filesystem",
                        "Create the requested folder at " + path + ".",
                        A(("action", "create-folder"), ("path", path), ("verify", "dir:" + path)),
                        completion: "Folder exists at " + path + "."));
                    break;
                }
                case "write":
                {
                    var text = IntentParser.ExtractTextToWrite(request);
                    var namedFile = IntentParser.FirstPathOrDefault(request);
                    string? file = null;
                    if (namedFile is not null)
                    {
                        file = Path.IsPathRooted(namedFile) ? namedFile : Path.Combine(documentsDir, namedFile);
                        if (!file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(file).Contains('.'))
                            file += ".txt";
                    }

                    if (lower.Contains("notepad"))
                    {
                        // Notepad flow through the "app" tool, not a bare
                        // file write: launch Notepad, type the real text,
                        // and save it only when the user named a path.
                        steps.Add(Step("Open Notepad", "app",
                            "Open Notepad so the text can be typed into it.",
                            A(("action", "launch-app"), ("app", "notepad")),
                            completion: "A Notepad window is open."));
                        var typeArgs = A(("action", "type-text"), ("app", "notepad"));
                        if (text is not null) typeArgs["text"] = text;
                        steps.Add(Step("Type the text into Notepad", "app",
                            text is not null
                                ? $"Type the requested text ({text.Length} characters) into Notepad."
                                : "No text was provided in the request, so nothing can be typed. Ask the user for the exact text to write.",
                            typeArgs,
                            completion: text is not null
                                ? "The requested text is in the Notepad window and read back to verify it."
                                : "The step fails honestly: no text was provided."));
                        if (file is not null)
                        {
                            var saveArgs = A(("action", "save-as"), ("app", "notepad"), ("path", file), ("verify", "file:" + file));
                            if (text is not null) saveArgs["text"] = text;
                            steps.Add(Step("Save the Notepad text", "app",
                                "Save the Notepad text to " + file + ".",
                                saveArgs,
                                completion: "Text file exists at " + file + " with exactly the requested content."));
                        }
                    }
                    else
                    {
                        file ??= Path.Combine(documentsDir, "AuroraNote.txt");
                        var writeArgs = A(("action", "write-file"), ("path", file), ("verify", "file:" + file));
                        if (text is not null) writeArgs["text"] = text;
                        steps.Add(Step("Write the requested text", "filesystem",
                            text is not null
                                ? $"Write the requested text ({text.Length} characters) to {file}."
                                : "No text was provided in the request, so nothing can be written. Ask the user for the exact text to write.",
                            writeArgs,
                            completion: text is not null
                                ? "Text file exists at " + file + "."
                                : "The step fails honestly: no text was provided."));
                    }
                    break;
                }
                case "pdf":
                {
                    var pdf = IntentParser.FirstPathOrDefault(request) ?? "the PDF named in the request";
                    steps.Add(Step("Read the PDF", "document",
                        "Read the PDF text with PdfPig from " + pdf + ".",
                        A(("action", "read-pdf"), ("path", pdf)),
                        completion: "PDF text is extracted."));
                    steps.Add(Step("Summarise the PDF", "document",
                        "Summarise the extracted PDF text in plain language.",
                        A(("action", "summarise-pdf"), ("path", pdf)),
                        completion: "A short summary of the PDF is produced."));
                    break;
                }
                case "csv":
                {
                    var csv = IntentParser.FirstPathOrDefault(request) ?? "the CSV named in the request";
                    var xlsx = Path.ChangeExtension(csv, ".xlsx") ?? Path.Combine(documentsDir, "output.xlsx");
                    steps.Add(Step("Convert CSV to Excel", "document",
                        "Convert the CSV into an Excel workbook with ClosedXML at " + xlsx + ".",
                        A(("action", "csv-to-excel"), ("path", csv), ("destination", xlsx), ("verify", "file:" + xlsx)),
                        completion: "Excel workbook exists at " + xlsx + "."));
                    break;
                }
                case "duplicates":
                {
                    var root = IntentParser.FirstPathOrDefault(request) ?? documentsDir;
                    steps.Add(Step("Find duplicate files", "duplicates",
                        "Group files by size, then SHA256 hash, and report groups with more than one path under " + root + ". Never delete.",
                        A(("action", "find-duplicates"), ("path", root)),
                        completion: "Duplicate groups are listed; nothing is deleted."));
                    break;
                }
                case "browser":
                {
                    steps.Add(Step("Research on the web", "browser",
                        "Research the topic in the browser and collect sourced findings.",
                        A(("action", "research"), ("path", Path.Combine(documentsDir, "research.md"))),
                        completion: "A sourced research summary is produced."));
                    break;
                }
                case "python":
                {
                    var dir = IntentParser.FirstPathOrDefault(request) ?? Path.Combine(documentsDir, "python-project");
                    steps.Add(Step("Create the Python project", "coding",
                        "Create a Python project (main.py, tests, README) under " + dir + ".",
                        A(("action", "create-project"), ("path", dir), ("name", "app")),
                        completion: "Project files exist under " + dir + "."));
                    steps.Add(Step("Run the project tests", "coding",
                        "Run pytest (fallback: unittest) and capture the real output and exit code.",
                        A(("action", "run-tests"), ("path", dir)),
                        completion: "Test output and exit code are reported honestly."));
                    break;
                }
                case "diagnostics":
                {
                    steps.Add(Step("Diagnose the PC", "diagnostics",
                        "Measure CPU, RAM, disk free space, top processes and network reachability.",
                        A(("action", "diagnose")),
                        completion: "A diagnostics report with slow-PC findings is produced."));
                    break;
                }
                case "organize":
                {
                    var folder = IntentParser.FirstPathOrDefault(request) ?? documentsDir;
                    steps.Add(Step("Organise files by extension", "filesystem",
                        "Move files in " + folder + " into subfolders named by their extension.",
                        A(("action", "organize"), ("path", folder)),
                        completion: "Files in " + folder + " are grouped by extension."));
                    break;
                }
                case "powerpoint":
                {
                    var pptx = Path.Combine(documentsDir, "Presentation.pptx");
                    var named = IntentParser.ExtractFileNames(request)
                        .FirstOrDefault(f => f.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase));
                    if (named is not null)
                        pptx = Path.IsPathRooted(named) ? named : Path.Combine(documentsDir, named);
                    steps.Add(Step("Create the presentation", "document",
                        "Build a PowerPoint deck from the request at " + pptx + ".",
                        A(("action", "create-powerpoint"), ("path", pptx), ("verify", "file:" + pptx)),
                        completion: "Presentation exists at " + pptx + "."));
                    break;
                }
                case "word":
                {
                    var docx = Path.Combine(documentsDir, "Report.docx");
                    steps.Add(Step("Create the Word report", "document",
                        "Build a Word report from the request at " + docx + ".",
                        A(("action", "create-word"), ("path", docx), ("verify", "file:" + docx)),
                        completion: "Word document exists at " + docx + "."));
                    break;
                }
                case "delete":
                {
                    var target = IntentParser.FirstPathOrDefault(request) ?? "the item named in the request";
                    steps.Add(Step("Confirm deletion scope", "filesystem",
                        "Ask the user to confirm the exact file or folder to delete before anything is removed. Item named: " + target + ".",
                        A(("action", "list"), ("path", target)),
                        approval: true,
                        precondition: "User named a specific item to delete: " + target + ".",
                        completion: "User confirms the exact deletion scope, or the step is skipped."));
                    break;
                }
            }
        }

        if (steps.Count == 1)
        {
            steps.Add(Step("Carry out the request", "filesystem",
                "Carry out the requested work using the available tools.",
                A(("action", "list"), ("path", IntentParser.FirstPathOrDefault(request) ?? documentsDir)),
                completion: intent.CompletionCondition));
        }

        steps.Add(Step("Verify and report", "filesystem",
            "Verify the result against the completion condition and report what was accomplished.",
            A(("action", "list"), ("path", IntentParser.FirstPathOrDefault(request) ?? documentsDir)),
            completion: intent.CompletionCondition));

        return Finish(task, intent, steps);
    }


    /// <summary>Extracts a folder name from "folder named X" / "folder X" phrasing.</summary>
    private static string? ExtractFolderName(string request)
    {
        var m = System.Text.RegularExpressions.Regex.Match(request,
            @"\bnamed\s+([\w][\w .\-]*?)\s+(?:in|on|at|under)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value.Trim().TrimEnd('.', '!');
        m = System.Text.RegularExpressions.Regex.Match(request,
            @"\bfolder\s+(?:called|named)\s+([\w][\w .\-]*?)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim().TrimEnd('.', '!') : null;
    }

    private static TaskPlan Finish(AgentTask task, UserIntent intent, List<PlanStep> steps)
    {
        // Keep the plan within 3–10 steps: keep the first step, the last
        // (verify) step, and trim from the middle if a long request produced more.
        if (steps.Count > 10)
        {
            var trimmed = new List<PlanStep> { steps[0] };
            trimmed.AddRange(steps.Skip(1).Take(8));
            trimmed.Add(steps[^1]);
            steps = trimmed;
        }
        while (steps.Count < 3)
        {
            steps.Insert(steps.Count - 1, new PlanStep(
                "step-extra-" + steps.Count,
                "Review the result",
                "filesystem",
                "Review the intermediate result before finishing.",
                CompletionCondition: "Intermediate result is reviewed.")
            {
                Args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["action"] = "list",
                    ["path"] = ".",
                },
            });
        }
        var summary = "Plan for: " + intent.DesiredOutcome +
                      " (" + steps.Count + " steps; completion: " + intent.CompletionCondition + ")";
        return new TaskPlan(task.TaskId, task.UserRequest, task.Language, steps, summary);
    }

    private static string GetDocumentsDir()
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }
        catch (Exception)
        {
            // Fall through to the home directory.
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
