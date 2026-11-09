using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using NAudio.CoreAudioApi;

namespace MusicOverlay.Modules;

public sealed record AppSession(int Pid, string Name, float Volume, bool Muted);

/// <summary>
/// Громкость/мьют устройства вывода и микшер по приложениям.
/// Реализация на NAudio Core Audio (устойчивый COM-слой вместо ручных интерфейсов).
/// </summary>
public sealed class AudioService : IDisposable
{
    private const int PollIntervalMs = 1500;
    private const int DeviceRecheckTicks = 4; // пересоздание цепочки каждые ~6 с

    private DispatcherTimer? _timer;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private int _ticks;
    private volatile bool _disposed;

    public event Action? Changed;

    public float MasterVolume { get; private set; }

    public bool Muted { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public IReadOnlyList<AppSession> Sessions { get; private set; } = Array.Empty<AppSession>();

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    /// <summary>Перечитывает громкость/мьют/устройство/сессии и поднимает Changed.</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        EnsureDevice();
        ReadMaster();
        RefreshSessions();
        Changed?.Invoke();
    }

    public void SetMasterVolume(float value01)
    {
        float value = Math.Clamp(value01, 0f, 1f);
        try
        {
            EnsureDevice();
            if (_device is null)
            {
                return;
            }

            _device.AudioEndpointVolume.MasterVolumeLevelScalar = value;
            MasterVolume = value;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LogAudio($"set-volume-fail: {ex.Message}");
        }
    }

    public void ToggleMute()
    {
        try
        {
            EnsureDevice();
            if (_device is null)
            {
                return;
            }

            bool next = !Muted;
            _device.AudioEndpointVolume.Mute = next;
            Muted = next;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LogAudio($"set-mute-fail: {ex.Message}");
        }
    }

    public void SetSessionVolume(int pid, float value01)
    {
        float value = Math.Clamp(value01, 0f, 1f);
        try
        {
            var control = FindLiveSession(pid);
            if (control is null)
            {
                return;
            }

            control.SimpleAudioVolume.Volume = value;
            Sessions = Sessions.Select(s => s.Pid == pid ? s with { Volume = value } : s).ToList();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LogAudio($"set-session-volume-fail: {ex.Message}");
        }
    }

    public void ToggleSessionMute(int pid)
    {
        try
        {
            var control = FindLiveSession(pid);
            if (control is null)
            {
                return;
            }

            bool next = !control.SimpleAudioVolume.Mute;
            control.SimpleAudioVolume.Mute = next;
            Sessions = Sessions.Select(s => s.Pid == pid ? s with { Muted = next } : s).ToList();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            LogAudio($"set-session-mute-fail: {ex.Message}");
        }
    }

    /// <summary>Находит живую сессию процесса повторной итерацией.</summary>
    private AudioSessionControl? FindLiveSession(int pid)
    {
        try
        {
            EnsureDevice();
            if (_device is null)
            {
                return null;
            }

            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            SessionCollection collection = manager.Sessions;
            for (int i = 0; i < collection.Count; i++)
            {
                AudioSessionControl session = collection[i];
                try
                {
                    if ((int)session.State == 1 && session.GetProcessID == (uint)pid)
                    {
                        return session;
                    }
                }
                catch
                {
                    // сессия могла закрыться
                }
            }
        }
        catch (Exception ex)
        {
            LogAudio($"find-session-fail: {ex.Message}");
        }

        return null;
    }

    private void EnsureDevice()
    {
        bool missing = _device is null;
        bool periodic = _ticks++ % DeviceRecheckTicks == 0;
        if (missing || periodic)
        {
            RecreateDevice();
        }
    }

    private void RecreateDevice()
    {
        MMDevice? newDevice = null;
        try
        {
            _enumerator ??= new MMDeviceEnumerator();
            newDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            string name = string.Empty;
            try
            {
                name = newDevice.FriendlyName;
            }
            catch
            {
                // имя не критично
            }

            MMDevice? oldDevice = _device;
            _device = newDevice;
            DeviceName = name;
            newDevice = null;
            if (oldDevice is not null)
            {
                try
                {
                    oldDevice.Dispose();
                }
                catch
                {
                    // уже освобождено
                }
            }
        }
        catch (Exception ex)
        {
            LogAudio($"recreate-fail: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (newDevice is not null)
            {
                try
                {
                    newDevice.Dispose();
                }
                catch
                {
                    // не освобождён из-за ошибки
                }
            }
        }
    }

    /// <summary>Читает громкость и мьют устройства вывода.</summary>
    private void ReadMaster()
    {
        try
        {
            if (_device is null)
            {
                return;
            }

            var endpoint = _device.AudioEndpointVolume;
            MasterVolume = Math.Clamp(endpoint.MasterVolumeLevelScalar, 0f, 1f);
            Muted = endpoint.Mute;
        }
        catch (Exception ex)
        {
            LogAudio($"readmaster-fail: {ex.Message}");
            // устройство могло быть отключено — следующий Refresh пересоздаст цепочку
            try
            {
                _device?.Dispose();
            }
            catch
            {
                // ignore
            }

            _device = null;
        }
    }

    /// <summary>Пересобирает список активных сессий воспроизведения.</summary>
    private void RefreshSessions()
    {
        if (_device is null)
        {
            return;
        }

        try
        {
            var manager = _device.AudioSessionManager;
            manager.RefreshSessions();
            SessionCollection collection = manager.Sessions;
            var sessions = new List<AppSession>();

            for (int i = 0; i < collection.Count; i++)
            {
                AudioSessionControl session = collection[i];
                try
                {
                    if ((int)session.State != 1)
                    {
                        continue; // не активна
                    }

                    int pid;
                    try
                    {
                        pid = (int)session.GetProcessID;
                    }
                    catch
                    {
                        continue;
                    }

                    if (sessions.Any(s => s.Pid == pid))
                    {
                        continue; // уникальность по Pid
                    }

                    var volume = session.SimpleAudioVolume;
                    sessions.Add(new AppSession(
                        pid,
                        ResolveProcessName(pid),
                        Math.Clamp(volume.Volume, 0f, 1f),
                        volume.Mute));
                }
                catch
                {
                    // одна сессия не должна ломать весь обход
                }
            }

            Sessions = sessions;
        }
        catch (Exception ex)
        {
            // COM-ошибка: оставляем прежний список Sessions
            LogAudio($"sessions-fail: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ResolveProcessName(int pid)
    {
        if (pid <= 0)
        {
            return "Системные звуки";
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return $"pid {pid}";
        }
    }

    private static void LogAudio(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "musicoverlay_ui.log"),
                $"{DateTime.Now:HH:mm:ss.fff} audio {line}{Environment.NewLine}");
        }
        catch
        {
            // лог не критичен
        }
    }

    public void Dispose()
    {
        _disposed = true;

        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        Sessions = Array.Empty<AppSession>();

        try
        {
            _device?.Dispose();
        }
        catch
        {
            // ignore
        }

        _device = null;

        try
        {
            _enumerator?.Dispose();
        }
        catch
        {
            // ignore
        }

        _enumerator = null;

        GC.SuppressFinalize(this);
    }
}
