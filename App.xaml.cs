using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using ShowCPU.Services;

namespace ShowCPU;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private SettingsStore? _store;
    private CpuSampler? _sampler;
    private FrequencySampler? _frequency;
    private PowerSampler? _power;
    private ProcessCpuTracker? _processCpu;
    private HighCpuLogger? _highCpuLog;
    private WidgetWindow? _widget;
    private TrayController? _tray;
    private AboutWindow? _about;
    private DispatcherTimer? _sampleTimer;
    private DispatcherTimer? _placeTimer;
    private bool _quitting;
    private bool _widgetClosed;
    private AppSettings _appSettings = new();
    private long _placementReverifyAt;
    private long _lastPlacementRefreshRequest;
    private TaskbarSlot _lastPlacedSlot = TaskbarSlot.Hidden;
    private int? _lastCpuShown;
    private string? _lastClockGhz;
    private int? _lastPowerShown;
    private long _highCpuLogCooldownUntil;
    private bool _highCpuBaselineReady;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!AcquireInstance())
        {
            Shutdown();
            return;
        }
        System.Windows.Forms.Application.EnableVisualStyles();

        _store = new SettingsStore();
        _appSettings = _store.LoadOrCreate();
        // Running the app opts into start-with-Windows. Quit Forever turns that off.
        _appSettings.StartWithWindows = true;
        try
        {
            StartupService.Apply(true);
            _store.Save(_appSettings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // The widget still runs if the Run key or settings file cannot be updated.
        }

        _sampler = new CpuSampler();
        _sampler.Prime();
        _frequency = new FrequencySampler();
        _frequency.SeedPeakMhz(_appSettings.PeakClockMhz);
        _frequency.Prime();
        _power = new PowerSampler();
        _power.SeedPeakWatts(_appSettings.PeakPowerWatts);
        _power.Prime();
        _processCpu = new ProcessCpuTracker();
        _highCpuLog = new HighCpuLogger(_appSettings, _store);

        _widget = new WidgetWindow();
        _widget.DragCompleted += OnWidgetDragged;
        _widget.DisplayMetricsChanged += OnDisplayMetricsChanged;
        _widget.Closed += (_, _) =>
        {
            _widgetClosed = true;
            Quit();
        };

        SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;

        _tray = new TrayController(ShowAbout, Quit, QuitForever);

        // Schedule the next sample 1s after the current one finishes (not a fixed
        // wall clock cadence), so a busy UI thread does not pile up ticks.
        _sampleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _sampleTimer.Tick += OnSample;
        _sampleTimer.Start();

        _placeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _placeTimer.Tick += (_, _) => PlaceWidget();
        _placeTimer.Start();
        PlaceWidget();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        _sampleTimer?.Stop();
        _placeTimer?.Stop();
        _sampler?.Dispose();
        _sampler = null;
        _frequency?.Dispose();
        _frequency = null;
        _power?.Dispose();
        _power = null;
        _tray?.Dispose();
        _tray = null;
        ReleaseInstance();
        base.OnExit(e);
    }

    private void OnSample(object? sender, EventArgs e)
    {
        _sampleTimer?.Stop();
        try
        {
            if (_widget == null || _tray == null || _quitting)
                return;

            try
            {
                double? cpuPercent = null;
                int? cpuShown = null;
                if (_sampler != null && _sampler.TryRead(out double percent))
                {
                    cpuPercent = percent;
                    cpuShown = (int)Math.Clamp(Math.Round(percent, MidpointRounding.AwayFromZero), 0, 100);
                }

                double? clockPercent = null;
                double? clockMhz = null;
                string? clockGhz = null;
                if (_frequency != null && _frequency.TryRead(out double freqPercent, out double mhz))
                {
                    clockPercent = freqPercent;
                    clockMhz = mhz;
                    clockGhz = FormatTrayGhz(mhz);
                }

                int? powerShown = null;
                double? powerWatts = null;
                if (_power != null && _power.TryRead(out double watts))
                {
                    powerWatts = watts;
                    powerShown = (int)Math.Clamp(Math.Round(watts, MidpointRounding.AwayFromZero), 0, 999);
                }

                if (cpuPercent is null && clockPercent is null && powerWatts is null)
                    return;

                if (cpuShown is int)
                    _lastCpuShown = cpuShown;
                if (clockGhz is string)
                    _lastClockGhz = clockGhz;
                if (powerShown is int)
                    _lastPowerShown = powerShown;

                _widget.SetUsage(
                    cpuPercent,
                    cpuShown,
                    clockPercent,
                    clockMhz,
                    _frequency?.MaxPercentOfBase,
                    powerWatts,
                    powerShown,
                    _power?.MaxWatts);
                _tray.SetCpu(_lastCpuShown, _lastClockGhz, _lastPowerShown);
                RememberPeaks();

                MaybeLogHighCpu(cpuPercent, clockMhz);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
            {
                // Keep sampling on the next scheduled tick.
            }
        }
        finally
        {
            if (!_quitting && _sampleTimer != null)
                _sampleTimer.Start();
        }
    }

    private static string FormatTrayGhz(double mhz)
    {
        double ghz = Math.Clamp(mhz, 0, 99_999) / 1000.0;
        return ghz >= 10.0
            ? Math.Round(ghz, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) + "G"
            : ghz.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "G";
    }

    private void RememberPeaks()
    {
        if (_store == null)
            return;

        bool changed = false;
        if (_frequency?.PeakMhz is double clockPeak
            && (_appSettings.PeakClockMhz is not double savedClock || clockPeak > savedClock + 0.01))
        {
            _appSettings.PeakClockMhz = clockPeak;
            changed = true;
        }

        if (_power?.MaxWatts is double powerPeak
            && (_appSettings.PeakPowerWatts is not double savedPower || powerPeak > savedPower + 0.01))
        {
            _appSettings.PeakPowerWatts = powerPeak;
            changed = true;
        }

        if (!changed)
            return;

        try
        {
            _store.Save(_appSettings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory peak even if the file cannot be written.
        }
    }

    private void MaybeLogHighCpu(double? cpuPercent, double? clockMhz)
    {
        if (_processCpu == null || _highCpuLog == null)
            return;

        // Process enumeration is expensive -- only when CPU is hot, and not every second.
        if (cpuPercent is not double hot || hot <= 95.0)
        {
            _processCpu.Reset();
            _highCpuBaselineReady = false;
            _highCpuLogCooldownUntil = 0;
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_highCpuLogCooldownUntil != 0 && now < _highCpuLogCooldownUntil)
            return;

        if (!_highCpuBaselineReady)
        {
            _ = _processCpu.TakeTopAndRoll(0);
            _highCpuBaselineReady = true;
            return;
        }

        IReadOnlyList<ProcessCpuEntry> top = _processCpu.TakeTopAndRoll(10);
        if (top.Count == 0)
            return;

        _highCpuLog.LogIfHot(hot, top, clockMhz);
        // Cooldown so a sustained spike does not scan all processes every second.
        _highCpuLogCooldownUntil = now + (Stopwatch.Frequency * 10);
        _highCpuBaselineReady = false;
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RequestPlacementRefresh);
    }

    private void OnDisplayMetricsChanged()
    {
        RequestPlacementRefresh();
    }

    private void RequestPlacementRefresh()
    {
        if (_quitting)
            return;

        long now = Environment.TickCount64;
        if (_lastPlacementRefreshRequest != 0 && now - _lastPlacementRefreshRequest < 300)
            return;
        _lastPlacementRefreshRequest = now;

        TaskbarPlacement.ResetCaches();
        _placementReverifyAt = now + 1500;
        PlaceWidget(force: true);
    }

    private void PlaceWidget(bool force = false)
    {
        if (_widget == null || _quitting || _widget.IsDragging)
            return;

        try
        {
            bool timedForce = _placementReverifyAt != 0
                && Environment.TickCount64 >= _placementReverifyAt;
            if (timedForce)
            {
                _placementReverifyAt = 0;
                TaskbarPlacement.ResetCaches();
                force = true;
            }

            TaskbarSlot slot = TaskbarPlacement.GetSlot(
                _appSettings.TaskbarOffset,
                _appSettings.TaskbarAlongLength,
                _appSettings.TaskbarDpi);

            if (!force && slot.IsVisible && LooksLikeDisplayChange(_lastPlacedSlot, slot))
            {
                // Apply immediately with a fresh cache, then re-check after shell settles.
                TaskbarPlacement.ResetCaches();
                slot = TaskbarPlacement.GetSlot(
                    _appSettings.TaskbarOffset,
                    _appSettings.TaskbarAlongLength,
                    _appSettings.TaskbarDpi);
                force = true;
                _placementReverifyAt = Environment.TickCount64 + 1500;
            }

            _lastPlacedSlot = slot;
            _widget.Apply(slot, force);
            PersistPlacement(slot);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The next tick tries again. Explorer may be restarting.
        }
    }

    private static bool LooksLikeDisplayChange(TaskbarSlot previous, TaskbarSlot next)
    {
        if (!previous.IsVisible || !next.IsVisible)
            return false;

        return previous.TaskbarHwnd != next.TaskbarHwnd
            || previous.AlongStart != next.AlongStart
            || previous.AlongLength != next.AlongLength
            || previous.PixelWidth != next.PixelWidth
            || previous.PixelHeight != next.PixelHeight
            || previous.Dpi != next.Dpi
            || Math.Abs(previous.DipHeight - next.DipHeight) > 0.1;
    }

    private void OnWidgetDragged(int offset)
    {
        if (_store == null)
            return;

        TaskbarSlot slot = TaskbarPlacement.GetSlot(offset, null, null);
        _appSettings.TaskbarOffset = slot.IsVisible ? slot.EffectiveOffset : offset;
        _appSettings.TaskbarAlongLength = slot.IsVisible ? slot.AlongLength : null;
        _appSettings.TaskbarDpi = slot.IsVisible ? slot.Dpi : null;
        try
        {
            _store.Save(_appSettings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // The widget still stays where it was dropped for this session.
        }
    }

    private void PersistPlacement(TaskbarSlot slot)
    {
        if (!slot.IsVisible || _store == null || _appSettings.TaskbarOffset is null)
            return;

        bool changed = _appSettings.TaskbarOffset != slot.EffectiveOffset
            || _appSettings.TaskbarAlongLength != slot.AlongLength
            || _appSettings.TaskbarDpi != slot.Dpi;
        if (!changed)
            return;

        _appSettings.TaskbarOffset = slot.EffectiveOffset;
        _appSettings.TaskbarAlongLength = slot.AlongLength;
        _appSettings.TaskbarDpi = slot.Dpi;
        try
        {
            _store.Save(_appSettings);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // Keep the in-memory value even if the file cannot be written.
        }
    }

    private void ShowAbout()
    {
        if (_quitting)
            return;

        if (_about != null)
        {
            if (_about.WindowState == WindowState.Minimized)
                _about.WindowState = WindowState.Normal;
            _about.Activate();
            return;
        }

        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        _about.Show();
        _about.Activate();
    }

    private void QuitForever()
    {
        if (_quitting)
            return;

        _appSettings.StartWithWindows = false;
        try
        {
            StartupService.Apply(false);
            _store?.Save(_appSettings);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            // Still quit even if the Run key cannot be cleared.
        }

        Quit();
    }

    private void Quit()
    {
        if (_quitting)
            return;

        _quitting = true;
        SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
        _sampleTimer?.Stop();
        _placeTimer?.Stop();
        _sampler?.Dispose();
        _sampler = null;
        _frequency?.Dispose();
        _frequency = null;
        _power?.Dispose();
        _power = null;
        _tray?.Dispose();
        _tray = null;

        _about?.Close();

        if (_widget != null && !_widgetClosed)
        {
            _widgetClosed = true;
            _widget.Close();
        }

        ReleaseInstance();
        Shutdown();
    }

    private bool AcquireInstance()
    {
        _mutex = new Mutex(true, @"Local\ShowCPU.SingleInstance", out bool created);
        if (created)
            return true;

        try
        {
            if (_mutex.WaitOne(TimeSpan.Zero))
                return true;
        }
        catch (AbandonedMutexException)
        {
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    private void ReleaseInstance()
    {
        if (_mutex == null)
            return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // This thread does not own the mutex.
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
