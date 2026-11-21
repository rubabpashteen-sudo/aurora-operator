using System.Diagnostics;
using System.Net.NetworkInformation;

namespace AuroraOperator.Core.Tools;

public sealed record DiskInfo(string Name, long TotalBytes, long FreeBytes);
public sealed record TopProcessInfo(string Name, int ProcessId, long WorkingSetBytes);

/// <summary>Snapshot of the machine's health at diagnosis time.</summary>
public sealed record SystemReport(
    double CpuPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    IReadOnlyList<DiskInfo> Disks,
    IReadOnlyList<TopProcessInfo> TopProcesses,
    bool NetworkReachable,
    string NetworkDetail,
    IReadOnlyList<string> SlowPcFindings);

/// <summary>
/// Diagnoses the PC: CPU %, RAM used/total, disk free per drive, top processes
/// by memory, and network reachability (ping 8.8.8.8, 2 s timeout — a failed
/// ping is reported as a fact, never thrown). Produces slow-PC findings.
/// </summary>
public static class Diagnostics
{
    public static SystemReport Diagnose()
    {
        var cpu = MeasureCpuPercent();
        var (ramUsed, ramTotal) = MeasureRam();
        var disks = MeasureDisks();
        var top = TopProcesses();
        var (reachable, networkDetail) = PingNetwork();

        var findings = new List<string>();
        if (cpu >= 0 && cpu > 80)
            findings.Add($"CPU usage is high ({cpu:F1}%).");
        if (ramTotal > 0 && ramUsed * 100L / ramTotal > 85)
            findings.Add($"RAM usage is high ({ramUsed * 100L / ramTotal}% of {ramTotal / 1024 / 1024} MB).");
        if (ramTotal > 0 && ramTotal < 8L * 1024 * 1024 * 1024)
            findings.Add($"Total RAM is only {ramTotal / 1024 / 1024} MB; 8 GB or more is recommended for smooth use.");
        foreach (var disk in disks)
        {
            if (disk.TotalBytes > 0 && disk.FreeBytes * 100L / disk.TotalBytes < 10)
                findings.Add($"Drive {disk.Name} is low on free space ({disk.FreeBytes / 1024 / 1024} MB free).");
        }
        foreach (var p in top.Take(3))
        {
            if (p.WorkingSetBytes > 1L * 1024 * 1024 * 1024)
                findings.Add($"'{p.Name}' (PID {p.ProcessId}) is using {p.WorkingSetBytes / 1024 / 1024} MB of memory.");
        }
        if (!reachable)
            findings.Add("Network is unreachable (ping to 8.8.8.8 failed): " + networkDetail);
        if (cpu < 0)
            findings.Add("CPU usage could not be measured on this machine.");

        return new SystemReport(
            CpuPercent: cpu,
            RamUsedBytes: ramUsed,
            RamTotalBytes: ramTotal,
            Disks: disks,
            TopProcesses: top,
            NetworkReachable: reachable,
            NetworkDetail: networkDetail,
            SlowPcFindings: findings);
    }

    private static double MeasureCpuPercent()
    {
        try
        {
            var cores = Math.Max(1, Environment.ProcessorCount);
            long TotalTicks()
            {
                long total = 0;
                foreach (var p in Process.GetProcesses())
                {
                    try { total += p.TotalProcessorTime.Ticks; }
                    catch (Exception) { /* process exited / no access */ }
                    finally { p.Dispose(); }
                }
                return total;
            }
            var before = TotalTicks();
            var watch = Stopwatch.StartNew();
            Thread.Sleep(500);
            var after = TotalTicks();
            watch.Stop();
            var elapsedTicks = watch.Elapsed.Ticks;
            if (elapsedTicks <= 0) return -1;
            var cpu = (after - before) * 100.0 / (elapsedTicks * cores);
            return Math.Clamp(cpu, 0, 100);
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static (long Used, long Total) MeasureRam()
    {
        // Linux: /proc/meminfo is the reliable source.
        try
        {
            if (File.Exists("/proc/meminfo"))
            {
                long totalKb = 0, availableKb = 0;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                        totalKb = ParseKb(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                        availableKb = ParseKb(line);
                }
                if (totalKb > 0)
                    return (Math.Max(0, totalKb - availableKb) * 1024, totalKb * 1024);
            }
        }
        catch (Exception)
        {
            // Fall through to the GC-based estimate.
        }
        try
        {
            var info = GC.GetGCMemoryInfo();
            var total = info.TotalAvailableMemoryBytes;
            var used = info.MemoryLoadBytes;
            if (total > 0) return (used, total);
        }
        catch (Exception)
        {
            // No RAM data available.
        }
        return (0, 0);
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
    }

    private static IReadOnlyList<DiskInfo> MeasureDisks()
    {
        var disks = new List<DiskInfo>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    disks.Add(new DiskInfo(drive.Name, drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch (Exception)
                {
                    // Drive not readable; skip it.
                }
            }
        }
        catch (Exception)
        {
            // No drive data available.
        }
        return disks;
    }

    private static IReadOnlyList<TopProcessInfo> TopProcesses()
    {
        var list = new List<TopProcessInfo>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    list.Add(new TopProcessInfo(p.ProcessName, p.Id, p.WorkingSet64));
                }
                catch (Exception)
                {
                    // Process exited or is inaccessible.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception)
        {
            // No process data available.
        }
        return list.OrderByDescending(p => p.WorkingSetBytes).Take(10).ToList();
    }

    private static (bool Reachable, string Detail) PingNetwork()
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send("8.8.8.8", 2000);
            if (reply.Status == IPStatus.Success)
                return (true, $"Reply from 8.8.8.8 in {reply.RoundtripTime} ms.");
            return (false, $"Ping status: {reply.Status}.");
        }
        catch (Exception ex)
        {
            return (false, "Ping failed: " + ex.Message);
        }
    }
}
