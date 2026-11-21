using System.Runtime.InteropServices;
using System.Text;

namespace AuroraOperator.Core.Security;

/// <summary>
/// Per-value protection for preference values stored in SQLite
/// (Phase 8, security finding F14). The database file itself is
/// plaintext SQLite — encrypting the whole file would need a
/// decrypt-to-temp-file dance at startup (the temp file would leak
/// the plaintext), so instead each preference *value* is protected
/// individually:
///
/// * On Windows: DPAPI <c>CryptProtectData</c> with the current-user
///   scope. Only the same Windows user on the same machine can
///   decrypt it; no key is stored anywhere. Stored as
///   <c>dpapi1:&lt;base64&gt;</c>.
/// * Anywhere else (Linux test runs): no DPAPI exists, so the value is
///   stored base64-encoded with a <c>plain1:</c> marker. This is NOT
///   encryption — it only keeps the on-disk format versioned and
///   stops casual <c>strings</c> grepping. The marker makes the
///   downgrade visible instead of silent.
/// * Values stored before this change have no marker and are read
///   back as-is (migration: they are re-protected on the next save).
///
/// Recovery/compatibility plan (see SECURITY_REVIEW.md F14):
/// DPAPI keys live in the user's Windows profile. If the profile is
/// lost or the database is moved to another machine/user, dpapi1:
/// values cannot be decrypted and read back as null (with a warning
/// log) — preferences are non-critical settings (theme, model name),
/// so the user simply reconfigures them from the UI. Conversations
/// and tasks are NOT protected by this class: they are stored verbatim
/// by design (conversation memory is a feature), and the residual
/// risk — a pasted secret in a conversation turn stays in plaintext —
/// is documented, not hidden. The Windows DPAPI path has NOT been
/// executed in this Linux environment; it is a first-Windows-test
/// item.</summary>
public static class SecretProtector
{
    public const string DpapiPrefix = "dpapi1:";
    public const string PlainPrefix = "plain1:";

    /// <summary>Protects a value for storage. Never returns null.</summary>
    public static string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = DpapiProtect(Encoding.UTF8.GetBytes(value));
                return DpapiPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                // DPAPI failing must not lose the setting entirely; fall
                // back to the marked plaintext form and say so loudly.
                LocalLogger.Warn("SecretProtector: DPAPI protect failed, storing preference value unencrypted: " + ex.Message);
            }
        }
        return PlainPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Reverses <see cref="Protect"/>. Legacy unmarked values are
    /// returned as-is. Returns null when a dpapi1: value cannot be
    /// decrypted on this machine/user (wrong profile, or not Windows).
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (stored is null)
        {
            return null;
        }
        if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
        {
            if (!OperatingSystem.IsWindows())
            {
                LocalLogger.Warn("SecretProtector: cannot decrypt a DPAPI-protected value on this operating system.");
                return null;
            }
            try
            {
                var bytes = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
                return Encoding.UTF8.GetString(DpapiUnprotect(bytes));
            }
            catch (Exception ex)
            {
                LocalLogger.Warn("SecretProtector: DPAPI unprotect failed: " + ex.Message);
                return null;
            }
        }
        if (stored.StartsWith(PlainPrefix, StringComparison.Ordinal))
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[PlainPrefix.Length..]));
            }
            catch (FormatException)
            {
                return stored;
            }
        }
        return stored;
    }

    /// <summary>True when the stored form carries a protection marker.</summary>
    public static bool LooksProtected(string? stored) =>
        stored is not null &&
        (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal) ||
         stored.StartsWith(PlainPrefix, StringComparison.Ordinal));

    // ---- DPAPI (Windows only, crypt32.dll) ------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, out string? pszDataDescr, ref DataBlob pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const uint CryptProtectUiForbidden = 0x1;

    private static byte[] DpapiProtect(byte[] plaintext)
    {
        var input = new DataBlob { CbData = plaintext.Length, PbData = Marshal.AllocHGlobal(plaintext.Length) };
        var entropy = new DataBlob();
        try
        {
            Marshal.Copy(plaintext, 0, input.PbData, plaintext.Length);
            if (!CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new InvalidOperationException("CryptProtectData failed (Win32 error " + Marshal.GetLastWin32Error() + ").");
            }
            try
            {
                var result = new byte[output.CbData];
                Marshal.Copy(output.PbData, result, 0, output.CbData);
                return result;
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    private static byte[] DpapiUnprotect(byte[] ciphertext)
    {
        var input = new DataBlob { CbData = ciphertext.Length, PbData = Marshal.AllocHGlobal(ciphertext.Length) };
        var entropy = new DataBlob();
        try
        {
            Marshal.Copy(ciphertext, 0, input.PbData, ciphertext.Length);
            if (!CryptUnprotectData(ref input, out _, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new InvalidOperationException("CryptUnprotectData failed (Win32 error " + Marshal.GetLastWin32Error() + ").");
            }
            try
            {
                var result = new byte[output.CbData];
                Marshal.Copy(output.PbData, result, 0, output.CbData);
                return result;
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }
}
