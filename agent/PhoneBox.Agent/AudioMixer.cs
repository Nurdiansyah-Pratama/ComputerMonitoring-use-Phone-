using System.Diagnostics;
using System.Runtime.InteropServices;
#if WINDOWS
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
#endif

namespace PhoneBox.Agent;

/// <summary>
/// Windows Core Audio Output Switcher (Speaker / Headset / Bluetooth TWS) + Per-App Volume Mixer.
/// Uses NAudio.CoreAudioApi for device/session enumeration and standard COM IPolicyConfig to switch
/// the default playback endpoint without requiring administrator privileges.
/// </summary>
public sealed class AudioMixer
{
    public sealed record AudioDeviceItem(string Id, string Name, string Kind, bool IsDefault);
    public sealed record AppAudioSession(string Key, string Name, int Volume, bool Muted, bool Active);
    public sealed record AudioMixerSnapshot(bool Supported, AudioDeviceItem[] Outputs, AppAudioSession[] Apps, int MasterVolume, bool MasterMuted);

    private readonly ILogger<AudioMixer> _log;
    private readonly Lock _lock = new();

    public AudioMixer(ILogger<AudioMixer> log) => _log = log;

    public AudioMixerSnapshot Snapshot(AudioControl? master = null)
    {
        lock (_lock)
        {
#if WINDOWS
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                string? defaultId = null;
                MMDevice? defaultDev = null;
                try
                {
                    defaultDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    defaultId = defaultDev.ID;
                }
                catch { }

                var outputs = new List<AudioDeviceItem>();
                foreach (var dev in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    using (dev)
                    {
                        var friendly = CleanDeviceName(dev.FriendlyName);
                        outputs.Add(new AudioDeviceItem(
                            dev.ID,
                            friendly,
                            ClassifyDeviceKind(dev.FriendlyName),
                            string.Equals(dev.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
                    }
                }

                var apps = new List<AppAudioSession>();
                if (defaultDev is not null)
                {
                    using (defaultDev)
                    {
                        apps = ReadSessions(defaultDev);
                    }
                }

                var m = master?.Snapshot();
                return new AudioMixerSnapshot(
                    true,
                    outputs.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
                    apps.ToArray(),
                    m?.Volume ?? 0,
                    m?.Muted ?? false);
            }
            catch (Exception ex)
            {
                _log.LogDebug("AudioMixer snapshot failed: {Msg}", ex.Message);
                return new AudioMixerSnapshot(false, [], [], 0, false);
            }
#else
            return new AudioMixerSnapshot(false, [], [], 0, false);
#endif
        }
    }

    public (bool Ok, string? Error, AudioMixerSnapshot State) SetDefaultOutput(string? deviceId, AudioControl? master = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return (false, "Missing audio output device id.", Snapshot(master));

        lock (_lock)
        {
#if WINDOWS
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eConsole));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.eCommunications));
                _log.LogInformation("AudioMixer: switched default audio output to {Id}", deviceId);
                return (true, null, Snapshot(master));
            }
            catch (Exception ex)
            {
                _log.LogWarning("AudioMixer: SetDefaultOutput failed: {Msg}", ex.Message);
                return (false, ex.Message, Snapshot(master));
            }
#else
            return (false, "Audio output switching requires Windows.", Snapshot(master));
#endif
        }
    }

    public (bool Ok, string? Error, AudioMixerSnapshot State) SetAppAudio(string? key, int? volume, bool? mute, AudioControl? master = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Missing app key.", Snapshot(master));

        lock (_lock)
        {
#if WINDOWS
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = dev.AudioSessionManager.Sessions;
                var matched = 0;
                for (var i = 0; i < sessions.Count; i++)
                {
                    var s = sessions[i];
                    if (s.IsSystemSoundsSession) continue;
                    var (sKey, _) = DescribeSession(s);
                    if (!string.Equals(sKey, key, StringComparison.OrdinalIgnoreCase)) continue;
                    matched++;
                    if (volume is { } v)
                    {
                        var clamped = Math.Clamp(v, 0, 100);
                        s.SimpleAudioVolume.Volume = clamped / 100f;
                        if (clamped > 0 && mute is null) s.SimpleAudioVolume.Mute = false;
                    }
                    if (mute is { } m)
                        s.SimpleAudioVolume.Mute = m;
                }
                if (matched == 0)
                    return (false, $"No active audio session for '{key}'.", Snapshot(master));
                return (true, null, Snapshot(master));
            }
            catch (Exception ex)
            {
                return (false, ex.Message, Snapshot(master));
            }
#else
            return (false, "Per-app audio mixer requires Windows.", Snapshot(master));
#endif
        }
    }

    internal static string ClassifyDeviceKind(string friendlyName)
    {
        var s = (friendlyName ?? "").ToLowerInvariant();
        if (s.Contains("bluetooth") || s.Contains("buds") || s.Contains("airpods") || s.Contains("tws") || s.Contains("hands-free") || s.Contains("wf-") || s.Contains("wh-"))
            return "bluetooth";
        if (s.Contains("headphone") || s.Contains("headset") || s.Contains("earphone") || s.Contains("earbud") || s.Contains("rog") || s.Contains("hyperx") || s.Contains("steelseries"))
            return "headphones";
        if (s.Contains("hdmi") || s.Contains("display") || s.Contains("monitor") || s.Contains("nvidia") || s.Contains("amd high definition"))
            return "hdmi";
        return "speaker";
    }

    internal static string CleanDeviceName(string friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return "Audio Output";
        var s = friendlyName.Trim();
        // Shorten verbose driver suffixes like "Speakers (Realtek(R) Audio)" -> "Speakers (Realtek)"
        s = s.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
             .Replace("High Definition Audio", "HD Audio", StringComparison.OrdinalIgnoreCase)
             .Trim();
        return s.Length > 34 ? s[..33].TrimEnd() + "…" : s;
    }

    internal static string FriendlyAppName(string procName) => (procName ?? "").Trim().ToLowerInvariant() switch
    {
        "msedge" => "Edge",
        "chrome" => "Chrome",
        "brave" => "Brave",
        "firefox" => "Firefox",
        "spotify" => "Spotify",
        "discord" => "Discord",
        "whatsapp" or "whatsapp.root" => "WhatsApp",
        "telegram" => "Telegram",
        "code" => "VS Code",
        "vlc" => "VLC",
        "obs64" => "OBS",
        "steam" => "Steam",
        "zoom" => "Zoom",
        "ms-teams" or "teams" => "Teams",
        "" => "App",
        var other => char.ToUpperInvariant(other[0]) + other[1..],
    };

#if WINDOWS
    private static List<AppAudioSession> ReadSessions(MMDevice dev)
    {
        var grouped = new Dictionary<string, (string Name, int Vol, bool Muted, bool Active)>(StringComparer.OrdinalIgnoreCase);
        var sessions = dev.AudioSessionManager.Sessions;
        for (var i = 0; i < sessions.Count; i++)
        {
            try
            {
                var s = sessions[i];
                if (s.IsSystemSoundsSession) continue;
                if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
                var (key, name) = DescribeSession(s);
                if (string.IsNullOrEmpty(key)) continue;
                var vol = (int)Math.Round(Math.Clamp(s.SimpleAudioVolume.Volume, 0f, 1f) * 100);
                var muted = s.SimpleAudioVolume.Mute;
                var active = s.State == AudioSessionState.AudioSessionStateActive;
                if (grouped.TryGetValue(key, out var existing))
                {
                    grouped[key] = (
                        existing.Name,
                        active && !existing.Active ? vol : existing.Vol,
                        existing.Muted && muted,
                        existing.Active || active);
                }
                else
                {
                    grouped[key] = (name, vol, muted, active);
                }
            }
            catch { }
        }

        return grouped
            .Select(kv => new AppAudioSession(kv.Key, kv.Value.Name, kv.Value.Vol, kv.Value.Muted, kv.Value.Active))
            .OrderByDescending(a => a.Active)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static (string Key, string Name) DescribeSession(AudioSessionControl s)
    {
        var pid = (int)s.GetProcessID;
        if (pid > 0)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                var proc = p.ProcessName;
                if (!string.IsNullOrWhiteSpace(proc))
                    return (proc.ToLowerInvariant(), FriendlyAppName(proc));
            }
            catch { }
        }
        var disp = (s.DisplayName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(disp) && !disp.StartsWith("@%", StringComparison.Ordinal))
            return (disp.ToLowerInvariant(), disp);
        return ("", "");
    }

    private enum ERole
    {
        eConsole = 0,
        eMultimedia = 1,
        eCommunications = 2,
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient;

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
        [PreserveSig] int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
        [PreserveSig] int ResetDeviceFormat(string pszDeviceName);
        [PreserveSig] int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
        [PreserveSig] int GetShareMode(string pszDeviceName, IntPtr pMode);
        [PreserveSig] int SetShareMode(string pszDeviceName, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
    }
#endif
}
