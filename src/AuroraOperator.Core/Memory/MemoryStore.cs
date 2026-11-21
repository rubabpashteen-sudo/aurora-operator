using AuroraOperator.Core;
using AuroraOperator.Core.Security;
using Microsoft.Data.Sqlite;

namespace AuroraOperator.Core.Memory;

/// <summary>A single stored conversation turn.</summary>
public sealed record ConversationEntry(string Id, string Role, string Content, DateTimeOffset CreatedAt);

/// <summary>A stored reusable prompt template.</summary>
public sealed record TemplateEntry(string Name, string Content, DateTimeOffset CreatedAt);

/// <summary>
/// SQLite-backed memory store (conversations, tasks, preferences, templates).
/// Values whose key contains password/secret/token/key are never persisted.
/// Preference values are additionally protected with
/// <see cref="SecretProtector"/> (DPAPI on Windows) and refused when they
/// look like a credential. Conversations and tasks are stored verbatim by
/// design — see <see cref="SecretProtector"/> and SECURITY_REVIEW.md F14
/// for the documented residual risk.
/// </summary>
public sealed class MemoryStore : IDisposable
{
    private static readonly string[] SensitiveKeyFragments =
        { "password", "secret", "token", "key" };

    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public MemoryStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false };
        _connection = new SqliteConnection(connectionString.ToString());
        _connection.Open();
        CreateTables();
        RestrictDatabaseFileAccess(dbPath);
    }

    /// <summary>
    /// Phase 8 (F14): on Windows, restrict the database file to the
    /// current user only (no inherited ACLs). Any failure is logged and
    /// ignored — a permission error must never prevent the app from
    /// starting. No-op on other operating systems. The ACL itself is a
    /// Windows-test item (verify with icacls on the Windows machine).
    /// </summary>
    private static void RestrictDatabaseFileAccess(string dbPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            var info = new FileInfo(dbPath);
            var security = info.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user is not null)
            {
                security.SetAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            }
            info.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            LocalLogger.Warn("MemoryStore: could not restrict database file permissions: " + ex.Message);
        }
    }

    public string DatabasePath => _connection.DataSource;

    private void CreateTables()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS tasks (
                    task_id TEXT PRIMARY KEY,
                    user_request TEXT NOT NULL,
                    language TEXT NOT NULL,
                    mode TEXT NOT NULL,
                    state TEXT NOT NULL,
                    final_report TEXT,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS preferences (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS templates (
                    name TEXT PRIMARY KEY,
                    content TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """;

            cmd.ExecuteNonQuery();
            MigrateTaskHistory();
        }
    }

    /// <summary>True when a key must never be stored (password/secret/token/key).</summary>
    private void MigrateTaskHistory()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(tasks);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) columns.Add(reader.GetString(1));

        if (!columns.Contains("conversation_id"))
        {
            command.CommandText = "ALTER TABLE tasks ADD COLUMN conversation_id TEXT;";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("workspace_path"))
        {
            command.CommandText = "ALTER TABLE tasks ADD COLUMN workspace_path TEXT;";
            command.ExecuteNonQuery();
        }
        command.CommandText = "CREATE INDEX IF NOT EXISTS tasks_by_conversation ON tasks(conversation_id, created_at);";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var lower = key.ToLowerInvariant();
        return SensitiveKeyFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    // ---- Conversations -------------------------------------------------

    /// <summary>Stores one conversation turn and returns its new id.</summary>
    public string SaveConversation(string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);
        var id = Guid.NewGuid().ToString("N");
        SaveConversation(id, role, content);
        return id;
    }

    /// <summary>Stores one conversation turn under an explicit id (upsert).</summary>
    public void SaveConversation(string id, string role, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO conversations (id, role, content, created_at)
                VALUES ($id, $role, $content, $createdAt)
                ON CONFLICT(id) DO UPDATE SET role = excluded.role, content = excluded.content;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$role", role);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Searches role/content for the query (case-insensitive LIKE). Empty query returns recent turns.</summary>
    public IReadOnlyList<ConversationEntry> SearchConversations(string query)
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            if (string.IsNullOrWhiteSpace(query))
            {
                cmd.CommandText = "SELECT id, role, content, created_at FROM conversations ORDER BY created_at DESC LIMIT 100;";
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, role, content, created_at FROM conversations
                    WHERE content LIKE $q OR role LIKE $q
                    ORDER BY created_at DESC LIMIT 100;
                    """;
                cmd.Parameters.AddWithValue("$q", $"%{query}%");
            }

            var results = new List<ConversationEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // A corrupt row (bad date, NULL text) is skipped, never
                // allowed to fail the whole search — see TryReadConversation.
                if (TryReadConversation(reader) is { } entry)
                {
                    results.Add(entry);
                }
            }

            return results;
        }
    }

    /// <summary>Deletes one conversation turn. Returns true when a row was removed.</summary>
    public bool DeleteConversation(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM conversations WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Deletes every stored conversation turn.</summary>
    public void ClearConversations()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM conversations;";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Every stored conversation turn, newest first (used by ExportJson).</summary>
    private IReadOnlyList<ConversationEntry> AllConversations()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, role, content, created_at FROM conversations ORDER BY created_at DESC;";
            var results = new List<ConversationEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Corrupt rows are skipped, never allowed to fail an export.
                if (TryReadConversation(reader) is { } entry)
                {
                    results.Add(entry);
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Reads one conversation row defensively. A row whose date is not a
    /// valid timestamp still loads (with the current time as its date);
    /// a row whose id/role/content is missing is skipped (null) and the
    /// skip is logged, so one corrupt row can never fail a whole search,
    /// list or export.
    /// </summary>
    private static ConversationEntry? TryReadConversation(SqliteDataReader reader)
    {
        try
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
            {
                LocalLogger.Warn("MemoryStore: skipped a conversation row with missing id/role/content.");
                return null;
            }

            var createdAt = reader.IsDBNull(3) ||
                !DateTimeOffset.TryParse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                    ? DateTimeOffset.UtcNow
                    : parsed;
            return new ConversationEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), createdAt);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or FormatException)
        {
            LocalLogger.Warn("MemoryStore: skipped an unreadable conversation row. " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads one task row defensively. Unknown enum strings fall back to
    /// Unknown/Ask/Created (as before); a row whose id or request is
    /// missing is skipped (null) and logged, so one corrupt row can never
    /// fail a whole task list or a single-task load.
    /// </summary>
    private static AgentTask? TryReadTask(SqliteDataReader reader)
    {
        try
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                LocalLogger.Warn("MemoryStore: skipped a task row with missing id/request.");
                return null;
            }

            var language = !reader.IsDBNull(2) && Enum.TryParse<Language>(reader.GetString(2), ignoreCase: true, out var lang)
                ? lang
                : Language.Unknown;
            var mode = !reader.IsDBNull(3) && Enum.TryParse<AgentMode>(reader.GetString(3), ignoreCase: true, out var m)
                ? m
                : AgentMode.Ask;
            var state = !reader.IsDBNull(4) && Enum.TryParse<TaskState>(reader.GetString(4), ignoreCase: true, out var s)
                ? s
                : TaskState.Created;
            return new AgentTask(reader.GetString(0), reader.GetString(1), language, mode)
            {
                State = state,
                FinalReport = reader.IsDBNull(5) ? null : reader.GetString(5),
                ConversationId = reader.IsDBNull(6) ? null : reader.GetString(6),
                WorkspacePath = reader.IsDBNull(7) ? null : reader.GetString(7),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidCastException or FormatException)
        {
            LocalLogger.Warn("MemoryStore: skipped an unreadable task row. " + ex.Message);
            return null;
        }
    }

    // ---- Tasks ---------------------------------------------------------

    /// <summary>Stores or updates an agent task (upsert by TaskId).</summary>
    public void SaveTask(AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO tasks (task_id, user_request, language, mode, state, final_report, created_at, updated_at, conversation_id, workspace_path)
                VALUES ($id, $request, $language, $mode, $state, $report, $now, $now, $conversation, $workspace)
                ON CONFLICT(task_id) DO UPDATE SET
                    user_request = excluded.user_request,
                    language = excluded.language,
                    mode = excluded.mode,
                    state = excluded.state,
                    final_report = excluded.final_report,
                    conversation_id = excluded.conversation_id,
                    workspace_path = excluded.workspace_path,
                    updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$id", task.TaskId);
            cmd.Parameters.AddWithValue("$request", task.UserRequest);
            cmd.Parameters.AddWithValue("$language", task.Language.ToString());
            cmd.Parameters.AddWithValue("$mode", task.Mode.ToString());
            cmd.Parameters.AddWithValue("$state", task.State.ToString());
            cmd.Parameters.AddWithValue("$report", (object?)task.FinalReport ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$conversation", (object?)task.ConversationId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$workspace", (object?)task.WorkspacePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Stores or updates a task from plain fields (language/mode default to Unknown/Ask).</summary>
    public void SaveTask(string taskId, string userRequest, string state)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(userRequest);
        var taskState = Enum.TryParse<TaskState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : TaskState.Created;
        var task = new AgentTask(taskId, userRequest, Language.Unknown, AgentMode.Ask)
        {
            State = taskState,
        };
        SaveTask(task);
    }

    /// <summary>Loads a task by id, or null when it does not exist.</summary>
    public AgentTask? GetTask(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT task_id, user_request, language, mode, state, final_report, conversation_id, workspace_path
                FROM tasks WHERE task_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", taskId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            // A corrupt row reads as null (logged inside TryReadTask) and
            // is reported as "not found" rather than throwing.
            return TryReadTask(reader);
        }
    }

    /// <summary>Returns the n most recently updated tasks.</summary>
    public IReadOnlyList<AgentTask> ListRecentTasks(int n)
    {
        if (n < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(n), "Count must not be negative.");
        }

        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT task_id, user_request, language, mode, state, final_report, conversation_id, workspace_path
                FROM tasks ORDER BY updated_at DESC LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", n);
            var results = new List<AgentTask>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // Corrupt rows are skipped (logged inside TryReadTask);
                // one bad row no longer fails the whole recent-tasks list.
                if (TryReadTask(reader) is { } task)
                {
                    results.Add(task);
                }
            }

            return results;
        }
    }

    public IReadOnlyList<AgentTask> SearchTaskHistory(string query) => ReadHistory("""
        WITH matching AS (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY COALESCE(conversation_id, task_id)
                ORDER BY updated_at DESC) AS rank
            FROM tasks WHERE instr(lower(user_request || ' ' || coalesce(final_report, '')), lower($value)) > 0
                OR $value = ''
        )
        SELECT task_id, user_request, language, mode, state, final_report, conversation_id, workspace_path
        FROM matching WHERE rank = 1 ORDER BY updated_at DESC LIMIT 50;
        """, query);

    public IReadOnlyList<AgentTask> ReadConversationTasks(string conversationId) => ReadHistory("""
        SELECT task_id, user_request, language, mode, state, final_report, conversation_id, workspace_path
        FROM tasks WHERE conversation_id = $value OR (conversation_id IS NULL AND task_id = $value)
        ORDER BY created_at, task_id;
        """, conversationId);

    private IReadOnlyList<AgentTask> ReadHistory(string sql, string value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$value", value);
            using var reader = command.ExecuteReader();
            var result = new List<AgentTask>();
            while (reader.Read())
                if (TryReadTask(reader) is { } task) result.Add(task);
            return result;
        }
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM conversations; DELETE FROM tasks;";
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    // ---- Preferences ---------------------------------------------------

    /// <summary>
    /// Stores a preference. Silently skipped when the key contains
    /// password/secret/token/key — secrets are never persisted.
    /// Phase 8 (F14): a value that itself looks like a credential
    /// (it changes under <see cref="LocalLogger.RedactSecrets"/>) is
    /// refused with <see cref="ArgumentException"/> instead of being
    /// stored, and every stored value is protected with
    /// <see cref="SecretProtector"/> (DPAPI on Windows).
    /// </summary>
    public void SetPreference(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (IsSensitiveKey(key))
        {
            return;
        }
        if (!string.Equals(LocalLogger.RedactSecrets(value), value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Refusing to store a preference value that looks like a credential (password, token, API key or private key). " +
                "Secrets are never persisted in memory.", nameof(value));
        }
        var stored = SecretProtector.Protect(value);

        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO preferences (key, value, updated_at)
                VALUES ($key, $value, $now)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", stored);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Reads a preference, or null when it was never stored (or was refused as sensitive).
    /// DPAPI-protected values that cannot be decrypted on this machine/user also read as null.</summary>
    public string? GetPreference(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM preferences WHERE key = $key;";
            cmd.Parameters.AddWithValue("$key", key);
            var value = cmd.ExecuteScalar();
            return value is string stored ? SecretProtector.Unprotect(stored) : null;
        }
    }

    // ---- Templates -----------------------------------------------------

    /// <summary>
    /// Stores a reusable prompt template (upsert by name).
    /// Silently skipped when the name contains password/secret/token/key.
    /// </summary>
    public void SaveTemplate(string name, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);
        if (IsSensitiveKey(name))
        {
            return;
        }

        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO templates (name, content, created_at, updated_at)
                VALUES ($name, $content, $now, $now)
                ON CONFLICT(name) DO UPDATE SET content = excluded.content, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$content", content);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Lists all stored templates, newest first.</summary>
    public IReadOnlyList<TemplateEntry> ListTemplates()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT name, content, created_at FROM templates ORDER BY created_at DESC;";
            var results = new List<TemplateEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new TemplateEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
            }

            return results;
        }
    }

    // ---- Export --------------------------------------------------------

    /// <summary>Exports conversations, tasks, preferences and templates as JSON.</summary>
    public void ExportJson(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ThrowIfDisposed();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var conversations = AllConversations();
        var tasks = ListRecentTasks(int.MaxValue);
        var templates = ListTemplates();

        var preferences = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM preferences ORDER BY key;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                preferences[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var payload = new
        {
            exportedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            conversations = conversations.Select(c => new
            {
                c.Id,
                c.Role,
                c.Content,
                createdAt = c.CreatedAt.ToString("o"),
            }),
            tasks = tasks.Select(t => new
            {
                t.TaskId,
                t.UserRequest,
                language = t.Language.ToString(),
                mode = t.Mode.ToString(),
                state = t.State.ToString(),
                t.FinalReport,
            }),
            preferences,
            templates = templates.Select(t => new
            {
                t.Name,
                t.Content,
                createdAt = t.CreatedAt.ToString("o"),
            }),
        };

        var json = System.Text.Json.JsonSerializer.Serialize(
            payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    // ---- Disposal ------------------------------------------------------

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MemoryStore));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }
}
