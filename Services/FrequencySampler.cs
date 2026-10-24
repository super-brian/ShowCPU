using System.Management;
using System.Runtime.InteropServices;

namespace ShowCPU.Services;

/// <summary>
/// Current clock as a percent of the processor base frequency.
/// Values can be under 100% (power save / thermal) or over 100% (turbo).
/// Graph top is the highest clock seen so far.
/// </summary>
internal sealed class FrequencySampler : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const string FrequencyCounter = @"\Processor Information(_Total)\Processor Frequency";
    private const double MaxPeakMhz = 10000.0;

    private readonly double _baseMhz;
    private readonly bool _hasCounter;
    private IntPtr _query;
    private IntPtr _counter;
    private double _maxMhz;

    public FrequencySampler()
    {
        _baseMhz = ReadBaseMhz();
        _hasCounter = _baseMhz > 0 && TryOpenCounter();
    }

    public double BaseMhz => _baseMhz;

    /// <summary>
    /// Highest clock seen, in MHz. Null until a reading or a saved peak is seeded.
    /// </summary>
    public double? PeakMhz => _maxMhz > 0 ? _maxMhz : null;

    /// <summary>
    /// Top of the CLK graph as percent of base: the highest clock seen so far.
    /// </summary>
    public double? MaxPercentOfBase
    {
        get
        {
            if (_baseMhz <= 0 || _maxMhz <= 0)
                return null;

            return _maxMhz * 100.0 / _baseMhz;
        }
    }

    public void SeedPeakMhz(double? mhz)
    {
        if (mhz is double value)
            NotePeak(value);
    }

    public void Prime()
    {
        _ = TryRead(out _, out _);
    }

    public bool TryRead(out double percentOfBase, out double currentMhz)
    {
        percentOfBase = 0;
        currentMhz = 0;
        if (!_hasCounter || _baseMhz <= 0)
            return false;

        if (PdhCollectQueryData(_query) != 0)
            return false;

        uint status = PdhGetFormattedCounterValue(_counter, PdhFmtDouble, out _, out PdhFormatted value);
        if (status != 0 || value.CStatus != 0 || value.DoubleValue <= 0)
            return false;

        currentMhz = value.DoubleValue;
        percentOfBase = currentMhz * 100.0 / _baseMhz;
        if (percentOfBase < 0)
            percentOfBase = 0;

        NotePeak(currentMhz);
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

    private void NotePeak(double mhz)
    {
        if (!double.IsFinite(mhz) || mhz <= _maxMhz || mhz > MaxPeakMhz)
            return;

        _maxMhz = mhz;
    }

    private static double ReadBaseMhz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            double total = 0;
            int count = 0;
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                using (obj)
                {
                    if (obj["MaxClockSpeed"] is not null and not DBNull)
                    {
                        total += Convert.ToDouble(obj["MaxClockSpeed"]);
                        count++;
                    }
                }
            }

            return count > 0 ? total / count : 0;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return 0;
        }
    }

    private bool TryOpenCounter()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0)
            return false;

        if (PdhAddEnglishCounter(_query, FrequencyCounter, IntPtr.Zero, out _counter) != 0)
        {
            Dispose();
            return false;
        }

        return true;
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
