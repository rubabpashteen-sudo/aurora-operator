using System.Text.RegularExpressions;

namespace AuroraOperator.Core;

/// <summary>Thread-safe structured local file logger. Logs live under the
/// application data folder (or a folder supplied by Init) as aurora-operator-YYYY-MM-DD.log.
/// Never logs secrets: callers must not pass passwords, keys or tokens, and
/// as defence in depth every message is scrubbed for common secret patterns
/// (password=…, api_key=…, "password is …", Bearer tokens) before it is
/// written — see <see cref="RedactSecrets"/> (Phase 7 security review,
/// extended in Phase 8, finding F13).
/// Scrubbing is best-effort: a secret written as free prose with no key
/// marker can still reach the log, so callers must still avoid logging
/// user-supplied secrets — and the engine no longer logs the full user
/// request at all (Phase 8).</summary>
public static class LocalLogger
{
    private static readonly object Gate = new();
    private static string _logDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuroraOperator", "logs");

    /// <summary>
    /// Best-effort secret patterns, applied to every log line, in order:
    /// credential key followed by ':' or '=' and a value (quoted or bare),
    /// "password is &lt;value&gt;", Bearer and Basic authorization tokens,
    /// and PEM private-key blocks (the whole block, including headers, is
    /// replaced). The key name is kept so the line stays readable; the
    /// value is replaced with [REDACTED]. A secret with no key marker
    /// (bare pasted token, private key without its PEM headers) is NOT
    /// caught — that is the documented residual risk of F13.
    /// </summary>
    private static readonly (Regex Pattern, MatchEvaluator Replacement)[] SecretPatterns =
    {
        // PEM private key block (may span lines).
        (new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled | RegexOptions.Singleline),
         _ => "[REDACTED PRIVATE KEY]"),
        // Credential key = value (incl. access/refresh tokens, client secrets, x-api-key).
        (new Regex(@"(?i)(password|passwd|pwd|api[-_]?key|x[-_]?api[-_]?key|secret|client[-_]?secret|access[-_]?token|refresh[-_]?token|token|access[-_]?key)\s*[:=]\s*(""[^""]*""|'[^']*'|\S+)",
            RegexOptions.Compiled),
         m => m.Groups[1].Value + "=[REDACTED]"),
        // "password is <value>".
        (new Regex(@"(?i)(password\s+is\s+)\S+", RegexOptions.Compiled),
         m => m.Groups[1].Value + "[REDACTED]"),
        // Bearer token.
        (new Regex(@"(?i)(bearer\s+)\S+", RegexOptions.Compiled),
         m => m.Groups[1].Value + "[REDACTED]"),
        // Basic authorization (base64 username:password).
        (new Regex(@"(?i)(authorization:\s+basic\s+)\S+", RegexOptions.Compiled),
         m => m.Groups[1].Value + "[REDACTED]"),
    };

    /// <summary>Scrubs secret-looking values from a log line. Public for tests.</summary>
    public static string RedactSecrets(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }
        var result = message;
        foreach (var (pattern, replacement) in SecretPatterns)
        {
            result = pattern.Replace(result, replacement);
        }
        return result;
    }

    public static void Init(string logDir)
    {
        if (!string.IsNullOrWhiteSpace(logDir)) _logDir = logDir;
        Info("Logger initialised. Log directory: " + _logDir);
    }

    public static string LogDirectory => _logDir;

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message) => Write("WARN", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            var line = $"{DateTimeOffset.Now:O} [{level}] {RedactSecrets(message)}";
            if (ex is not null) line += " | " + ex.GetType().Name + ": " + RedactSecrets(ex.Message);
            var path = Path.Combine(_logDir, $"aurora-operator-{DateTimeOffset.Now:yyyy-MM-dd}.log");
            lock (Gate) File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
