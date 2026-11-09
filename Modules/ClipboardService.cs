using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace MusicOverlay.Modules;

/// <summary>Запись истории буфера обмена.</summary>
public sealed record ClipboardEntry(string Text, DateTime Time, bool Pinned);

/// <summary>
/// История буфера обмена: опрос каждые 1500 мс, закреплённые сверху,
/// хранение всей истории в %LOCALAPPDATA%\MusicOverlay\clipboard.json.
/// Таймер и Clipboard работают только на UI-потоке (STA).
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int MaxLength = 1000;
    private const int MaxUnpinned = 50;
    private const int PollMs = 1500;

    private static readonly string StoragePath = BuildStoragePath();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Вся история, новая сверху (закреплённые — в начале).</summary>
    private readonly List<ClipboardEntry> _entries = new();

    /// <summary>Отсортированное представление для UI.</summary>
    private readonly List<ClipboardEntry> _ordered = new();

    private DispatcherTimer? _timer;
    private string? _lastText;
    private bool _started;
    private bool _disposed;

    /// <summary>Снимок списка: каждый доступ возвращает новый список, чтобы WPF перечитывал источник.</summary>
    public IReadOnlyList<ClipboardEntry> Items => new List<ClipboardEntry>(_ordered);

    public event Action? Changed;

    private static string BuildStoragePath()
    {
        try
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MusicOverlay",
                "clipboard.json");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "musicoverlay_clipboard.json");
        }
    }

    /// <summary>Загрузка истории и запуск опроса. Вызывать с UI-потока.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;

        Load();
        Rebuild();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PollMs),
        };
        _timer.Tick += OnTick;
        _timer.Start();

        RaiseChanged();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!Clipboard.ContainsText())
            {
                return;
            }

            string text = Clipboard.GetText();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // уже обработанный текст — не трогаем (иначе Time обновляется каждые 1.5 с)
            if (string.Equals(text, _lastText, StringComparison.Ordinal))
            {
                return;
            }

            _lastText = text;
            Add(text);
        }
        catch
        {
            // буфер занят другим процессом — пропускаем тик
        }
    }

    /// <summary>Копирует текст в буфер и добавляет его в историю.</summary>
    public void Copy(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // не удалось положить в буфер — в историю не добавляем
            return;
        }

        _lastText = text;
        Add(text);
    }

    public void Remove(ClipboardEntry entry)
    {
        if (_disposed || entry is null)
        {
            return;
        }

        try
        {
            if (!_entries.Remove(entry))
            {
                // запись могла быть заменена (обновлён Time) — ищем по тексту
                _entries.RemoveAll(x => string.Equals(x.Text, entry.Text, StringComparison.Ordinal));
            }

            Rebuild();
            Save();
            RaiseChanged();
        }
        catch
        {
            // история не критична
        }
    }

    public void TogglePin(ClipboardEntry entry)
    {
        if (_disposed || entry is null)
        {
            return;
        }

        try
        {
            int index = _entries.FindIndex(x =>
                ReferenceEquals(x, entry) || string.Equals(x.Text, entry.Text, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            var old = _entries[index];
            _entries[index] = new ClipboardEntry(old.Text, old.Time, !old.Pinned);

            Rebuild();
            Save();
            RaiseChanged();
        }
        catch
        {
            // история не критична
        }
    }

    /// <summary>Очистка истории. includePinned=false оставляет закреплённые.</summary>
    public void Clear(bool includePinned)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (includePinned)
            {
                _entries.Clear();
            }
            else
            {
                _entries.RemoveAll(x => !x.Pinned);
            }

            _lastText = null;
            Rebuild();
            Save();
            RaiseChanged();
        }
        catch
        {
            // история не критична
        }
    }

    // ---------------- Внутренняя логика ----------------

    private void Add(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (text.Length > MaxLength)
            {
                text = text[..MaxLength];
            }

            var now = DateTime.Now;
            int existing = _entries.FindIndex(x => string.Equals(x.Text, text, StringComparison.Ordinal));
            if (existing >= 0)
            {
                var old = _entries[existing];
                _entries.RemoveAt(existing);
                _entries.Insert(0, new ClipboardEntry(text, now, old.Pinned));
            }
            else
            {
                _entries.Insert(0, new ClipboardEntry(text, now, false));
            }

            Trim();
            Rebuild();
            Save();
            RaiseChanged();
        }
        catch
        {
            // история не критична
        }
    }

    /// <summary>Ограничиваем число незакреплённых записей (старые — в хвосте).</summary>
    private void Trim()
    {
        int unpinned = _entries.Count(x => !x.Pinned);
        for (int i = _entries.Count - 1; i >= 0 && unpinned > MaxUnpinned; i--)
        {
            if (!_entries[i].Pinned)
            {
                _entries.RemoveAt(i);
                unpinned--;
            }
        }
    }

    /// <summary>Закреплённые сверху, остальные — новыми вверх.</summary>
    private void Rebuild()
    {
        _ordered.Clear();
        foreach (var entry in _entries)
        {
            if (entry.Pinned)
            {
                _ordered.Add(entry);
            }
        }

        foreach (var entry in _entries)
        {
            if (!entry.Pinned)
            {
                _ordered.Add(entry);
            }
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // подписчик упал — не мешаем
        }
    }

    // ---------------- Хранение ----------------

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(StoragePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StoragePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            // запись не удалась — работаем в памяти
        }
    }

    private void Load()
    {
        _entries.Clear();

        try
        {
            if (!File.Exists(StoragePath))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(File.ReadAllText(StoragePath));
            if (loaded is null)
            {
                return;
            }

            foreach (var entry in loaded)
            {
                if (entry is null || string.IsNullOrEmpty(entry.Text))
                {
                    continue;
                }

                string text = entry.Text.Length > MaxLength ? entry.Text[..MaxLength] : entry.Text;
                _entries.Add(new ClipboardEntry(text, entry.Time, entry.Pinned));
            }

            _entries.Sort((a, b) => b.Time.CompareTo(a.Time));
            Trim();
        }
        catch
        {
            // битый файл истории — начинаем с пустой
            _entries.Clear();
        }
    }

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
                _timer.Stop();
                _timer.Tick -= OnTick;
            }
            catch
            {
                // уже остановлен
            }

            _timer = null;
        }
    }
}
