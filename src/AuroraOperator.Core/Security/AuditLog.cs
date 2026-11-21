namespace AuroraOperator.Core.Security;

/// <summary>
/// Append-only audit log. Every record is one timestamped line; existing
/// entries are never edited or removed by this class.
/// </summary>
public sealed class AuditLog
{
    private readonly string _logPath;
    private readonly object _gate = new();

    public AuditLog(string logPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
        _logPath = Path.GetFullPath(logPath);
        var directory = Path.GetDirectoryName(_logPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string LogPath => _logPath;

    /// <summary>
    /// Records one approval request/decision. One line: timestamp, task id,
    /// action, outcome, risk, action id, parameter hash and source.
    /// </summary>
    public void RecordApproval(ApprovalRequest request, ApprovalOutcome outcome, string source)
    {
        ArgumentNullException.ThrowIfNull(request);
        var details = string.Join("; ",
            "risk=" + request.RiskLevel,
            "actionId=" + (string.IsNullOrWhiteSpace(request.ActionId) ? request.StepId : request.ActionId),
            "stepId=" + request.StepId,
            "hash=" + request.EffectiveParameterHash,
            "outcome=" + outcome,
            "source=" + (source ?? string.Empty),
            "title=" + request.Title);
        Record(request.TaskId, "approval:" + outcome.ToString().ToLowerInvariant(), details,
            approved: outcome == ApprovalOutcome.Approved);
    }

    /// <summary>Appends one record. Newlines inside fields are flattened so each record stays one line.</summary>
    public void Record(string taskId, string action, string details, bool approved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        var line = string.Join(" | ",
            DateTimeOffset.UtcNow.ToString("o"),
            Flatten(taskId ?? string.Empty),
            Flatten(action),
            approved ? "approved=true" : "approved=false",
            Flatten(details ?? string.Empty));
        lock (_gate)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine);
        }
    }

    /// <summary>Returns the most recent n records, oldest first. Empty when the log does not exist yet.</summary>
    public IReadOnlyList<string> ReadRecent(int n)
    {
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "Count must not be negative.");
        }

        if (!File.Exists(_logPath))
        {
            return Array.Empty<string>();
        }

        lock (_gate)
        {
            try
            {
                var lines = File.ReadAllLines(_logPath);
                return lines.Length <= n ? lines : lines[^n..];
            }
            catch (IOException)
            {
                return Array.Empty<string>();
            }
        }
    }

    private static string Flatten(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
