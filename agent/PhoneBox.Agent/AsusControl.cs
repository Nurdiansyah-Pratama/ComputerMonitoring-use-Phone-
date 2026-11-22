using System.Runtime.InteropServices;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// Controls laptop hardware features like MyASUS directly from PhoneBox without admin rights:
///  - Display refresh rate (e.g. 60 Hz / 120 Hz) via Win32 EnumDisplaySettings / ChangeDisplaySettingsEx
///  - ASUS Fan Profile (Whisper / Standard / Performance) via \\.\ATKACPI (0x00110019 or 0x00120075)
///  - ASUS Battery Health Charging Limit (60% / 80% / 100%) via \\.\ATKACPI (0x00120057)
/// </summary>
public sealed class AsusControl
{
    public sealed record DisplayRefreshState(int Width, int Height, int CurrentHz, int[] SupportedHz);
    public sealed record FanProfileState(bool Supported, string Mode);
    public sealed record BatteryLimitState(bool Supported, int Limit);
    public sealed record AsusSnapshot(DisplayRefreshState Display, FanProfileState Fan, BatteryLimitState Battery);

    private readonly string _statePath;
    private readonly ILogger<AsusControl> _log;
    private readonly Lock _lock = new();
    private int _batteryLimit = 100;

    public AsusControl(string configDir, ILogger<AsusControl> log)
    {
        _statePath = Path.Combine(configDir, "asus-state.json");
        _log = log;
        LoadState();
    }

    public AsusSnapshot Snapshot()
    {
        lock (_lock)
        {
            return new AsusSnapshot(
                GetDisplayState(),
                GetFanState(),
                new BatteryLimitState(IsBatteryLimitSupported(), _batteryLimit));
        }
    }

    public (bool Ok, string? Error, AsusSnapshot State) SetRefreshRate(string? hzSpec)
    {
        lock (_lock)
        {
            var cur = GetDisplayState();
            if (cur.SupportedHz.Length == 0)
                return (false, "Display refresh rate control is unavailable.", Snapshot());

            int targetHz;
            if (string.Equals(hzSpec, "toggle", StringComparison.OrdinalIgnoreCase))
            {
                // Toggle between highest (e.g. 120Hz) and lowest (e.g. 60Hz)
                var max = cur.SupportedHz.Max();
                var min = cur.SupportedHz.Min();
                targetHz = cur.CurrentHz == max ? min : max;
            }
            else if (!int.TryParse(hzSpec, out targetHz) || !cur.SupportedHz.Contains(targetHz))
            {
                return (false, $"Unsupported refresh rate '{hzSpec}'. Supported: {string.Join(", ", cur.SupportedHz)} Hz.", Snapshot());
            }

            var err = ApplyRefreshRate(targetHz);
            if (err is not null) return (false, err, Snapshot());
            _log.LogInformation("AsusControl: display refresh rate set to {Hz} Hz", targetHz);
            return (true, null, Snapshot());
        }
    }

    public (bool Ok, string? Error, AsusSnapshot State) SetFanProfile(string? mode)
    {
        var normalized = NormalizeFanMode(mode);
        if (normalized is null)
            return (false, "Invalid fan mode (use whisper, standard, or performance).", Snapshot());

        lock (_lock)
        {
            var err = ApplyFanProfile(normalized);
            if (err is not null) return (false, err, Snapshot());
            _log.LogInformation("AsusControl: fan profile set to {Mode}", normalized);
            return (true, null, Snapshot());
        }
    }

    public (bool Ok, string? Error, AsusSnapshot State) SetBatteryLimit(int limit)
    {
        if (limit is not (60 or 80 or 100))
            return (false, "Battery limit must be 60, 80, or 100.", Snapshot());

        lock (_lock)
        {
            var err = ApplyBatteryLimit(limit);
            if (err is not null) return (false, err, Snapshot());
            _batteryLimit = limit;
            SaveState();
            _log.LogInformation("AsusControl: battery health charging limit set to {Limit}%", limit);
            return (true, null, Snapshot());
        }
    }

    internal static string? NormalizeFanMode(string? mode) => (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "whisper" or "silent" or "quiet" => "whisper",
        "standard" or "balanced" or "normal" => "standard",
        "performance" or "turbo" => "performance",
        _ => null,
    };

    /// <summary>Maps VivoBook/ZenBook ATKACPI 0x00110019 mode codes (0=standard, 1=whisper, 2=performance).</summary>
    internal static string DecodeVivoFanMode(uint code) => code switch
    {
        1 => "whisper",
        2 => "performance",
        _ => "standard",
    };

    internal static uint EncodeVivoFanMode(string mode) => mode switch
    {
        "whisper" => 1u,
        "performance" => 2u,
        _ => 0u,
    };

    /// <summary>Maps ROG/TUF ATKACPI 0x00120075 mode codes (0=standard, 1=performance, 2=whisper).</summary>
    internal static string DecodeRogFanMode(uint code) => code switch
    {
        1 => "performance",
        2 => "whisper",
        _ => "standard",
    };

    internal static uint EncodeRogFanMode(string mode) => mode switch
    {
        "performance" => 1u,
        "whisper" => 2u,
        _ => 0u,
    };

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_statePath));
            if (doc.RootElement.TryGetProperty("batteryLimit", out var bl) && bl.TryGetInt32(out var v) && v is 60 or 80 or 100)
                _batteryLimit = v;
        }
        catch { }
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new { batteryLimit = _batteryLimit }));
        }
        catch { }
    }

#if WINDOWS
    private const uint IOCTL_ATKACPI = 0x0022240Cu;
    private const uint METHOD_DSTS = 0x53545344u; // "DSTS"
    private const uint METHOD_DEVS = 0x53564544u; // "DEVS"
    private const uint DEV_FAN_VIVO = 0x00110019u;
    private const uint DEV_FAN_ROG = 0x00120075u;
    private const uint DEV_BATTERY_LIMIT = 0x00120057u;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000u;
    private const uint CDS_UPDATEREGISTRY = 0x00000001u;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private static DisplayRefreshState GetDisplayState()
    {
        var cur = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(null, -1 /* ENUM_CURRENT_SETTINGS */, ref cur))
            return new DisplayRefreshState(0, 0, 0, []);

        var freqs = new SortedSet<int>();
        for (var i = 0; ; i++)
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(null, i, ref dm)) break;
            if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency >= 30)
                freqs.Add((int)dm.dmDisplayFrequency);
        }
        if (cur.dmDisplayFrequency >= 30) freqs.Add((int)cur.dmDisplayFrequency);
        return new DisplayRefreshState((int)cur.dmPelsWidth, (int)cur.dmPelsHeight, (int)cur.dmDisplayFrequency, freqs.ToArray());
    }

    private static string? ApplyRefreshRate(int targetHz)
    {
        var cur = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(null, -1, ref cur)) return "Could not read current display mode.";
        if ((int)cur.dmDisplayFrequency == targetHz) return null;

        cur.dmDisplayFrequency = (uint)targetHz;
        cur.dmFields = DM_DISPLAYFREQUENCY;
        var res = ChangeDisplaySettingsExW(null, ref cur, 0, CDS_UPDATEREGISTRY, 0);
        return res == DISP_CHANGE_SUCCESSFUL ? null : $"ChangeDisplaySettingsEx failed (code {res}).";
    }

    private static FanProfileState GetFanState()
    {
        if (AcpiDsts(DEV_FAN_VIVO) is { } vivo && (vivo & 0x10000u) != 0)
            return new FanProfileState(true, DecodeVivoFanMode(vivo & 0xFFFFu));
        if (AcpiDsts(DEV_FAN_ROG) is { } rog && (rog & 0x10000u) != 0)
            return new FanProfileState(true, DecodeRogFanMode(rog & 0xFFFFu));
        return new FanProfileState(false, "standard");
    }

    private static string? ApplyFanProfile(string mode)
    {
        if (AcpiDsts(DEV_FAN_VIVO) is { } vivo && (vivo & 0x10000u) != 0)
        {
            return AcpiDevs(DEV_FAN_VIVO, EncodeVivoFanMode(mode))
                ? null
                : "ASUS ACPI refused fan profile change (0x00110019).";
        }
        if (AcpiDsts(DEV_FAN_ROG) is { } rog && (rog & 0x10000u) != 0)
        {
            return AcpiDevs(DEV_FAN_ROG, EncodeRogFanMode(mode))
                ? null
                : "ASUS ACPI refused fan profile change (0x00120075).";
        }
        return "ASUS fan profile control is not supported on this PC.";
    }

    private static bool IsBatteryLimitSupported() =>
        AcpiDsts(DEV_BATTERY_LIMIT) is { } v && (v & 0x10000u) != 0;

    private static string? ApplyBatteryLimit(int limit)
    {
        if (!IsBatteryLimitSupported()) return "ASUS battery charging limit is not supported on this PC.";
        return AcpiDevs(DEV_BATTERY_LIMIT, (uint)limit)
            ? null
            : "ASUS ACPI refused battery limit change (0x00120057).";
    }

    private static uint? AcpiDsts(uint devId)
    {
        var h = CreateFileW(@"\\.\ATKACPI", 0xC0000000u, 3, 0, 3, 0, 0);
        if (h == -1 || h == 0) return null;
        try
        {
            var inBuf = new byte[12];
            BitConverter.GetBytes(METHOD_DSTS).CopyTo(inBuf, 0);
            BitConverter.GetBytes(4u).CopyTo(inBuf, 4);
            BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
            var outBuf = new byte[16];
            if (!DeviceIoControl(h, IOCTL_ATKACPI, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, 0))
                return null;
            return BitConverter.ToUInt32(outBuf, 0);
        }
        finally { CloseHandle(h); }
    }

    private static bool AcpiDevs(uint devId, uint param)
    {
        var h = CreateFileW(@"\\.\ATKACPI", 0xC0000000u, 3, 0, 3, 0, 0);
        if (h == -1 || h == 0) return false;
        try
        {
            var inBuf = new byte[16];
            BitConverter.GetBytes(METHOD_DEVS).CopyTo(inBuf, 0);
            BitConverter.GetBytes(8u).CopyTo(inBuf, 4);
            BitConverter.GetBytes(devId).CopyTo(inBuf, 8);
            BitConverter.GetBytes(param).CopyTo(inBuf, 12);
            var outBuf = new byte[16];
            return DeviceIoControl(h, IOCTL_ATKACPI, inBuf, inBuf.Length, outBuf, outBuf.Length, out _, 0);
        }
        finally { CloseHandle(h); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint dmFields;
        public int dmPositionX, dmPositionY;
        public uint dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public uint dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string? deviceName, int modeNum, ref DEVMODE devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? deviceName, ref DEVMODE devMode, nint hwnd, uint flags, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, nint ov);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint h);
#else
    private static DisplayRefreshState GetDisplayState() => new(0, 0, 0, []);
    private static string? ApplyRefreshRate(int _) => "Display refresh rate control requires Windows.";
    private static FanProfileState GetFanState() => new(false, "standard");
    private static string? ApplyFanProfile(string _) => "ASUS fan profile control requires Windows.";
    private static bool IsBatteryLimitSupported() => false;
    private static string? ApplyBatteryLimit(int _) => "ASUS battery limit control requires Windows.";
#endif
}
