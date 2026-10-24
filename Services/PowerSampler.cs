using System.Runtime.InteropServices;
using System.Text;

namespace ShowCPU.Services;

/// <summary>
/// CPU package power in watts via the Windows Energy Meter Interface (EMI / RAPL).
/// Available on Windows 11 bare metal (and some Windows 10 devices with metering hardware).
/// No elevation required.
/// </summary>
internal sealed class PowerSampler : IDisposable
{
    private const uint IoctlGetVersion = 0x00224000;
    private const uint IoctlGetMetadataSize = 0x00224004;
    private const uint IoctlGetMetadata = 0x00224008;
    private const uint IoctlGetMeasurement = 0x0022400C;
    private const int EmiVersionV1 = 1;
    private const int EmiVersionV2 = 2;
    private const int MeasurementSize = 16;
    private const double PicowattHoursToWattsFactor = 3.6e-9; // pWh / s -> W
    private const double HundredNsToSeconds = 1e-7;
    private const double MaxReasonableWatts = 500.0;

    private static readonly Guid EnergyMeterGuid = new("45BD8344-7ED6-49CF-A440-C276C933B053");

    private IntPtr _handle = InvalidHandle;
    private int _version;
    private int _channelCount;
    private int _channelIndex;
    private bool _hasBaseline;
    private ulong _lastEnergy;
    private ulong _lastTime;
    private double _maxWatts;

    private static readonly IntPtr InvalidHandle = new(-1);

    public PowerSampler()
    {
        _ = TryOpen();
    }

    /// <summary>
    /// Top of the PWR graph in watts. Grows with observed peaks.
    /// </summary>
    public double? MaxWatts => _maxWatts > 0 ? _maxWatts : null;

    public void Prime()
    {
        _ = TryRead(out _);
    }

    public bool TryRead(out double watts)
    {
        watts = 0;
        if (_handle == IntPtr.Zero || _handle == InvalidHandle)
            return false;

        if (!TryReadMeasurement(out ulong energy, out ulong time))
            return false;

        if (!_hasBaseline)
        {
            _lastEnergy = energy;
            _lastTime = time;
            _hasBaseline = true;
            return false;
        }

        if (energy < _lastEnergy || time <= _lastTime)
        {
            _lastEnergy = energy;
            _lastTime = time;
            return false;
        }

        double deltaSeconds = (time - _lastTime) * HundredNsToSeconds;
        ulong deltaEnergy = energy - _lastEnergy;
        _lastEnergy = energy;
        _lastTime = time;

        if (deltaSeconds <= 0 || deltaSeconds > 3)
            return false;

        watts = deltaEnergy * PicowattHoursToWattsFactor / deltaSeconds;
        if (!double.IsFinite(watts) || watts < 0 || watts > MaxReasonableWatts)
            return false;

        UpdateMaxWatts(watts);
        return true;
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero || _handle == InvalidHandle)
            return;

        _ = CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private void UpdateMaxWatts(double watts)
    {
        // Leave headroom so typical load sits mid-graph; never shrink.
        double floor = Math.Max(10.0, watts * 1.25);
        if (floor > _maxWatts)
            _maxWatts = Math.Min(floor, MaxReasonableWatts);
    }

    private bool TryOpen()
    {
        foreach (string path in EnumerateEnergyMeters())
        {
            if (TryOpenDevice(path))
                return true;
        }

        return false;
    }

    private bool TryOpenDevice(string path)
    {
        IntPtr handle = CreateFileW(
            path,
            0x80000000, // GENERIC_READ
            0x00000003, // FILE_SHARE_READ | FILE_SHARE_WRITE
            IntPtr.Zero,
            3, // OPEN_EXISTING
            0,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == InvalidHandle)
            return false;

        try
        {
            if (!DeviceIo(handle, IoctlGetVersion, 2, out byte[] versionBuf) || versionBuf.Length < 2)
            {
                CloseHandle(handle);
                return false;
            }

            int version = BitConverter.ToUInt16(versionBuf, 0);
            if (version is not (EmiVersionV1 or EmiVersionV2))
            {
                CloseHandle(handle);
                return false;
            }

            if (!DeviceIo(handle, IoctlGetMetadataSize, 4, out byte[] sizeBuf) || sizeBuf.Length < 4)
            {
                CloseHandle(handle);
                return false;
            }

            int metadataSize = BitConverter.ToInt32(sizeBuf, 0);
            if (metadataSize < 8 || metadataSize > 64 * 1024)
            {
                CloseHandle(handle);
                return false;
            }

            if (!DeviceIo(handle, IoctlGetMetadata, metadataSize, out byte[] metadata))
            {
                CloseHandle(handle);
                return false;
            }

            if (!TrySelectChannel(version, metadata, out int channelCount, out int channelIndex))
            {
                CloseHandle(handle);
                return false;
            }

            _handle = handle;
            _version = version;
            _channelCount = channelCount;
            _channelIndex = channelIndex;
            return true;
        }
        catch
        {
            CloseHandle(handle);
            return false;
        }
    }

    private static bool TrySelectChannel(int version, byte[] metadata, out int channelCount, out int channelIndex)
    {
        channelCount = 0;
        channelIndex = 0;
        List<string> names = ParseChannelNames(version, metadata);
        if (names.Count == 0)
            return false;

        channelCount = names.Count;
        int pkg = -1;
        for (int i = 0; i < names.Count; i++)
        {
            string name = names[i];
            if (name.Contains("DRAM", StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.Contains("PKG", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Package", StringComparison.OrdinalIgnoreCase))
            {
                pkg = i;
                break;
            }
        }

        if (pkg >= 0)
        {
            channelIndex = pkg;
            return true;
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (!names[i].Contains("DRAM", StringComparison.OrdinalIgnoreCase))
            {
                channelIndex = i;
                return true;
            }
        }

        channelIndex = 0;
        return true;
    }

    private static List<string> ParseChannelNames(int version, byte[] metadata)
    {
        var names = new List<string>();
        if (version == EmiVersionV2)
        {
            if (metadata.Length < 68)
                return names;

            int count = BitConverter.ToUInt16(metadata, 66);
            int offset = 68;
            for (int i = 0; i < count; i++)
            {
                if (offset + 6 > metadata.Length)
                    break;

                int nameSize = BitConverter.ToUInt16(metadata, offset + 4);
                if (nameSize < 0 || offset + 6 + nameSize > metadata.Length)
                    break;

                names.Add(DecodeUtf16Z(metadata, offset + 6, nameSize));
                offset += 6 + nameSize;
            }

            return names;
        }

        if (version == EmiVersionV1)
        {
            if (metadata.Length < 72)
                return names;

            int nameSize = BitConverter.ToUInt16(metadata, 70);
            if (nameSize < 0 || 72 + nameSize > metadata.Length)
                return names;

            string name = DecodeUtf16Z(metadata, 72, nameSize);
            names.Add(string.IsNullOrWhiteSpace(name) ? "EMI" : name);
        }

        return names;
    }

    private static string DecodeUtf16Z(byte[] buffer, int offset, int byteCount)
    {
        if (byteCount <= 0)
            return string.Empty;

        string text = Encoding.Unicode.GetString(buffer, offset, byteCount);
        int z = text.IndexOf('\0');
        return z >= 0 ? text.Substring(0, z) : text;
    }

    private bool TryReadMeasurement(out ulong energy, out ulong time)
    {
        energy = 0;
        time = 0;
        int bytesNeeded = _version == EmiVersionV2
            ? MeasurementSize * Math.Max(1, _channelCount)
            : MeasurementSize;

        if (!DeviceIo(_handle, IoctlGetMeasurement, bytesNeeded, out byte[] raw) || raw.Length < MeasurementSize)
            return false;

        int offset = _version == EmiVersionV2
            ? _channelIndex * MeasurementSize
            : 0;
        if (offset + MeasurementSize > raw.Length)
            return false;

        energy = BitConverter.ToUInt64(raw, offset);
        time = BitConverter.ToUInt64(raw, offset + 8);
        return true;
    }

    private static bool DeviceIo(IntPtr handle, uint code, int outputSize, out byte[] output)
    {
        output = new byte[outputSize];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, output, outputSize, out int written, IntPtr.Zero))
        {
            output = [];
            return false;
        }

        if (written > 0 && written < output.Length)
            Array.Resize(ref output, written);
        return written > 0;
    }

    private static List<string> EnumerateEnergyMeters()
    {
        var paths = new List<string>();
        Guid guid = EnergyMeterGuid;
        uint size = 0;
        if (CM_Get_Device_Interface_List_SizeW(ref size, ref guid, IntPtr.Zero, 0) != 0 || size <= 1)
            return paths;

        char[] buffer = new char[size];
        if (CM_Get_Device_Interface_ListW(ref guid, IntPtr.Zero, buffer, size, 0) != 0)
            return paths;

        var current = new StringBuilder();
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = buffer[i];
            if (c == '\0')
            {
                if (current.Length > 0)
                {
                    paths.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        return paths;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(
        ref uint size,
        ref Guid interfaceClassGuid,
        IntPtr deviceId,
        uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(
        ref Guid interfaceClassGuid,
        IntPtr deviceId,
        [Out] char[] buffer,
        uint bufferLength,
        uint flags);
}
