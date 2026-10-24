using System.IO;
using System.Text.Json;

namespace ShowCPU.Services;

internal sealed class AppSettings
{
    public bool StartWithWindows { get; set; } = true;

    // Distance from the left or top of the taskbar, in pixels. Null keeps the
    // widget just beside the notification area until the user drags it.
    public int? TaskbarOffset { get; set; }

    // Taskbar length along its long edge when TaskbarOffset was saved. Used to
    // rescale the offset when the main display or taskbar width changes.
    public int? TaskbarAlongLength { get; set; }

    // Taskbar DPI (96 = 100%) when TaskbarOffset was saved. Used with
    // TaskbarAlongLength when the main display scale changes (e.g. 125% to 200%).
    public int? TaskbarDpi { get; set; }

    // File name only (under Downloads) of the active high-CPU log. Updated when
    // a new ShowCPU-*.log is created after the previous one hits 10 MB.
    public string? HighCpuLogFileName { get; set; }
}

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;

    public SettingsStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShowCPU");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
    }

    public AppSettings LoadOrCreate()
    {
        bool existed = File.Exists(_path);
        AppSettings settings = Load();
        if (!existed)
            Save(settings);
        return settings;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new AppSettings();

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(_path, json);
    }
}
