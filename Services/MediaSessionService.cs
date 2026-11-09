using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage;
using Windows.Storage.Streams;

namespace MusicOverlay.Services;

public sealed record TrackRecord(string Title, string Artist, DateTime Time);

/// <summary>
/// Чтение текущей медиа-сессии Windows (Spotify, браузеры, VLC и т.д.)
/// и отправка команд Play/Pause/Next/Previous.
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicOverlay", "tracks.json");

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private volatile bool _disposed;

    /// <summary>Вызывается на UI-потоке при любой смене состояния.</summary>
    public event Action? SessionChanged;

    /// <summary>Вызывается при добавлении трека в историю.</summary>
    public event Action? HistoryChanged;

    public IReadOnlyList<TrackRecord> RecentTracks { get; private set; } = Array.Empty<TrackRecord>();

    public MediaSessionService()
    {
        try
        {
            if (File.Exists(HistoryPath))
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<List<TrackRecord>>(File.ReadAllText(HistoryPath));
                if (loaded is not null)
                {
                    RecentTracks = loaded;
                }
            }
        }
        catch
        {
            // история не критична
        }
    }

    public string Title { get; private set; } = string.Empty;
    public string Artist { get; private set; } = string.Empty;
    public string Album { get; private set; } = string.Empty;
    public string SourceApp { get; private set; } = string.Empty;
    public bool HasSession => _session is not null && !_disposed;
    public bool IsPlaying { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public BitmapImage? Cover { get; private set; }

    public string ArtistLine
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Artist))
            {
                return Artist;
            }

            return !string.IsNullOrWhiteSpace(Album) ? Album : string.Empty;
        }
    }

    public async Task InitializeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Attach(_manager.GetCurrentSession());
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        // в аргументах события нет публичного Session — перечитываем у менеджера
        OnUi(() => Attach(sender.GetCurrentSession()));
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_disposed)
        {
            return;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _session = session;
        _lastDuration = TimeSpan.Zero;
        _lastPosition = TimeSpan.Zero;

        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = RefreshAsync();
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        OnUi(() => _ = RefreshAsync());
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        OnUi(() =>
        {
            UpdatePlayback();
            RaiseChanged();
        });
    }

    private void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        OnUi(() =>
        {
            UpdateTimeline();
            RaiseChanged();
        });
    }

    private async Task RefreshAsync()
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (_disposed)
        {
            return;
        }

        if (session is null)
        {
            Title = string.Empty;
            Artist = string.Empty;
            Album = string.Empty;
            SourceApp = string.Empty;
            Cover = null;
            IsPlaying = false;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            RaiseChanged();
            return;
        }

        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            Title = properties.Title;
            Artist = properties.Artist;
            Album = properties.AlbumTitle;
            SourceApp = ShortAppName(session.SourceAppUserModelId);
            Cover = await LoadCoverAsync(properties.Thumbnail);
            AddTrackIfChanged();
        }
        catch
        {
            // сессия могла закрыться во время запроса
        }

        if (_disposed)
        {
            return;
        }

        UpdatePlayback();
        UpdateTimeline();
        RaiseChanged();
    }

    /// <summary>Обновляет позицию/длительность/статус без перечитывания метаданных.</summary>
    public void RefreshTimeline()
    {
        if (_session is null || _disposed)
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            IsPlaying = false;
            return;
        }

        UpdatePlayback();
        UpdateTimeline();
    }

    private void UpdatePlayback()
    {
        var info = _session?.GetPlaybackInfo();
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    private TimeSpan _lastDuration;
    private TimeSpan _lastPosition;

    private void UpdateTimeline()
    {
        if (_session is null)
        {
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            _lastDuration = TimeSpan.Zero;
            _lastPosition = TimeSpan.Zero;
            return;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();

            TimeSpan? rawEnd = timeline.EndTime is { } end && end > TimeSpan.Zero
                ? end
                : null;
            TimeSpan rawPos = timeline.Position < TimeSpan.Zero ? TimeSpan.Zero : timeline.Position;

            // смена трека: длительность резко изменилась — сбрасываем кэш позиции
            if (rawEnd is { } newEnd &&
                _lastDuration > TimeSpan.Zero &&
                Math.Abs((newEnd - _lastDuration).TotalSeconds) > 1.5)
            {
                _lastPosition = TimeSpan.Zero;
            }

            // ДЛИТЕЛЬНОСТЬ: многие плееры на паузе отдают пустую EndTime —
            // держим последнюю известную, чтобы время не пропадало
            if (rawEnd is { } duration)
            {
                Duration = duration;
                _lastDuration = duration;
            }
            else
            {
                Duration = _lastDuration;
            }

            // ПОЗИЦИЯ: на паузе некоторые источники отдают ноль — берём последнюю известную
            if (rawPos > TimeSpan.Zero)
            {
                Position = rawPos;
                _lastPosition = rawPos;
            }
            else if (_lastPosition > TimeSpan.Zero && !IsPlayingNow())
            {
                Position = _lastPosition;
            }
            else
            {
                Position = rawPos;
                _lastPosition = rawPos;
            }
        }
        catch
        {
            // сессия могла закрыться
        }
    }

    private bool IsPlayingNow()
    {
        try
        {
            return _session?.GetPlaybackInfo()?.PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return true;
        }
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session is null || _disposed)
        {
            return;
        }

        try
        {
            await session.TrySkipNextAsync();
            await RefreshAsync();
        }
        catch
        {
            // плеер может не поддерживать команду
        }
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session is null || _disposed)
        {
            return;
        }

        try
        {
            await session.TrySkipPreviousAsync();
            await RefreshAsync();
        }
        catch
        {
            // плеер может не поддерживать команду
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = _session;
        if (session is null || _disposed)
        {
            return;
        }

        try
        {
            await session.TryTogglePlayPauseAsync();
            UpdatePlayback();
            RaiseChanged();
        }
        catch
        {
            // плеер может не поддерживать команду
        }
    }

    /// <summary>Перемотка к позиции (по клику на слайдер).</summary>
    public async Task SeekAsync(TimeSpan position)
    {
        var session = _session;
        if (session is null || _disposed || position < TimeSpan.Zero)
        {
            return;
        }

        try
        {
            await session.TryChangePlaybackPositionAsync(position.Ticks);
            UpdateTimeline();
            RaiseChanged();
        }
        catch
        {
            // плеер может не поддерживать перемотку
        }
    }

    private static async Task<BitmapImage?> LoadCoverAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            return null;
        }

        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0)
            {
                return null;
            }

            using var reader = new DataReader(stream);
            uint length = (uint)Math.Min(stream.Size, 512 * 1024);
            await reader.LoadAsync(length);

            var bytes = new byte[length];
            reader.ReadBytes(bytes);

            using var memory = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 128;
            image.StreamSource = memory;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static string ShortAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return string.Empty;
        }

        int bang = appId.IndexOf('!');
        if (bang > 0)
        {
            appId = appId[..bang];
        }

        int underscore = appId.IndexOf('_');
        if (underscore > 0)
        {
            appId = appId[..underscore];
        }

        return appId;
    }

    // ---------- история треков ----------

    private void AddTrackIfChanged()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            return;
        }

        if (RecentTracks.Count > 0 &&
            string.Equals(RecentTracks[0].Title, Title, StringComparison.Ordinal) &&
            string.Equals(RecentTracks[0].Artist, Artist, StringComparison.Ordinal))
        {
            return;
        }

        var updated = new List<TrackRecord>(RecentTracks)
        {
            new(Title, Artist, DateTime.Now),
        };

        if (updated.Count > 20)
        {
            updated = updated.Skip(20).ToList();
        }

        RecentTracks = updated;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(RecentTracks));
        }
        catch
        {
            // история не критична
        }

        HistoryChanged?.Invoke();
    }

    /// <summary>Короткие имена активных источников музыки (без переключения — Windows его не даёт).</summary>
    public IReadOnlyList<string> GetActiveSources()
    {
        try
        {
            if (_manager is null)
            {
                return Array.Empty<string>();
            }

            return _manager.GetSessions()
                .Select(s => ShortAppName(s.SourceAppUserModelId))
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void OnUi(Action action)
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

    private void RaiseChanged()
    {
        if (_disposed)
        {
            return;
        }

        SessionChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            _session = null;
        }

        SessionChanged = null;
    }
}
