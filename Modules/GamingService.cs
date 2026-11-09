using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace MusicOverlay.Modules;

/// <summary>
/// Детект активной игры: (а) известный процесс-игра или (б) полноэкранное окно
/// на основном мониторе, не принадлежащее системному процессу.
/// Логика DND — на стороне вызывающего кода.
/// </summary>
public sealed class GamingService : IDisposable
{
    private const int PollIntervalMs = 2000;
    private const int FullscreenTolerancePx = 2;
    private const int SmCxScreen = 0; // ширина основного монитора, px
    private const int SmCyScreen = 1; // высота основного монитора, px

    /// <summary>Известные процессы-игры (без .exe).</summary>
    private static readonly string[] KnownGames =
    {
        "cs2", "dota2", "hl2", "valorant", "LeagueClient",
        "GTA5", "Rdr2", "EldenRing", "Cyberpunk2077", "rust",
        "DeadByDaylight", "Apex", "Fortnite", "pubg",
        "java", // только при видимом полноэкранном окне
    };

    /// <summary>Системные процессы — полноэкранное окно которых не считаем игрой.</summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "taskmgr",
        "SearchHost",
        "ShellExperienceHost",
        "WindowsShellExperienceHost",
        "TextInputHost",
        "ApplicationFrameHost",
        "SystemSettings",
        "RuntimeBroker",
        "dwm",
        "ctfmon",
        "MusicOverlay",
        // полноэкранные НЕ-игры: браузеры, просмотрщики, плееры, редакторы
        "firefox",
        "chrome",
        "msedge",
        "opera",
        "brave",
        "vivaldi",
        "PaintStudio.View",
        "Microsoft.Photos",
        "Photos",
        "OpenCode",
        "vlc",
        "wmplayer",
        "aimp",
        "SnippingTool",
        "ScreenClippingHost",
    };

    // колбэки EnumWindows держим в полях, чтобы GC не собрал делегат во время перебора
    private readonly EnumWindowsCallback _windowProc;
    private readonly EnumWindowsCallback _probeProc;

    private DispatcherTimer? _timer;
    private Rectangle _bounds;
    private int _foundPid;
    private int _probePid;
    private bool _probeFound;

    public GamingService()
    {
        _windowProc = OnWindow;
        _probeProc = OnProbeWindow;
    }

    public event Action? GameStarted;

    public event Action? GameEnded;

    public bool InGame { get; private set; }

    public string? GameName { get; private set; }

    public DateTime? SessionStart { get; private set; }

    public TimeSpan SessionDuration =>
        InGame && SessionStart.HasValue ? DateTime.Now - SessionStart.Value : TimeSpan.Zero;

    /// <summary>Запускает детект: DispatcherTimer 2000 мс (UI-поток) → Detect().</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _timer.Tick += OnTick;
        _timer.Start();
        Detect();
    }

    private void OnTick(object? sender, EventArgs e) => Detect();

    /// <summary>Определяет текущее состояние и поднимает GameStarted/GameEnded.</summary>
    public void Detect()
    {
        bool inGame = false;
        string? name = null;
        try
        {
            (inGame, name) = DetectKnownGame();
        }
        catch
        {
            // обход процессов не удался — пробуем эвристику
        }

        if (!inGame)
        {
            try
            {
                (inGame, name) = DetectFullscreenWindow();
            }
            catch
            {
                // перебор окон не удался
            }
        }

        ApplyState(inGame, name);
    }

    /// <summary>Смена состояния: true → GameStarted, false → GameEnded.</summary>
    private void ApplyState(bool inGame, string? name)
    {
        if (inGame && !InGame)
        {
            InGame = true;
            GameName = name;
            SessionStart = DateTime.Now;
            GameStarted?.Invoke();
        }
        else if (!inGame && InGame)
        {
            InGame = false;
            GameName = null;
            SessionStart = null;
            GameEnded?.Invoke();
        }
        else if (inGame && !string.IsNullOrWhiteSpace(name) && !string.Equals(GameName, name, StringComparison.Ordinal))
        {
            GameName = name; // игра сменилась, сессия продолжается
        }
    }

    /// <summary>(а) Процессы из списка известных игр с доступным MainModule.</summary>
    private (bool Found, string? Name) DetectKnownGame()
    {
        foreach (string gameProcess in KnownGames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(gameProcess);
            }
            catch
            {
                continue;
            }

            foreach (Process process in processes)
            {
                try
                {
                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // нет доступа к чужому процессу
                    }

                    if (path is null)
                    {
                        continue; // нужен именно файл модуля
                    }

                    // java — только при видимом полноэкранном окне
                    if (string.Equals(gameProcess, "java", StringComparison.OrdinalIgnoreCase) &&
                        !HasFullscreenWindow(process.Id))
                    {
                        continue;
                    }

                    return (true, PrettyProcessName(process, path));
                }
                catch
                {
                    // процесс завершился во время обхода
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return (false, null);
    }

    /// <summary>
    /// (б) Эвристика: видимое окно с границами основного монитора (допуск 2 px),
    /// непустым заголовком, не наше и не из системных процессов.
    /// </summary>
    private (bool Found, string? Name) DetectFullscreenWindow()
    {
        _bounds = PrimaryScreenBounds();
        if (_bounds.IsEmpty)
        {
            return (false, null);
        }

        _foundPid = 0;
        try
        {
            EnumWindows(_windowProc, IntPtr.Zero);
        }
        catch
        {
            return (false, null);
        }

        if (_foundPid == 0)
        {
            return (false, null);
        }

        return (true, ResolveProcessDisplayName(_foundPid));
    }

    private bool OnWindow(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            string title = GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title))
            {
                return true;
            }

            if (!IsFullscreenOnPrimaryMonitor(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pidValue);
            int pid = (int)pidValue;
            if (pid == Environment.ProcessId || IsSystemProcess(pid))
            {
                return true;
            }

            _foundPid = pid;
            return false; // окно найдено — прерываем перебор
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Есть ли у процесса видимое полноэкранное окно (для java).</summary>
    private bool HasFullscreenWindow(int pid)
    {
        _bounds = PrimaryScreenBounds();
        if (_bounds.IsEmpty)
        {
            return false;
        }

        _probePid = pid;
        _probeFound = false;
        try
        {
            EnumWindows(_probeProc, IntPtr.Zero);
        }
        catch
        {
            return false;
        }

        return _probeFound;
    }

    private bool OnProbeWindow(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            if (!IsWindowVisible(hwnd) || !IsFullscreenOnPrimaryMonitor(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint pidValue);
            if ((int)pidValue == _probePid)
            {
                _probeFound = true;
                return false;
            }
        }
        catch
        {
            // пропускаем окно
        }

        return true;
    }

    private bool IsFullscreenOnPrimaryMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT rect))
        {
            return false;
        }

        return Math.Abs(rect.Left - _bounds.Left) <= FullscreenTolerancePx &&
               Math.Abs(rect.Top - _bounds.Top) <= FullscreenTolerancePx &&
               Math.Abs(rect.Right - _bounds.Right) <= FullscreenTolerancePx &&
               Math.Abs(rect.Bottom - _bounds.Bottom) <= FullscreenTolerancePx;
    }

    /// <summary>Границы основного монитора в физических пикселях (0,0 — всегда primary).</summary>
    private static Rectangle PrimaryScreenBounds()
    {
        int width = GetSystemMetrics(SmCxScreen);
        int height = GetSystemMetrics(SmCyScreen);
        return width > 0 && height > 0 ? new Rectangle(0, 0, width, height) : Rectangle.Empty;
    }

    private static bool IsSystemProcess(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return SystemProcesses.Contains(process.ProcessName);
        }
        catch
        {
            return true; // процесс недоступен — не считаем игрой
        }
    }

    private static string ResolveProcessDisplayName(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch
            {
                // нет доступа — берём имя процесса
            }

            return PrettyProcessName(process, path);
        }
        catch
        {
            return "Игра";
        }
    }

    /// <summary>Красивое имя: FileDescription из версии файла, иначе имя процесса.</summary>
    private static string PrettyProcessName(Process process, string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description;
                }
            }
            catch
            {
                // описание недоступно
            }
        }

        return process.ProcessName;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public void Dispose()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }

        GC.SuppressFinalize(this);
    }

    private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // ExactSpelling: в user32 есть только эти имена (без суффиксов A/W)
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EnumWindows(EnumWindowsCallback lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
