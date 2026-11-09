using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Devices.Radios;

namespace MusicOverlay.Modules;

/// <summary>
/// Быстрые действия оболочки: блокировка, диспетчер задач, настройки, скриншот,
/// схема питания, Bluetooth и Wi-Fi. Всё в try/catch, UI-поток не блокировать надолго.
/// </summary>
public static class QuickActions
{
    /// <summary>Схема электропитания Windows.</summary>
    public sealed record PowerScheme(string Id, string Name);

    private const string ExplorerPath = "explorer.exe";
    private const int ProcessTimeoutMs = 3000;
    private const int SmCxScreen = 0; // ширина основного монитора, px
    private const int SmCyScreen = 1; // высота основного монитора, px
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;

    public static IReadOnlyList<PowerScheme> PowerSchemes { get; } = new[]
    {
        new PowerScheme("a1841308-3541-4fab-bc81-f71556f20b4a", "Экономия"),
        new PowerScheme("381b4222-f694-41f0-9685-ff5bb260df2e", "Сбалансирована"),
        new PowerScheme("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", "Высокая производительность"),
    };

    /// <summary>Блокирует рабочую станцию (user32 LockWorkStation).</summary>
    public static void LockWorkstation()
    {
        try
        {
            LockWorkStationNative();
        }
        catch
        {
            // системный вызов недоступен
        }
    }

    public static void OpenTaskManager()
    {
        try
        {
            StartShell("taskmgr.exe", "");
        }
        catch
        {
            // не удалось запустить
        }
    }

    public static void OpenWindowsSettings()
    {
        try
        {
            StartShell("ms-settings:", "");
        }
        catch
        {
            // не удалось запустить
        }
    }

    public static void OpenExplorer()
    {
        try
        {
            StartShell(ExplorerPath, "");
        }
        catch
        {
            // не удалось запустить
        }
    }

    /// <summary>
    /// Полный снимок виртуального рабочего стола → %USERPROFILE%\Pictures\Screenshots\island_HHmmss.png,
    /// копия в буфер обмена. null при ошибке.
    /// </summary>
    public static string? CaptureScreenshot()
    {
        try
        {
            // виртуальный экран охватывает все мониторы, включая расположенные слева/сверху.
            int width = GetSystemMetrics(SmCxVirtualScreen);
            int height = GetSystemMetrics(SmCyVirtualScreen);
            int left = GetSystemMetrics(SmXVirtualScreen);
            int top = GetSystemMetrics(SmYVirtualScreen);
            if (width <= 0 || height <= 0)
            {
                width = GetSystemMetrics(SmCxScreen);
                height = GetSystemMetrics(SmCyScreen);
                left = 0;
                top = 0;
            }
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var bounds = new Rectangle(left, top, width, height);

            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            }

            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Pictures",
                "Screenshots");
            Directory.CreateDirectory(directory);

            string path = Path.Combine(directory, $"island_{DateTime.Now:HHmmss}.png");
            bitmap.Save(path, ImageFormat.Png);

            // буфер обмена не критичен для результата (System.Windows.Clipboard — WPF, STA-поток)
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                System.Windows.Clipboard.SetImage(image);
            }
            }
            catch
            {
                // буфер занят или недоступен
            }

            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Активирует схему питания (powercfg /setactive &lt;id&gt;), true при успехе.</summary>
    public static bool SetPowerScheme(string id)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = $"/setactive {id}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            if (!process.WaitForExit(ProcessTimeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // процесс уже завершился
                }

                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Состояние Bluetooth через Windows.Devices.Radios.
    /// Блокирующая обёртка: async-цепочка выполняется вне UI-потока.
    /// </summary>
    public static (bool Available, bool On) GetBluetooth()
    {
        try
        {
            return Task.Run(GetBluetoothAsync).GetAwaiter().GetResult();
        }
        catch
        {
            return (false, false);
        }
    }

    public static async Task<(bool Available, bool On)> GetBluetoothAsync()
    {
        try
        {
            Radio? radio = await FindRadioAsync();
            if (radio is null)
            {
                return (false, false);
            }

            return (true, radio.State == RadioState.On);
        }
        catch
        {
            return (false, false);
        }
    }

    /// <summary>Включает/выключает Bluetooth. false — доступ запрещён или ошибка.</summary>
    public static async Task<bool> SetBluetoothAsync(bool on)
    {
        try
        {
            Radio? radio = await FindRadioAsync();
            if (radio is null)
            {
                return false;
            }

            RadioAccessStatus status = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            return status == RadioAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Wi-Fi через netsh wlan show interfaces (оба языка вывода).
    /// (false, false, "") при ошибке или отсутствии адаптера.
    /// </summary>
    public static (bool Available, bool Connected, string Ssid) GetWifi()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            startInfo.StandardOutputEncoding = Encoding.UTF8;

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, false, "");
            }

            // читаем асинхронно, чтобы не заблокировать UI-поток при переполнении буфера
            Task<string> readTask = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(ProcessTimeoutMs))
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // процесс уже завершился
                }

                return (false, false, "");
            }

            string output = readTask.GetAwaiter().GetResult();
            return ParseNetshInterfaces(output);
        }
        catch
        {
            return (false, false, "");
        }
    }

    private static (bool Available, bool Connected, string Ssid) ParseNetshInterfaces(string output)
    {
        bool available = false;
        bool connected = false;
        string ssid = "";

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();

            // SSID (не BSSID): "SSID : имя" / "Ssid : имя"
            if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    ssid = line[(colon + 1)..].Trim();
                }
            }
            else if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("Состояние", StringComparison.OrdinalIgnoreCase))
            {
                available = true;
                if (line.Contains("connected", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("подключено", StringComparison.OrdinalIgnoreCase))
                {
                    connected = true;
                }
            }
        }

        return (available, connected, ssid);
    }

    private static async Task<Radio?> FindRadioAsync()
    {
        var radios = await Radio.GetRadiosAsync();
        foreach (Radio radio in radios)
        {
            if (radio.Kind == RadioKind.Bluetooth)
            {
                return radio;
            }
        }

        return null;
    }

    private static void StartShell(string fileName, string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
        });
    }

    [DllImport("user32.dll", EntryPoint = "LockWorkStation", ExactSpelling = true, SetLastError = true)]
    private static extern bool LockWorkStationNative();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
