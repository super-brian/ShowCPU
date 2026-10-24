using Microsoft.Win32;

namespace ShowCPU.Services;

internal static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShowCPU";

    public static void Apply(bool enable)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Run registry key is unavailable.");

        if (!enable)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            return;

        key.SetValue(ValueName, "\"" + path + "\"");
    }
}
