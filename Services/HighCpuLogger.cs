using System.Globalization;
using System.IO;
using System.Text;

namespace ShowCPU.Services;

/// <summary>
/// When system CPU is above 95%, appends a line to a ShowCPU-*.log file in Downloads.
/// Reuses the file name saved in settings; creates a new file when size would exceed 10 MB.
/// </summary>
internal sealed class HighCpuLogger
{
    private const long MaxBytes = 10L * 1024 * 1024;
    private const double ThresholdPercent = 95.0;

    private readonly string _downloads;
    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private string? _path;
    private readonly object _gate = new();

    public HighCpuLogger(AppSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
        _downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        TryRestoreSavedFile();
    }

    public void LogIfHot(double cpuPercent, IReadOnlyList<ProcessCpuEntry> top, double? clockMhz)
    {
        if (!double.IsFinite(cpuPercent) || cpuPercent <= ThresholdPercent)
            return;

        string line = FormatLine(cpuPercent, top, clockMhz);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_downloads);
                EnsureFile(Encoding.UTF8.GetByteCount(line) + 2);
                File.AppendAllText(_path!, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or DirectoryNotFoundException)
            {
                // Logging must never take down the widget.
            }
        }
    }

    private void TryRestoreSavedFile()
    {
        string? name = _settings.HighCpuLogFileName;
        if (string.IsNullOrWhiteSpace(name))
            return;

        // File name only -- ignore any path the settings file may contain.
        name = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(name)
            || !name.StartsWith("ShowCPU-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return;

        string path = Path.Combine(_downloads, name);
        try
        {
            if (!File.Exists(path))
                return;

            if (new FileInfo(path).Length >= MaxBytes)
                return;

            _path = path;
            _settings.HighCpuLogFileName = name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through and create a new file on the first hot sample.
        }
    }

    private void EnsureFile(int upcomingBytes)
    {
        if (_path != null && File.Exists(_path))
        {
            long length = new FileInfo(_path).Length;
            if (length + upcomingBytes <= MaxBytes)
                return;
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = "ShowCPU-" + stamp + ".log";
        _path = Path.Combine(_downloads, name);
        _settings.HighCpuLogFileName = name;
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep using the new path for this session even if settings cannot be written.
        }
    }

    private static string FormatLine(double cpuPercent, IReadOnlyList<ProcessCpuEntry> top, double? clockMhz)
    {
        // Tenths of a second, e.g. 2026-10-24 09:32:04.5
        DateTime now = DateTime.Now;
        int tenth = now.Millisecond / 100;
        var sb = new StringBuilder(256);
        sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append('.');
        sb.Append(tenth.ToString(CultureInfo.InvariantCulture));
        sb.Append("  CPU ");
        sb.Append(Math.Clamp(Math.Round(cpuPercent, MidpointRounding.AwayFromZero), 0, 100)
            .ToString(CultureInfo.InvariantCulture));
        sb.Append('%');
        AppendClock(sb, clockMhz);

        if (top.Count == 0)
        {
            sb.Append("  (no process samples)");
            return sb.ToString();
        }

        sb.Append("  top:");
        for (int i = 0; i < top.Count; i++)
        {
            ProcessCpuEntry entry = top[i];
            sb.Append(' ');
            sb.Append(entry.Name);
            sb.Append('=');
            sb.Append(entry.Percent.ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append('%');
        }

        return sb.ToString();
    }

    private static void AppendClock(StringBuilder sb, double? clockMhz)
    {
        if (clockMhz is not double mhz || !double.IsFinite(mhz) || mhz < 0)
            return;

        double ghz = Math.Clamp(mhz, 0, 99_999) / 1000.0;
        sb.Append("  CLK ");
        sb.Append(ghz >= 10.0
            ? Math.Round(ghz, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)
            : ghz.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append('G');
    }
}
