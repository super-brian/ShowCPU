# ShowCPU

A small Windows taskbar widget that shows CPU usage for the last 15 seconds, plus current clock speed as a percent of the processor base frequency (CLK).

It lives in the notification area, can sit on the taskbar, and supports start-with-Windows from Settings.

## Requirements

- Windows 10 or later
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build

From the repository root:

```bat
dotnet restore
dotnet build -c Release
```

The output is under `bin\Release\net10.0-windows\`.

## Run

```bat
dotnet run -c Release
```

Or start the built app:

```bat
bin\Release\net10.0-windows\ShowCPU.exe
```

Use the tray icon for Settings, About, and Quit. Drag the widget along the taskbar to move it.
