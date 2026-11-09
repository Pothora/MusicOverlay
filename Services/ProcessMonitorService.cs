using System.Diagnostics;

namespace MusicOverlay.Services;

public sealed record RunningApp(
    int Pid,
    string DisplayName,
    double CpuPercent,
    double MemoryMb,
    string? ExePath);

/// <summary>
    /// Снимок запущенных приложений с окном, отсортированный по потреблению CPU.
/// Опрос идёт в фоновом потоке (2 с) — не подвисает интерфейс во время игры;
/// путь exe кэшируется по pid (MainModule — самый дорогой вызов).
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private readonly Dictionary<int, (long Ticks, DateTime Time)> _previous = new();
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string?> _pathCache = new();
    private System.Threading.Timer? _timer;

    public IReadOnlyList<RunningApp> Apps { get; private set; } = Array.Empty<RunningApp>();

    public event Action? Changed;

    /// <summary>Запускает фоновый опрос: первый снимок сразу, дальше каждые 2 с.</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    if (_disposed)
                    {
                        return;
                    }

                    Apps = Sample();
                    Changed?.Invoke();
                }
                catch
                {
                    // сбой одного цикла не должен ронять опрос
                }
            },
            null,
            0,
            2000);
    }

    public IReadOnlyList<RunningApp> Sample()
    {
        var now = DateTime.UtcNow;
        var result = new List<RunningApp>();
        var alive = new HashSet<int>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                // только приложения с главным окном (заголовок у окон бывает пустым).
                if (process.Id == Environment.ProcessId ||
                    process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                alive.Add(process.Id);

                long totalTicks = process.TotalProcessorTime.Ticks;
                double cpu = 0;
                if (_previous.TryGetValue(process.Id, out var prev))
                {
                    double elapsed = (now - prev.Time).TotalMilliseconds;
                    if (elapsed > 300)
                    {
                        cpu = (totalTicks - prev.Ticks) / (elapsed * 1000.0) /
                              Environment.ProcessorCount * 100.0;
                        if (cpu < 0)
                        {
                            cpu = 0;
                        }
                    }
                }

                _previous[process.Id] = (totalTicks, now);

                double memoryMb = process.WorkingSet64 / (1024.0 * 1024.0);

                // MainModule очень дорогой — запрашиваем один раз на pid
                string? exePath = null;
                if (_pathCache.TryGetValue(process.Id, out string? cachedPath))
                {
                    exePath = cachedPath;
                }
                else
                {
                    try
                    {
                        exePath = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // нет доступа к чужому процессу
                    }

                    _pathCache[process.Id] = exePath;
                }

                result.Add(new RunningApp(
                    process.Id,
                    ResolveName(process.ProcessName, exePath),
                    cpu,
                    memoryMb,
                    exePath));
            }
            catch
            {
                // процесс мог завершиться во время обхода
            }
            finally
            {
                process.Dispose();
            }
        }

        // убираем записи о завершённых процессах
        if (_previous.Count > alive.Count)
        {
            foreach (int pid in _previous.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _previous.Remove(pid);
            }
        }

        if (_pathCache.Count > alive.Count)
        {
            foreach (int pid in _pathCache.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _pathCache.Remove(pid);
            }
        }

        result.Sort(static (a, b) =>
        {
            int byCpu = b.CpuPercent.CompareTo(a.CpuPercent);
            return byCpu != 0 ? byCpu : b.MemoryMb.CompareTo(a.MemoryMb);
        });

        return result;
    }

    private string ResolveName(string processName, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return processName;
        }

        if (_nameCache.TryGetValue(exePath, out string? cached))
        {
            return cached;
        }

        string name = processName;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(exePath).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                name = description;
            }
        }
        catch
        {
            // описание недоступно — оставляем имя процесса
        }

        _nameCache[exePath] = name;
        return name;
    }

    public void Dispose()
    {
        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private volatile bool _disposed;
}
