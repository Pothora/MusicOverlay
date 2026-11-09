using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace MusicOverlay.Services;

public sealed record NotificationInfo(string AppName, string Text);

public sealed record NotificationRecord(string AppName, string Text, DateTime Time, bool Read);

/// <summary>
/// Слушает системные уведомления (тосты) всех приложений через UserNotificationListener,
/// ведёт историю с сохранением на диск.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const int MaxTracked = 400;
    private const int MaxHistory = 60;

    private static readonly Regex Invisible = new("[\u200e\u200f\u2028\u2029\u2060\n\r\t]+", RegexOptions.Compiled);

    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicOverlay", "notifications.json");

    private readonly HashSet<uint> _seen = new();
    private readonly Queue<uint> _order = new();
    private readonly object _gate = new();
    private readonly System.Threading.Timer _pollTimer;

    private UserNotificationListener? _listener;
    private volatile bool _disposed;

    public event Action<NotificationInfo>? NotificationReceived;
    public event Action? HistoryChanged;

    public bool IsAvailable { get; private set; }

    public IReadOnlyList<NotificationRecord> History { get; private set; } = Array.Empty<NotificationRecord>();

    public int UnreadCount
    {
        get
        {
            lock (_gate)
            {
                return History.Count(r => !r.Read);
            }
        }
    }

    public NotificationService()
    {
        // страховка: даже если события не приходят — периодически опрашиваем
        _pollTimer = new System.Threading.Timer(_ => _ = PollAsync(), null, 4000, 4000);
        LoadHistory();
    }

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            IsAvailable = access == UserNotificationListenerAccessStatus.Allowed;

            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "musicoverlay_ui.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} notification-access={access}{Environment.NewLine}");
            }
            catch
            {
                // лог не критичен
            }

            if (!IsAvailable)
            {
                return;
            }

            // существующие уведомления не показываем — только новые
            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_gate)
            {
                foreach (var n in existing)
                {
                    Remember(n.Id);
                }
            }

            _listener.NotificationChanged += OnNotificationChanged;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    private async void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        // форма аргументов зависит от версии SDK — просто перечитываем список,
        // дифф по id сам отсеет удалённые и повторы
        if (_disposed)
        {
            return;
        }

        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_disposed || _listener is null)
        {
            return;
        }

        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var notification in notifications)
            {
                bool isNew;
                lock (_gate)
                {
                    isNew = Remember(notification.Id);
                }

                if (isNew)
                {
                    Raise(notification);
                }
            }
        }
        catch
        {
            // доступ могли отозвать — просто пропускаем
        }
    }

    /// <summary>Возвращает true, если id не встречался раньше.</summary>
    private bool Remember(uint id)
    {
        if (!_seen.Add(id))
        {
            return false;
        }

        _order.Enqueue(id);
        while (_order.Count > MaxTracked)
        {
            _seen.Remove(_order.Dequeue());
        }

        return true;
    }

    private void Raise(UserNotification notification)
    {
        string app = "Приложение";
        try
        {
            string? display = notification.AppInfo?.DisplayInfo?.DisplayName;
            if (!string.IsNullOrWhiteSpace(display))
            {
                app = display;
            }
        }
        catch
        {
            // имя может быть недоступно
        }

        var parts = new List<string>();
        try
        {
            var visual = notification.Notification?.Visual;
            if (visual is not null)
            {
                foreach (var binding in visual.Bindings)
                {
                    foreach (var element in binding.GetTextElements())
                    {
                        string text = Invisible.Replace(element.Text ?? string.Empty, " ").Trim();
                        if (text.Length > 0)
                        {
                            parts.Add(text);
                        }
                    }
                }
            }
        }
        catch
        {
            // содержимое может быть недоступно
        }

        string body = parts.Count > 0 ? string.Join(" — ", parts) : string.Empty;
        if (body.Length > 160)
        {
            body = body[..157] + "...";
        }

        AddToHistory(app, body);

        var info = new NotificationInfo(app, body);
        Dispatch(() => NotificationReceived?.Invoke(info));
    }

    private void AddToHistory(string app, string text)
    {
        lock (_gate)
        {
            var updated = new List<NotificationRecord>(History)
            {
                new(app, text, DateTime.Now, false),
            };

            if (updated.Count > MaxHistory)
            {
                updated = updated.Skip(updated.Count - MaxHistory).ToList();
            }

            History = updated;
        }

        SaveHistory();
        Dispatch(() => HistoryChanged?.Invoke());
    }

    public void MarkAllRead()
    {
        lock (_gate)
        {
            History = History.Select(r => r with { Read = true }).ToList();
        }

        SaveHistory();
        HistoryChanged?.Invoke();
    }

    public void Remove(NotificationRecord record)
    {
        lock (_gate)
        {
            History = History.Where(r => r != record).ToList();
        }

        SaveHistory();
        HistoryChanged?.Invoke();
    }

    public void ClearHistory()
    {
        lock (_gate)
        {
            History = Array.Empty<NotificationRecord>();
        }

        SaveHistory();
        HistoryChanged?.Invoke();
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<NotificationRecord>>(File.ReadAllText(HistoryPath));
            if (loaded is not null)
            {
                lock (_gate)
                {
                    History = loaded;
                }
            }
        }
        catch
        {
            // история не критична
        }
    }

    private void SaveHistory()
    {
        try
        {
            List<NotificationRecord> snapshot;
            lock (_gate)
            {
                snapshot = History.ToList();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // история не критична
        }
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer.Dispose();

        if (_listener is not null)
        {
            _listener.NotificationChanged -= OnNotificationChanged;
            _listener = null;
        }
    }
}
