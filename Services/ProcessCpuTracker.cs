using System.Diagnostics;

namespace ShowCPU.Services;

internal readonly record struct ProcessCpuEntry(string Name, int Pid, double Percent);

/// <summary>
/// Tracks per-process CPU deltas so a high-CPU log line can list the busiest processes.
/// Call only when about to log -- scanning every process is expensive.
/// </summary>
internal sealed class ProcessCpuTracker
{
    private readonly int _selfPid = Environment.ProcessId;
    private Dictionary<int, TimeSpan> _previous = new();
    private long _lastStamp;

    public void Reset()
    {
        _previous = new Dictionary<int, TimeSpan>();
        _lastStamp = 0;
    }

    /// <summary>
    /// Reads current process CPU times, returns the top consumers since the last call,
    /// then stores this snapshot as the baseline for the next call.
    /// Pass count 0 to take a baseline only (no ranking).
    /// </summary>
    public IReadOnlyList<ProcessCpuEntry> TakeTopAndRoll(int count)
    {
        long now = Stopwatch.GetTimestamp();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<ProcessCpuEntry>();
        }

        var current = new Dictionary<int, TimeSpan>(processes.Length);
        var names = new Dictionary<int, string>(processes.Length);
        foreach (Process process in processes)
        {
            try
            {
                using (process)
                {
                    int pid = process.Id;
                    if (pid == _selfPid)
                        continue;

                    current[pid] = process.TotalProcessorTime;
                    string name = process.ProcessName;
                    names[pid] = string.IsNullOrWhiteSpace(name) ? "?" : name;
                }
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or System.ComponentModel.Win32Exception
                    or UnauthorizedAccessException
                    or ArgumentException)
            {
                try { process.Dispose(); } catch { /* ignore */ }
            }
        }

        IReadOnlyList<ProcessCpuEntry> top = Array.Empty<ProcessCpuEntry>();
        if (_lastStamp != 0 && count > 0)
        {
            double elapsedMs = Stopwatch.GetElapsedTime(_lastStamp, now).TotalMilliseconds;
            if (elapsedMs >= 50)
            {
                int cores = Math.Max(1, Environment.ProcessorCount);
                var ranked = new List<ProcessCpuEntry>(Math.Min(64, current.Count));
                foreach ((int pid, TimeSpan cpu) in current)
                {
                    if (!_previous.TryGetValue(pid, out TimeSpan prev))
                        continue;

                    double deltaMs = (cpu - prev).TotalMilliseconds;
                    if (deltaMs <= 0)
                        continue;

                    double percent = deltaMs * 100.0 / (elapsedMs * cores);
                    if (percent <= 0)
                        continue;

                    ranked.Add(new ProcessCpuEntry(names[pid], pid, percent));
                }

                ranked.Sort(static (a, b) => b.Percent.CompareTo(a.Percent));
                if (ranked.Count > count)
                    ranked.RemoveRange(count, ranked.Count - count);
                top = ranked;
            }
        }

        _previous = current;
        _lastStamp = now;
        return top;
    }
}
