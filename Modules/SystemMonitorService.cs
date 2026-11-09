using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MusicOverlay.Modules;

/// <summary>Снимок системных метрик на момент опроса.</summary>
public sealed record SystemStats(
    double? CpuPercent,
    double? GpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    double NetDownBps,
    double NetUpBps,
    double? DiskPercent,
    double BatteryPercent,
    bool OnBattery,
    TimeSpan Uptime);

/// <summary>
/// Опрос ресурсов системы раз в секунду: CPU (GetSystemTimes), GPU/Disk (PDH),
/// RAM (GlobalMemoryStatusEx), сеть (NetworkInterface), батарея, аптайм.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private const int MaxHistory = 60;
    private const double BytesPerKb = 1024.0;
    private const double BytesPerMb = BytesPerKb * 1024.0;
    private const double BytesPerGb = BytesPerMb * 1024.0;

    private const uint PdhFmtDouble = 0x00000200;

    private readonly List<double> _cpuHistory = new(MaxHistory);

    private System.Threading.Timer? _timer;

    // CPU
    private ulong _prevIdle;
    private ulong _prevTotal;
    private bool _cpuPrimed;

    // сеть
    private long _prevRx;
    private long _prevTx;
    private DateTime _prevNetUtc;
    private bool _netPrimed;

    // PDH: два независимых запроса, чтобы сбой одного счётчика не ломал второй
    private nuint _gpuQuery;
    private nuint _gpuCounter;
    private bool _gpuReady;

    private nuint _diskQuery;
    private nuint _diskCounter;
    private bool _diskReady;

    private bool _started;
    private bool _disposed;

    public SystemStats Current { get; private set; } =
        new(null, null, 0, 0, 0, 0, null, -1, false, TimeSpan.Zero);

    /// <summary>Последние до 60 значений CPU в процентах (новое — последним).</summary>
    public IReadOnlyList<double> CpuHistory
    {
        get
        {
            lock (_cpuHistory)
            {
                return new List<double>(_cpuHistory);
            }
        }
    }

    public event Action? Updated;

    /// <summary>Запуск опроса. Первый замер — сразу, дальше каждую секунду в фоновом потоке.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;

        OpenGpuQuery();
        OpenDiskQuery();

        // фоновый поток: сбор метрик (PDH, счётчики) не тормозит UI даже в игре
        _timer = new System.Threading.Timer(_ => OnTick(null, EventArgs.Empty), null, 1000, 1000);

        // первый замер сразу, чтобы Current не был пустым
        OnTick(null, EventArgs.Empty);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        double? cpu = null;
        double? gpu = null;
        double? disk = null;
        double ramUsed = 0;
        double ramTotal = 0;
        double netDown = 0;
        double netUp = 0;
        double battery = -1;
        bool onBattery = false;
        var uptime = TimeSpan.Zero;

        try
        {
            cpu = SampleCpu();
            gpu = SampleGpu();
            disk = SampleDisk();
            SampleRam(out ramUsed, out ramTotal);
            SampleNet(out netDown, out netUp);
            SampleBattery(out battery, out onBattery);
            uptime = SampleUptime();

            Current = new SystemStats(
                cpu, gpu, ramUsed, ramTotal, netDown, netUp, disk, battery, onBattery, uptime);

            if (cpu.HasValue)
            {
                lock (_cpuHistory)
                {
                    _cpuHistory.Add(cpu.Value);
                    while (_cpuHistory.Count > MaxHistory)
                    {
                        _cpuHistory.RemoveAt(0);
                    }
                }
            }
        }
        catch
        {
            // частичный снимок лучше падения таймера
        }

        try
        {
            Updated?.Invoke();
        }
        catch
        {
            // подписчик упал — не мешаем опросу
        }
    }

    // ---------------- CPU ----------------

    private double? SampleCpu()
    {
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return null;
            }

            ulong i = ToUlong(idle);
            ulong k = ToUlong(kernel);
            ulong u = ToUlong(user);
            ulong total = k + u;

            if (!_cpuPrimed)
            {
                _prevIdle = i;
                _prevTotal = total;
                _cpuPrimed = true;
                return null; // первый замер — только базовая точка
            }

            if (total < _prevTotal || i < _prevIdle)
            {
                // счётчик сброшен — перебазируемся
                _prevIdle = i;
                _prevTotal = total;
                return null;
            }

            ulong dTotal = total - _prevTotal;
            ulong dIdle = i - _prevIdle;
            _prevIdle = i;
            _prevTotal = total;

            if (dTotal == 0)
            {
                return null;
            }

            double usage = (1.0 - (double)dIdle / dTotal) * 100.0;
            return Math.Clamp(usage, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    private static ulong ToUlong(FileTime ft)
    {
        return ((ulong)ft.High << 32) | ft.Low;
    }

    // ---------------- PDH: GPU и диск ----------------

    private void OpenGpuQuery()
    {
        try
        {
            if (PdhOpenQueryW(null, 0, out _gpuQuery) != 0 || _gpuQuery == 0)
            {
                _gpuQuery = 0;
                return;
            }

            if (PdhAddEnglishCounterW(_gpuQuery, @"\GPU Engine(*)\Utilization Percentage", 0, out _gpuCounter) != 0)
            {
                PdhCloseQuery(_gpuQuery);
                _gpuQuery = 0;
                return;
            }

            PdhCollectQueryData(_gpuQuery); // прогрев: первая точка для расчёта скорости
            _gpuReady = true;
        }
        catch
        {
            _gpuReady = false;
            _gpuQuery = 0;
        }
    }

    private void OpenDiskQuery()
    {
        try
        {
            if (PdhOpenQueryW(null, 0, out _diskQuery) != 0 || _diskQuery == 0)
            {
                _diskQuery = 0;
                return;
            }

            if (PdhAddEnglishCounterW(_diskQuery, @"\PhysicalDisk(_Total)\% Disk Time", 0, out _diskCounter) != 0)
            {
                PdhCloseQuery(_diskQuery);
                _diskQuery = 0;
                return;
            }

            PdhCollectQueryData(_diskQuery);
            _diskReady = true;
        }
        catch
        {
            _diskReady = false;
            _diskQuery = 0;
        }
    }

    private double? SampleGpu()
    {
        if (!_gpuReady || _gpuQuery == 0)
        {
            return null;
        }

        try
        {
            if (PdhCollectQueryData(_gpuQuery) != 0)
            {
                return null;
            }

            return ReadMax3d(_gpuCounter);
        }
        catch
        {
            return null;
        }
    }

    private double? SampleDisk()
    {
        if (!_diskReady || _diskQuery == 0)
        {
            return null;
        }

        try
        {
            if (PdhCollectQueryData(_diskQuery) != 0)
            {
                return null;
            }

            if (PdhGetFormattedCounterValue(_diskCounter, PdhFmtDouble, out _, out var value) != 0)
            {
                return null;
            }

            double v = value.DoubleValue;
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return null;
            }

            return Math.Clamp(v, 0, 100);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Максимум по инстансам "engtype_3D" среди адаптеров (не сумма).</summary>
    private static double? ReadMax3d(nuint counter)
    {
        uint size = 0;
        uint count = 0;
        PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
        if (size == 0 || count == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer) != 0)
            {
                return null;
            }

            int itemSize = Marshal.SizeOf<PdhFmtValueItem>();
            double max = 0;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PdhFmtValueItem>(IntPtr.Add(buffer, i * itemSize));
                string name = Marshal.PtrToStringUni(item.SzName) ?? string.Empty;
                if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                double v = item.FmtValue.DoubleValue;
                if (double.IsNaN(v) || double.IsInfinity(v))
                {
                    continue;
                }

                if (!found || v > max)
                {
                    max = v;
                    found = true;
                }
            }

            return found ? Math.Clamp(max, 0, 100) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------- RAM ----------------

    private static void SampleRam(out double usedGb, out double totalGb)
    {
        usedGb = 0;
        totalGb = 0;

        try
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (!GlobalMemoryStatusEx(ref status))
            {
                return;
            }

            totalGb = status.TotalPhys / BytesPerGb;
            double freeGb = status.AvailPhys / BytesPerGb;
            usedGb = Math.Max(0, totalGb - freeGb);
        }
        catch
        {
            // значения останутся нулевыми
        }
    }

    // ---------------- Сеть ----------------

    private void SampleNet(out double downBps, out double upBps)
    {
        downBps = 0;
        upBps = 0;

        try
        {
            long rx = 0;
            long tx = 0;

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                catch
                {
                    // отдельный адаптер может быть недоступен
                }
            }

            var now = DateTime.UtcNow;

            if (!_netPrimed)
            {
                _prevRx = rx;
                _prevTx = tx;
                _prevNetUtc = now;
                _netPrimed = true;
                return;
            }

            double seconds = (now - _prevNetUtc).TotalSeconds;
            long dRx = rx >= _prevRx ? rx - _prevRx : 0;
            long dTx = tx >= _prevTx ? tx - _prevTx : 0;
            _prevRx = rx;
            _prevTx = tx;
            _prevNetUtc = now;

            if (seconds <= 0)
            {
                return;
            }

            downBps = dRx / seconds;
            upBps = dTx / seconds;
        }
        catch
        {
            downBps = 0;
            upBps = 0;
        }
    }

    // ---------------- Батарея и аптайм ----------------

    private static void SampleBattery(out double percent, out bool onBattery)
    {
        percent = -1;
        onBattery = false;

        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                return;
            }

            onBattery = status.ACLineStatus == 0;
            percent = status.BatteryLifePercent == 0 || status.BatteryLifePercent == 255
                ? -1 // нет батареи / значение неизвестно (настольный ПК)
                : status.BatteryLifePercent;
        }
        catch
        {
            percent = -1;
        }
    }

    private static TimeSpan SampleUptime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return DateTime.Now - process.StartTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    // ---------------- Завершение ----------------

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_timer is not null)
        {
            try
            {
                _timer.Dispose();
            }
            catch
            {
                // таймер уже мог быть остановлен
            }

            _timer = null;
        }

        CloseQuery(ref _gpuQuery, ref _gpuReady);
        CloseQuery(ref _diskQuery, ref _diskReady);
    }

    private static void CloseQuery(ref nuint query, ref bool ready)
    {
        ready = false;
        if (query == 0)
        {
            return;
        }

        try
        {
            PdhCloseQuery(query);
        }
        catch
        {
            // закрытие не критично
        }

        query = 0;
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtValue
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFmtValueItem
    {
        public IntPtr SzName;
        public PdhFmtValue FmtValue;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus lpSystemPowerStatus);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, nuint userData, out nuint query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nuint query, string counterPath, nuint userData, out nuint counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(nuint query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterValue(nuint counter, uint format, out uint status, out PdhFmtValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(
        nuint counter, uint format, ref uint bufferLength, ref uint itemCount, IntPtr items);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(nuint query);
}
