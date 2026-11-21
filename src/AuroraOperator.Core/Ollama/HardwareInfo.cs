using System.Runtime.InteropServices;

namespace AuroraOperator.Core.Ollama;

/// <summary>Snapshot of the machine's hardware, used for model selection.</summary>
public sealed record HardwareReport(
    long TotalRamBytes,
    long AvailableRamBytes,
    string CpuName,
    int CpuCores,
    string GpuName,
    string OsVersion,
    bool Is64Bit,
    string WindowsVersion);

/// <summary>Detects RAM/CPU/GPU/OS/disk. Windows detection uses Environment and
/// the registry where available; on Linux it falls back to /proc/meminfo and
/// /proc/cpuinfo so the Core library still compiles and runs cross-platform.</summary>
public static class HardwareInfo
{
    public static HardwareReport Detect()
    {
        long total = 0, available = 0;
        var cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? string.Empty;
        var gpu = string.Empty;
        var os = RuntimeInformation.OSDescription;
        var windowsVersion = OperatingSystem.IsWindows() ? Environment.OSVersion.VersionString : "Not Windows";

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var mem = new MemoryStatusEx();
                if (GlobalMemoryStatusEx(mem))
                {
                    total = (long)mem.TotalPhys;
                    available = (long)mem.AvailPhys;
                }
            }
            catch { /* fall through to GC estimate */ }
            if (string.IsNullOrWhiteSpace(cpu))
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "Unknown CPU";
            gpu = ReadWindowsGpuName();
        }
        else
        {
            (total, available) = ReadLinuxMemory();
            cpu = ReadLinuxCpuName() ?? cpu;
            if (string.IsNullOrWhiteSpace(cpu)) cpu = "Unknown CPU";
        }

        if (total <= 0)
        {
            var gc = GC.GetGCMemoryInfo();
            total = gc.TotalAvailableMemoryBytes;
            available = Math.Max(0, total - GC.GetTotalMemory(false));
        }

        return new HardwareReport(total, available, cpu, Environment.ProcessorCount,
            string.IsNullOrWhiteSpace(gpu) ? "Unknown" : gpu, os, Environment.Is64BitOperatingSystem, windowsVersion);
    }

    public static long FreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    private static (long Total, long Available) ReadLinuxMemory()
    {
        long total = 0, avail = 0;
        try
        {
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                var parts = line.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 2) continue;
                var kb = long.TryParse(parts[1].Split(' ')[0], out var v) ? v : 0;
                if (parts[0] == "MemTotal") total = kb * 1024;
                if (parts[0] == "MemAvailable") avail = kb * 1024;
            }
        }
        catch { /* ignore */ }
        return (total, avail);
    }

    private static string? ReadLinuxCpuName()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line.Split(':', StringSplitOptions.TrimEntries).LastOrDefault();
        }
        catch { /* ignore */ }
        return null;
    }

    private static string ReadWindowsGpuName()
    {
#pragma warning disable CA1416 // Windows-only call site; Detect() only calls this on Windows.
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000");
            return key?.GetValue("DriverDesc") as string ?? string.Empty;
        }
        catch { return string.Empty; }
#pragma warning restore CA1416
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
        public MemoryStatusEx() { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
