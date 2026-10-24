# ShowCPU

A small Windows app that draws a CPU widget on the taskbar and keeps a system-tray icon for Settings, About, and Quit.

The folder `log/kil0bit-system-monitor-main` is only a reference for the idea of a taskbar widget. Do not copy that project. This app shows CPU only.

## What the user sees

### Taskbar widget

A dark rounded pill, vertically centered on the primary taskbar, sitting just to the left of the notification area (clock, network, volume). It must not cover those icons.

Layout, left to right:

- Current CPU as an integer percent, for example `7%`, `41%`, `100%`. No decimals. Green text.
- A gap.
- Ten green vertical bars. One sample per second. The bars are the last 10 seconds.

Each second:

1. Shift every bar one slot to the left.
2. Drop the oldest sample.
3. Draw the new sample as the rightmost bar.

Bar height is that second's CPU percent (0% is an empty slot, 100% is full height). Until 10 real samples exist, the missing older slots stay empty, so the graph fills in from the right.

Match the reference screenshot: dark pill, bright green percent, bright green bars, compact enough to sit inside a normal taskbar. Keep it sharp on high-DPI screens.

Hide the widget when the taskbar itself is hidden (auto-hide, or a fullscreen app covering it). Show it again when the taskbar comes back. If the taskbar is moved or resized, move the pill with it. Bottom taskbar is the common case; also handle top, left, and right.

The pill is display-only. It does not open a window when clicked.

### System tray

A tray icon is always present while the app is running. Tooltip shows the live value, for example `CPU 41%`.

Right-click menu, in this order:

1. Settings
2. About
3. separator
4. Quit

Quit is required. Without it the process runs until the user signs out, and there is no way to stop it. Quit removes the widget, removes the tray icon, and exits the process.

Closing Settings or About does not quit the app.

Left-click on the tray icon opens Settings.

## Settings

A small dark window, same visual language as the widget.

One option:

- Start with Windows. A toggle. Default is on, so a normal first launch registers the app to start at sign-in.

Turning it on writes the current executable path to:

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

Value name: `ShowCPU`

Turning it off deletes that value. No administrator rights. If the exe moves, the next launch while the toggle is on refreshes the stored path.

Remember the toggle in `%AppData%\ShowCPU\settings.json`. On startup, apply that saved value (and repair the Run key if it does not match).

## About

A small dark window with the app name, version, and one line: taskbar widget that shows CPU usage for the last 10 seconds.

## Startup

- Single instance. A second launch does nothing and exits.
- No main window. Only the widget, the tray icon, and Settings or About when opened.
- First run: start-with-Windows is on.

## Tech

- .NET 10, `net10.0-windows`, `WinExe`.
- WPF for the widget, Settings, and About.
- Tray icon via `NotifyIcon` (Windows Forms), because WPF has no tray icon.
- CPU from `GetSystemTimes` (kernel32). No performance-counter package and no elevation.
  - Kernel time includes idle time.
  - Busy = (kernel + user) - idle.
  - Percent = busy delta / (kernel + user) delta, clamped to 0..100.
  - Sample once per second. Ignore the first delta (there is no previous snapshot yet).
- Per-monitor DPI v2 in the application manifest. `asInvoker`.
- Settings file: JSON in `%AppData%\ShowCPU\settings.json`.

Suggested layout (new files only; leave `log/` alone):

```
ShowCPU.csproj
app.manifest
App.xaml
App.xaml.cs
WidgetWindow.xaml
WidgetWindow.xaml.cs
SettingsWindow.xaml
SettingsWindow.xaml.cs
AboutWindow.xaml
AboutWindow.xaml.cs
Services/CpuSampler.cs
Services/StartupService.cs
Services/SettingsStore.cs
Services/TaskbarPlacement.cs
```

## Look

- Pill background near `#1C1C1C`, slight transparency, thin light border, rounded corners.
- Percent and bars near `#3DDC84` (the green in the screenshot). Taller bars can be a little brighter than short ones so the graph is easy to read. Still green, not a rainbow.
- Font: Segoe UI, semibold for the percent. Use a width that fits `100%` so the pill does not resize every second.
- Settings and About: dark background, rounded window, no default thick title bar, Segoe UI, a clear close button. The start-with-Windows toggle should look like a Windows 11 switch.

## Out of scope

- RAM, GPU, disk, network, or any other sensor.
- An installer.
- Copying the kil0bit overlay, its settings, or its telemetry.
