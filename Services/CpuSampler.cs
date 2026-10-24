using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShowCPU.Services;

/// <summary>
/// Share of time the processors were busy, not scaled by clock speed.
/// </summary>
internal sealed class CpuSampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const string BusyCounter = @"\Processor Information(_Total)\% Processor Time";

    private readonly bool _hasCounter;
    private IntPtr _query;
    private IntPtr _counter;
    private long _lastStamp;

    private bool _hasBaseline;
    private long _idle;
    private long _kernel;
    private long _user;

    public CpuSampler()
    {
        _hasCounter = TryOpenCounter();
        _lastStamp = Stopwatch.GetTimestamp();
    }

    public void Prime()
    {
        _ = TryRead(out _);
    }

    public bool TryRead(out double percent)
    {
        percent = 0;
        long now = Stopwatch.GetTimestamp();
        double elapsed = Stopwatch.GetElapsedTime(_lastStamp, now).TotalSeconds;
        _lastStamp = now;

        bool ok = _hasCounter ? TryReadCounter(out percent) : TryReadBusyTime(out percent);
        if (!ok)
            return false;

        // A sample that spans sleep is not a one-second reading.
        if (elapsed > 3)
            return false;

        if (percent < 0)
            percent = 0;
        else if (percent > 100)
            percent = 100;
        return true;
    }

    public void Dispose()
    {
        if (_query == IntPtr.Zero)
            return;

        _ = PdhCloseQuery(_query);
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
    }

    private bool TryOpenCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return false;

        if (PdhAddEnglishCounter(_query, BusyCounter, IntPtr.Zero, out _counter) != 0)
        {
            Dispose();
            return false;
        }

        return true;
    }

    private bool TryReadCounter(out double percent)
    {
        percent = 0;
        if (PdhCollectQueryData(_query) != 0)
            return false;

        uint status = PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out PdhFormatted value);
        if (status != 0 || value.CStatus != 0)
            return false;

        percent = value.DoubleValue;
        return true;
    }

    private bool TryReadBusyTime(out double percent)
    {
        percent = 0;
        if (!NativeMethods.GetSystemTimes(out NativeMethods.FILETIME idle, out NativeMethods.FILETIME kernel, out NativeMethods.FILETIME user))
            return false;

        long idleNow = ToLong(idle);
        long kernelNow = ToLong(kernel);
        long userNow = ToLong(user);

        if (!_hasBaseline)
        {
            _idle = idleNow;
            _kernel = kernelNow;
            _user = userNow;
            _hasBaseline = true;
            return false;
        }

        long idleDelta = idleNow - _idle;
        long totalDelta = (kernelNow - _kernel) + (userNow - _user);
        _idle = idleNow;
        _kernel = kernelNow;
        _user = userNow;

        if (totalDelta <= 0 || idleDelta < 0 || idleDelta > totalDelta)
            return false;

        percent = (totalDelta - idleDelta) * 100.0 / totalDelta;
        return true;
    }

    private static long ToLong(NativeMethods.FILETIME value)
    {
        return ((long)value.High << 32) | value.Low;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PdhFormatted
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFormatted value);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
