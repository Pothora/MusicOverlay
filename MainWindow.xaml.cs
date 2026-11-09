using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using PathShape = System.Windows.Shapes.Path;
using System.Windows.Threading;
using MusicOverlay.Core;
using MusicOverlay.Modules;
using MusicOverlay.Services;

namespace MusicOverlay;

public partial class MainWindow : Window
{
    private enum View
    {
        Mini,
        Expanded,
        Settings,
    }

    private const int GwlExstyle = -20;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolwindow = 0x00000080;
    private const int WsExNoactivate = 0x08000000;

    private const int DotCount = 5;
    private const int SmallBarCount = 4;

    private const double MiniWidth = 350;
    private const double WideWidth = 480;

    private static readonly CultureInfo Ru = new("ru-RU");

    private static readonly Brush ChipBrush = CreateFrozen(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipMoreBrush = CreateFrozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush ChipSubBrush = CreateFrozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));

    private readonly MediaSessionService _media = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _typeTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer _bannerTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _backdropTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly float[] _levels = new float[AudioVisualizer.BandCount];

    private readonly ScaleTransform[] _dotScales = new ScaleTransform[DotCount];
    private readonly ScaleTransform[] _smallBarScales = new ScaleTransform[SmallBarCount];
    private readonly double[] _dotValues = new double[DotCount];
    private readonly double[] _smallBarValues = new double[SmallBarCount];
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    // сервисы модулей
    private NotificationService? _notifications;
    private SystemMonitorService? _system;
    private AudioService? _audio;
    private ClipboardService? _clipboard;
    private WeatherService? _weather;
    private GamingService? _gaming;
    private LauncherService? _launcher;
    private ProcessMonitorService? _appsMonitor;
    private AudioVisualizer? _visualizer;
    private IReadOnlyList<RunningApp> _apps = Array.Empty<RunningApp>();
    private bool _appsExpanded;

    // вкладки
    private readonly List<(string Key, string Label)> _tabs = new()
    {
        ("music", "Музыка"),
        ("system", "Система"),
        ("notify", "Уведомления"),
        ("audio", "Звук"),
        ("clipboard", "Буфер"),
        ("actions", "Действия"),
        ("weather", "Погода"),
        ("gaming", "Игры"),
        ("launcher", "Лаунчер"),
    };
    private readonly Dictionary<string, Button> _tabButtons = new();
    private readonly Dictionary<string, Grid> _tabPanels = new();
    private readonly List<Button> _actionTiles = new();

    // печатная машинка
    private string _titleTarget = "Загрузка...";
    private int _shownCount;
    private int _typePhase;
    private int _typeTick;
    private string _lastAnimatedTitle = string.Empty;

    private View _view = View.Mini;
    private View _lastView = View.Mini;
    private View _appliedView = (View)(-1); // для анимации только при смене состояния

    // синхронизация UI (программные изменения значений)
    private bool _syncingUi;
    private bool _audioDragging;
    private bool _settingsBuilt;
    private bool _tabsBuilt;
    private bool _actionsBuilt;
    private bool _dndBeforeGame;
    private bool _isDragging;
    private bool _dragMoved;
    private bool _dragStartedOnButton;
    private Point _dragScreenStart;
    private double _dragWindowLeftStart;
    private double _dragWindowTopStart;
    private bool _suppressNextClick;

    // активное окно (показывается, когда музыка не играет)
    // тестовый хук: MUSICOVERLAY_TESTAPP=1 — всегда показывать активное приложение
    private static readonly bool ForceAppHeader =
        Environment.GetEnvironmentVariable("MUSICOVERLAY_TESTAPP") == "1";

    private bool _musicActive;
    private int _activePid;
    private string _activeName = string.Empty;
    private string _activeTitle = string.Empty;
    private string? _activePath;
    private int _appCoverPid = -1;
    private readonly Dictionary<string, string> _friendlyNameCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Windows.UI.Core.CoreWindow",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
    };

    // таймер / секундомер
    private DateTime _timerEnd = DateTime.Now.AddMinutes(5);
    private int _timerLeftSeconds = 300;
    private bool _timerRunning;
    private bool _stopwatchRunning;
    private TimeSpan _stopwatchElapsed;
    private DateTime _stopwatchStart;

    // горячая клавиша
    private const int HotkeyId = 0x1501;
    private HwndSource? _hwndSource;

    public MainWindow()
    {
        InitializeComponent();

        SettingsStore.Load();
        _view = SettingsStore.Current.View == "full" ? View.Expanded : View.Mini;
        _lastView = _view;

        ApplyView();

        // всегда закреплён по центру экрана сверху
        Rect workArea = SystemParameters.WorkArea;
        bool hasSavedPosition = double.IsFinite(SettingsStore.Current.WindowLeft) &&
                                double.IsFinite(SettingsStore.Current.WindowTop) &&
                                SettingsStore.Current.WindowLeft >= 0 && SettingsStore.Current.WindowTop >= 0;
        if (hasSavedPosition)
        {
            Left = SettingsStore.Current.WindowLeft;
            Top = SettingsStore.Current.WindowTop;
        }

        if (!hasSavedPosition)
        {
            Left = Math.Round(workArea.Left + (workArea.Width - Width) / 2);
            Top = Math.Round(workArea.Top + 8);
        }

        _clock.Tick += OnClockTick;
        _render.Tick += OnRenderTick;
        _typeTimer.Tick += OnTypeTick;
        _bannerTimer.Tick += (_, _) => HideBanner();
        _backdropTimer.Tick += (_, _) => UpdateBackdrop();
        _media.SessionChanged += OnSessionChanged;
        _media.HistoryChanged += () => Dispatcher.BeginInvoke(new Action(RenderTrackHistory));
    }

    // ---------- окно: стили, горячая клавиша ----------

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // оверлей поверх всех окон, без панели задач и без перехвата фокуса
        int style = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, style | WsExTopmost | WsExToolwindow | WsExNoactivate);
        try
        {
            RegisterHotKey(hwnd, HotkeyId, 0x0002 | 0x0001, 0x4D); // Ctrl+Alt+M
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WndProc);
        }
        catch
        {
            // горячая клавиша не критична
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WmHotkey = 0x0312;
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            ToggleExpand();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ToggleExpand()
    {
        if (_view == View.Settings)
        {
            _view = _lastView;
        }
        else
        {
            _view = _view == View.Mini ? View.Expanded : View.Mini;
        }

        ApplyView();
        if (_view != View.Settings)
        {
            RefreshTitle();
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SettingsStore.Current.View = _lastView == View.Expanded || _view == View.Expanded ? "full" : "mini";
        SettingsStore.Save();

        _clock.Stop();
        _render.Stop();
        _typeTimer.Stop();
        _bannerTimer.Stop();
        _backdropTimer.Stop();
        SettingsStore.Current.WindowLeft = Left;
        SettingsStore.Current.WindowTop = Top;
        SettingsStore.Save();

        _media.SessionChanged -= OnSessionChanged;
        _media.Dispose();
        _notifications?.Dispose();
        _appsMonitor?.Dispose();
        _system?.Dispose();
        _audio?.Dispose();
        _clipboard?.Dispose();
        _weather?.Dispose();
        _gaming?.Dispose();
        _visualizer?.Dispose();
        _visualizer = null;

        try
        {
            if (_hwndSource is not null)
            {
                UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
                _hwndSource.RemoveHook(WndProc);
            }
        }
        catch
        {
            // не критично
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // плавное появление окна
        Opacity = 0;
        BeginAnimation(
            Window.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });

        TabStrip.PreviewMouseWheel += (_, e) =>
        {
            // ручная прокрутка ленты вкладок колесом
            TabStrip.ScrollToHorizontalOffset(TabStrip.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        };

        // пузырьки на ЛЮБОМ клике по пилюле (включая кнопки)
        Root.AddHandler(
            UIElement.MouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnAnyMouseDown),
            handledEventsToo: true);

        CreateIndicators();
        BuildTabs();
        BuildSettings();
        BuildActions();

        ApplyAppearance();
        ApplyAutostart(SettingsStore.Current.Autostart);

        _clock.Start();
        _render.Start();
        _typeTimer.Start();
        _backdropTimer.Start();

        // ---------- сервисы модулей ----------
        try { if (SettingsStore.Current.Notifications) SetNotificationModule(true); }
        catch { /* центр уведомлений недоступен */ }
        try { if (SettingsStore.Current.System) SetSystemModule(true); }
        catch { /* системный монитор недоступен */ }
        try { if (SettingsStore.Current.Audio) SetAudioModule(true); }
        catch { /* аудио недоступно */ }
        try { if (SettingsStore.Current.Clipboard) SetClipboardModule(true); }
        catch { /* буфер недоступен */ }
        try { if (SettingsStore.Current.Weather) SetWeatherModule(true); }
        catch { /* погода недоступна */ }
        try { if (SettingsStore.Current.Gaming) SetGamingModule(true); }
        catch { /* игровой режим недоступен */ }
        try { if (SettingsStore.Current.Launcher) SetLauncherModule(true); }
        catch { /* лаунчер недоступен */ }
        try { if (SettingsStore.Current.RunningApps) SetAppsModule(true); }
        catch { /* список процессов недоступен */ }

        try
        {
            await _media.InitializeAsync();
        }
        catch
        {
            // медиа-сессии недоступны
        }

        if (SettingsStore.Current.Viz)
        {
            try { SetVisualizerModule(true); }
            catch { /* эквалайзер недоступен */ }
        }

        if (_view == View.Expanded)
        {
            SelectTab(ActiveTab);
        }

        SyncDndUi();
        UpdateTabBadges();
        RenderTrackHistory();

        if (ForceAppHeader)
        {
            UpdateActiveApp(force: true);
            ApplyAppHeader();
        }
    }

    // ---------- режимы ----------

    private string ActiveTab =>
        string.IsNullOrWhiteSpace(SettingsStore.Current.Tab) ? "music" : SettingsStore.Current.Tab;

    private void ApplyView()
    {
        var store = SettingsStore.Current;
        bool mini = _view == View.Mini;
        bool expanded = _view == View.Expanded;
        bool settings = _view == View.Settings;
        bool viewChanged = _view != _appliedView;
        _appliedView = _view;

        bool canAnchor = !double.IsNaN(Left) && !double.IsNaN(Top);
        double centerX = canAnchor ? Left + Width / 2 : 0;

        // высота всегда по содержимому — нет пустых мест под короткими панелями
        Width = mini ? MiniWidth : WideWidth;
        SizeToContent = SizeToContent.Height;

        if (canAnchor)
        {
            Left = Math.Round(centerX - Width / 2);
        }

        Visibility visible = Visibility.Visible;
        Visibility hidden = Visibility.Collapsed;

        ContentGrid.Margin = mini ? new Thickness(9, 9, 8, 9) : new Thickness(14, 12, 14, 12);

        double coverSize = mini ? 42 : 56;
        double coverRadius = coverSize / 2;
        CoverBorder.Width = coverSize;
        CoverBorder.Height = coverSize;
        CoverBorder.CornerRadius = new CornerRadius(coverRadius);
        CoverBorder.Clip = new RectangleGeometry(new Rect(0, 0, coverSize, coverSize), coverRadius, coverRadius);
        CoverBorder.Visibility = settings ? hidden : visible;

        ArtistText.Visibility = (mini || settings) ? hidden : visible;

        TabStrip.Visibility = expanded ? visible : hidden;
        PanelsHost.Visibility = expanded ? visible : hidden;
        AppsRow.Visibility = (expanded && store.RunningApps) ? visible : hidden;
        SettingsScroll.Visibility = settings ? visible : hidden;

        DotsPanel.Visibility = (mini && store.Viz) ? visible : hidden;
        SmallBarsPanel.Visibility = (expanded && store.Viz) ? visible : hidden;

        ModeButton.Visibility = settings ? hidden : visible;
        ExpandGlyph.Visibility = mini ? visible : hidden;
        CollapseGlyph.Visibility = mini ? hidden : visible;
        ModeButton.ToolTip = mini ? "Развернуть" : "Свернуть";
        GearButton.ToolTip = settings ? "Закрыть настройки" : "Настройки";

        TitleText.FontSize = mini ? 15 : 16;

        if (expanded)
        {
            SelectTab(ActiveTab);
        }

        ApplyAppearance();
        if (viewChanged)
        {
            AnimateEntrance(ContentGrid);
        }

        UiLog($"ApplyView view={_view} w={Width} left={Left}");
    }

    private void ApplyAppearance()
    {
        var store = SettingsStore.Current;
        bool mini = _view == View.Mini;

        double radius = mini ? 30 : store.Radius;
        Root.CornerRadius = new CornerRadius(radius);
        Banner.CornerRadius = new CornerRadius(radius);

        byte alpha = (byte)Math.Round(Math.Clamp(store.Opacity, 0.35, 1.0) * 255);

        // при включённом размытии фон становится полупрозрачным — сквозь него видно стекло
        if (store.Blur)
        {
            alpha = (byte)Math.Round(alpha * 0.72);
        }

        Color surface = TryParseColor(store.BackgroundColor, out Color configuredSurface)
            ? configuredSurface
            : Color.FromRgb(0x11, 0x11, 0x15);
        // Мягкий градиент строится от выбранного пользователем цвета поверхности.
        var background = new LinearGradientBrush(
            Color.FromArgb(alpha, (byte)Math.Min(255, surface.R + 9), (byte)Math.Min(255, surface.G + 9), (byte)Math.Min(255, surface.B + 9)),
            Color.FromArgb(alpha, surface.R, surface.G, surface.B),
            new Point(0.5, 0),
            new Point(0.5, 1));
        background.Freeze();
        TintBorder.CornerRadius = new CornerRadius(radius);
        TintBorder.Background = background;
        Root.Background = background;

        if (TryParseColor(store.TextColor, out Color textColor))
        {
            var textBrush = CreateFrozen(textColor);
            TitleText.Foreground = textBrush;
            ArtistText.Foreground = textBrush;
        }

        if (TryParseColor(store.Accent, out Color accent))
        {
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            Resources["AccentBrush"] = accentBrush;
        }

        ApplyTextAppearance();

        UpdateBackdrop();
    }

    private void ApplyTextAppearance()
    {
        var store = SettingsStore.Current;
        var family = new FontFamily("Segoe UI");

        Brush textBrush = TryParseColor(store.TextColor, out Color color) ? CreateFrozen(color) : Brushes.White;
        ApplyTextStyle(ContentGrid, family, textBrush);
        // Значки и служебные надписи сохраняют свой исходный цвет.
        TitleText.Foreground = textBrush;
        ArtistText.Foreground = textBrush == Brushes.White ? CreateFrozen(Color.FromRgb(0x9A, 0x9A, 0x9E)) : textBrush;
    }

    private static void ApplyTextStyle(DependencyObject node, FontFamily family, Brush brush)
    {
        if (node is TextBlock text)
        {
            text.FontFamily = family;
            string foreground = text.Foreground.ToString();
            if (foreground.Equals("#FFFFFFFF", StringComparison.OrdinalIgnoreCase))
            {
                text.Foreground = brush;
            }
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            ApplyTextStyle(VisualTreeHelper.GetChild(node, i), family, brush);
        }
    }

    /// <summary>
    /// Снимает полосу экрана под виджетом и делает её размытой подложкой —
    /// сила размытия регулируется, а «водяное» покачивание добавляет жизнь фону.
    /// </summary>
    private void UpdateBackdrop()
    {
        if (!SettingsStore.Current.Blur)
        {
            BackdropHost.Visibility = Visibility.Collapsed;
            return;
        }

        if (_gaming is { InGame: true })
        {
            return; // во время игры не дёргаем захват экрана — меньше лагов
        }

        if (ActualWidth < 20 || ActualHeight < 20 ||
            double.IsNaN(Left) || double.IsNaN(Top))
        {
            return;
        }

        // фиксируем размер картинки = контент + запас на покачивание.
        // Без этого её натуральный размер раздувает SizeToContent до полноэкранного.
        if (ContentGrid.ActualWidth > 10 && ContentGrid.ActualHeight > 10)
        {
            BackdropImage.Width = ContentGrid.ActualWidth + 24;
            BackdropImage.Height = ContentGrid.ActualHeight + 24;
        }

        try
        {
            double scale = 1.0;
            try
            {
                var source = PresentationSource.FromVisual(this);
                scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            }
            catch
            {
                // масштаб = 1
            }

            int w = (int)Math.Ceiling(ActualWidth * scale);
            int h = (int)Math.Ceiling(ActualHeight * scale);
            int x = (int)Math.Round(Left * scale);
            int windowBottom = (int)Math.Round((Top + ActualHeight) * scale);
            int screenHeight = GetSystemMetrics(SmCyScreen);

            // снимаем полосу экрана ПОД виджетом — её мы не перекрываем,
            // поэтому нет петли «снимок самого себя»
            bool below = windowBottom + h <= screenHeight;
            int y = below ? windowBottom : Math.Max(0, (int)Math.Round(Top * scale) - h);
            if (!below && y <= 0)
            {
                return; // свободной полосы нет — пропускаем
            }

            using var bitmap = new System.Drawing.Bitmap(w, h);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h));
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            BackdropImage.Source = image;
            BackdropHost.Visibility = Visibility.Visible;
            ApplyBackdropClip();
        }
        catch
        {
            // снимок не удался — оставляем прежнюю подложку
        }
    }

    /// <summary>Скругляет подложку по форме текущего вида.</summary>
    private void ApplyBackdropClip()
    {
        if (BackdropHost.ActualWidth < 5)
        {
            return;
        }

        double corner = Root.CornerRadius.TopLeft;
        BackdropHost.CornerRadius = new CornerRadius(corner);
        BackdropHost.Clip = new RectangleGeometry(
            new Rect(0, 0, BackdropHost.ActualWidth, BackdropHost.ActualHeight),
            corner,
            corner);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // подложка и клип пересчитываются после завершения компоновки
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                ApplyBackdropClip();
                UpdateBackdrop();
            }),
            DispatcherPriority.Background);
    }

    private static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Transparent;
        try
        {
            var brush = new SolidColorBrush();
            brush.Color = (Color)ColorConverter.ConvertFromString(hex);
            color = brush.Color;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (_view == View.Mini)
        {
            _view = View.Expanded;
        }
        else if (_view == View.Expanded)
        {
            _view = View.Mini;
        }

        ApplyView();
        RefreshTitle();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_view == View.Settings)
        {
            _view = _lastView;
            ApplyView();
            RefreshTitle();
        }
        else
        {
            _lastView = _view;
            _view = View.Settings;
            SyncSettingsUi();
            ApplyView();
            TitleText.Text = "Настройки";

            // ступенчатое появление строк настроек
            int rowIndex = 0;
            foreach (UIElement child in SettingsPanelContent.Children)
            {
                child.Opacity = 0;
                var stagger = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
                {
                    BeginTime = TimeSpan.FromMilliseconds(rowIndex * 30),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                child.BeginAnimation(UIElement.OpacityProperty, stagger);
                rowIndex++;
            }
        }
    }

    private static void UiLog(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "musicoverlay_ui.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // лог не критичен
        }
    }

    // ---------- плавная прокрутка вкладок ----------

    private DispatcherTimer? _scrollTween;
    private double _scrollFrom;
    private double _scrollTo;
    private DateTime _scrollStart = DateTime.Now;

    /// <summary>Плавно центрирует активный чип в ленте вкладок.</summary>
    private void ScrollTabSmooth(int chipIndex)
    {
        if (chipIndex < 0 || chipIndex >= TabStripPanel.Children.Count)
        {
            return;
        }

        TabStrip.UpdateLayout();

        double offset = 0;
        for (int i = 0; i < chipIndex; i++)
        {
            var previous = (FrameworkElement)TabStripPanel.Children[i];
            offset += previous.ActualWidth + previous.Margin.Left + previous.Margin.Right;
        }

        var chip = (FrameworkElement)TabStripPanel.Children[chipIndex];
        double target = offset + chip.ActualWidth / 2 - TabStrip.ViewportWidth / 2;
        target = Math.Clamp(target, 0, Math.Max(0, TabStrip.ScrollableWidth));

        if (Math.Abs(TabStrip.HorizontalOffset - target) < 1)
        {
            TabStrip.ScrollToHorizontalOffset(target);
            return;
        }

        _scrollFrom = TabStrip.HorizontalOffset;
        _scrollTo = target;
        _scrollStart = DateTime.Now;

        if (_scrollTween is null)
        {
            _scrollTween = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _scrollTween.Tick += OnScrollTick;
        }

        _scrollTween.Start();
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        double t = Math.Min(1, (DateTime.Now - _scrollStart).TotalMilliseconds / 280);
        double eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        TabStrip.ScrollToHorizontalOffset(_scrollFrom + (_scrollTo - _scrollFrom) * eased);
        if (t >= 1)
        {
            _scrollTween?.Stop();
        }
    }

    // ---------- анимации интерфейса ----------

    /// <summary>Появление элемента: проявление + лёгкий подъём снизу.</summary>
    private static void AnimateEntrance(UIElement element, double fromY = 8, int durationMs = 220)
    {
        element.Opacity = 0;
        var transform = new TranslateTransform(0, fromY);
        element.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease });
        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(durationMs)) { EasingFunction = ease });
    }

    // ---------- вкладки ----------

    private void BuildTabs()
    {
        if (_tabsBuilt)
        {
            return;
        }

        _tabsBuilt = true;
        var store = SettingsStore.Current;

        foreach (var (key, label) in _tabs)
        {
            if (key == "system" && !store.System) continue;
            if (key == "notify" && !store.Notifications) continue;
            if (key == "audio" && !store.Audio) continue;
            if (key == "clipboard" && !store.Clipboard) continue;
            if (key == "actions" && !store.Actions) continue;
            if (key == "weather" && !store.Weather) continue;
            if (key == "gaming" && !store.Gaming) continue;
            if (key == "launcher" && !store.Launcher) continue;

            var chip = new Button
            {
                Style = (Style)FindResource("TabChip"),
                Content = label,
                Tag = key,
            };
            chip.Click += (_, _) => SelectTab(key);

            _tabButtons[key] = chip;
            TabStripPanel.Children.Add(chip);
        }

        _tabPanels["music"] = PanelMusic;
        _tabPanels["system"] = PanelSystem;
        _tabPanels["notify"] = PanelNotify;
        _tabPanels["audio"] = PanelAudio;
        _tabPanels["clipboard"] = PanelClipboard;
        _tabPanels["actions"] = PanelActions;
        _tabPanels["weather"] = PanelWeather;
        _tabPanels["gaming"] = PanelGaming;
        _tabPanels["launcher"] = PanelLauncher;
    }

    private void SelectTab(string key)
    {
        SettingsStore.Current.Tab = key;
        SettingsStore.Save();

        bool activeExists = _tabPanels.ContainsKey(key);
        if (!activeExists)
        {
            key = "music";
        }

        foreach (var (panelKey, panel) in _tabPanels)
        {
            bool on = panelKey == key;
            panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            // элементы внутри невидимых панелей скрываем, чтобы не занимали место в SizeToContent
            foreach (UIElement child in panel.Children)
            {
                child.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // анимация появления панели при смене вкладки
        AnimateEntrance(PanelsHost, 10, 200);

        bool accent = TryParseColor(SettingsStore.Current.Accent, out Color accentColor);
        int chipIndex = 0;
        int activeIndex = 0;
        foreach (var (chipKey, chip) in _tabButtons)
        {
            if (chipKey == key)
            {
                var active = new SolidColorBrush(accentColor) { Opacity = 0.30 };
                chip.Background = active;
                chip.Foreground = Brushes.White;
                activeIndex = chipIndex;
            }
            else
            {
                chip.Background = CreateFrozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
                chip.Foreground = CreateFrozen(Color.FromArgb(0xB9, 0xB9, 0xB9, 0xBE));
            }

            chipIndex++;
        }

        try
        {
            ScrollTabSmooth(activeIndex);
        }
        catch
        {
            // прокрутка не критична
        }

        // обновление содержимого при входе
        switch (key)
        {
            case "music":
                RenderTrackHistory();
                UpdateSourcesText();
                break;
            case "system":
                RenderSystem();
                break;
            case "notify":
                RenderNotifications();
                _notifications?.MarkAllRead();
                UpdateTabBadges();
                break;
            case "audio":
                RefreshAudioUi();
                RenderSessions();
                break;
            case "clipboard":
                RenderClipboard();
                break;
            case "actions":
                RefreshActionStates();
                break;
            case "weather":
                RenderWeather();
                break;
            case "gaming":
                RenderGaming();
                break;
            case "launcher":
                RenderLauncher();
                break;
        }
    }

    private void UpdateTabBadges()
    {
        if (_tabButtons.TryGetValue("notify", out Button? chip))
        {
            int unread = _notifications?.UnreadCount ?? 0;
            chip.Content = unread > 0 ? $"Уведомления • {unread}" : "Уведомления";
        }
    }

    // ---------- настройки (секции строятся кодом) ----------

    private void BuildSettings()
    {
        if (_settingsBuilt)
        {
            return;
        }

        _settingsBuilt = true;
        var store = SettingsStore.Current;
        var panel = SettingsPanelContent;

        AddSection(panel, "Общие");

        var autostart = AddSwitchRow(panel, "Автозапуск с Windows", store.Autostart);

        void OnAutostartChanged(object? s, RoutedEventArgs e)
        {
            store.Autostart = autostart.IsChecked == true;
            ApplyAutostart(store.Autostart);
            SettingsStore.Save();
        }

        autostart.Checked += OnAutostartChanged;
        autostart.Unchecked += OnAutostartChanged;

        AddInfoRow(panel, "Горячая клавиша: Ctrl+Alt+M — свернуть/развернуть");
        AddInfoRow(panel, "Окно всегда поверх других окон; перетаскивайте его мышью");
        AddSwitchRow(panel, "Перетаскивание островка", store.DragEnabled, v =>
        {
            store.DragEnabled = v;
            SettingsStore.Save();
        });
        AddSwitchRow(panel, "Пузырьки при клике", store.Bubbles, v => store.Bubbles = v);
        AddInfoRow(panel, "Включите перетаскивание, затем перемещайте островок мышью.");
        AddSwitchRow(panel, "Перетаскивание островка", store.DragEnabled, v =>
        {
            store.DragEnabled = v;
            SettingsStore.Save();
        });
        AddSwitchRow(panel, "Размытие фона (стекло)", store.Blur, v =>
        {
            store.Blur = v;
            ApplyAppearance();
        });
        AddSliderRow(panel, "Сила размытия", store.BlurStrength, 4, 40, v =>
        {
            store.BlurStrength = Math.Round(v);
            if (BackdropBlur is not null)
            {
                BackdropBlur.Radius = store.BlurStrength;
            }
        });

        AddSection(panel, "Модули");
        AddSwitchRow(panel, "Системный монитор", store.System, v => { store.System = v; SetSystemModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Центр уведомлений", store.Notifications, v => { store.Notifications = v; SetNotificationModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Микшер звука", store.Audio, v => { store.Audio = v; SetAudioModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "История буфера обмена", store.Clipboard, v => { store.Clipboard = v; SetClipboardModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Быстрые действия", store.Actions, v => { store.Actions = v; RebuildTabs(); });
        AddSwitchRow(panel, "Погода и часы", store.Weather, v => { store.Weather = v; SetWeatherModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Игровой режим", store.Gaming, v => { store.Gaming = v; SetGamingModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Лаунчер приложений", store.Launcher, v => { store.Launcher = v; SetLauncherModule(v); RebuildTabs(); });
        AddSwitchRow(panel, "Запущенные приложения", store.RunningApps,
            v => { store.RunningApps = v; SetAppsModule(v); ApplyView(); });
        AddSwitchRow(panel, "Анимация названия", store.Typewriter, v =>
        {
            store.Typewriter = v;
            if (_view != View.Settings)
            {
                if (v)
                {
                    ResetTypewriter();
                }
                else
                {
                    TitleText.Text = _titleTarget;
                }
            }
        });
        AddSwitchRow(panel, "Эквалайзер", store.Viz, v => { store.Viz = v; SetVisualizerModule(v); ApplyView(); });

        AddSection(panel, "Уведомления");
        AddSwitchRow(panel, "Режим «Не беспокоить»", store.Dnd, v =>
        {
            store.Dnd = v;
            SyncDndUi();
        });
        AddSwitchRow(panel, "Автоматически DND во время игры", store.AutoDndOnGame,
            v => store.AutoDndOnGame = v);

        AddSection(panel, "Внешний вид");

        AddInfoRow(panel, "Цвет текста");
        string[] textColors = { "#FFFFFFFF", "#FFE5E7EB", "#FFB9C0CC", "#FF0FA3E0", "#FF8B5CF6", "#FF22C55E", "#FFF59E0B", "#FFEF4444", "#FFE879F9" };
        var textColorRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        void UpdateTextColor(string selected)
        {
            store.TextColor = selected;
            SettingsStore.Save();
            ApplyAppearance();
            BuildAppearanceSwatches(textColorRow, textColors, store.TextColor, UpdateTextColor);
        }
        BuildAppearanceSwatches(textColorRow, textColors, store.TextColor, UpdateTextColor);
        panel.Children.Add(textColorRow);

        AddInfoRow(panel, "Цвет фона островка");
        var backgroundColors = new[] { "#FF111115", "#FF202127", "#FF263238", "#FF18243A", "#FF30213A", "#FF17352D", "#FF3A2525", "#FF383027", "#FF45454D" };
        var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 10) };
        void UpdateBackgroundColor(string selected)
        {
            store.BackgroundColor = selected;
            SettingsStore.Save();
            ApplyAppearance();
            BuildAppearanceSwatches(backgroundRow, backgroundColors, store.BackgroundColor, UpdateBackgroundColor);
        }
        BuildAppearanceSwatches(backgroundRow, backgroundColors, store.BackgroundColor, UpdateBackgroundColor);
        panel.Children.Add(backgroundRow);

        AddInfoRow(panel, "Акцентный цвет");
        string[] accents = { "#0FA3E0", "#8B5CF6", "#22C55E", "#F59E0B", "#EF4444", "#E879F9", "#E5E7EB" };
        var accentPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        void UpdateAccent(string selected)
        {
            store.Accent = selected;
            SettingsStore.Save();
            ApplyAppearance();
            BuildAppearanceSwatches(accentPanel, accents, store.Accent, UpdateAccent);
            if (_view == View.Expanded) SelectTab(ActiveTab);
        }
        BuildAppearanceSwatches(accentPanel, accents, store.Accent, UpdateAccent);
        panel.Children.Add(accentPanel);

        AddSliderRow(panel, "Прозрачность", store.Opacity, 0.35, 1.0, v =>
        {
            store.Opacity = Math.Round(v, 2);
            ApplyAppearance();
        });
        AddSliderRow(panel, "Скругление", store.Radius, 4, 30, v =>
        {
            store.Radius = Math.Round(v);
            ApplyAppearance();
        });

        // Apply the custom native dark pill styles to settings controls.
        ApplyDarkSettingsControls(panel);

        AddSection(panel, "О программе");
        AddInfoRow(panel, "Desktop Island — виджет поверх всех окон, версия 1.0");
        AddInfoRow(panel, "Модули: музыка, система, уведомления, звук, буфер, действия, погода, игры, лаунчер");

        SettingsStore.Save();
    }

    private static void ApplyDarkSettingsControls(DependencyObject node)
    {
        if (node is ComboBox combo)
        {
            if (combo.Style is null)
            {
                combo.Style = (Style)((FrameworkElement)Application.Current.MainWindow).FindResource("DarkCombo");
            }
        }
        else if (node is TextBox textBox)
        {
            textBox.Foreground = Brushes.White;
            textBox.Background = new SolidColorBrush(Color.FromRgb(42, 42, 47));
            textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(75, 75, 82));
            textBox.CaretBrush = Brushes.White;
            textBox.Padding = new Thickness(8, 5, 8, 5);
        }

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            ApplyDarkSettingsControls(VisualTreeHelper.GetChild(node, i));
        }
    }

    private static void BuildAppearanceSwatches(Panel panel, IReadOnlyList<string> values, string selected, Action<string> onSelected)
    {
        panel.Children.Clear();
        foreach (string colorHex in values)
        {
            if (!TryParseColor(colorHex, out Color color)) continue;
            bool isSelected = string.Equals(selected, colorHex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 27,
                Height = 27,
                Margin = new Thickness(0, 0, 7, 0),
                CornerRadius = new CornerRadius(50),
                Background = CreateFrozen(color),
                BorderBrush = isSelected ? Brushes.White : CreateFrozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(isSelected ? 2 : 1),
                Cursor = Cursors.Hand,
                ToolTip = colorHex,
            };
            swatch.MouseLeftButtonDown += (_, e) =>
            {
                onSelected(colorHex);
                e.Handled = true;
            };
            panel.Children.Add(swatch);
        }
    }

    private void RebuildTabs()
    {
        SettingsStore.Save();
        // пересборка вкладок при изменении модулей
        TabStripPanel.Children.Clear();
        _tabButtons.Clear();
        _tabsBuilt = false;
        BuildTabs();
        if (_view == View.Expanded)
        {
            SelectTab(ActiveTab);
        }
    }

    private void SetSystemModule(bool enabled)
    {
        _system?.Dispose();
        _system = null;
        if (!enabled) return;
        _system = new SystemMonitorService();
        _system.Updated += () => Dispatcher.BeginInvoke(new Action(() => { RenderSystem(); RenderGamingStats(); }));
        _system.Start();
    }

    private void SetNotificationModule(bool enabled)
    {
        _notifications?.Dispose();
        _notifications = null;
        if (!enabled) { UpdateTabBadges(); return; }
        _notifications = new NotificationService();
        _notifications.NotificationReceived += ShowNotification;
        _notifications.HistoryChanged += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateTabBadges();
            if (_view == View.Expanded && ActiveTab == "notify") RenderNotifications();
        }));
        _ = _notifications.InitializeAsync();
    }

    private void SetAudioModule(bool enabled)
    {
        _audio?.Dispose();
        _audio = null;
        if (!enabled) return;
        _audio = new AudioService();
        _audio.Changed += () => Dispatcher.BeginInvoke(new Action(OnAudioChanged));
        _audio.Start();
    }

    private void SetClipboardModule(bool enabled)
    {
        _clipboard?.Dispose();
        _clipboard = null;
        if (!enabled) return;
        _clipboard = new ClipboardService();
        _clipboard.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_view == View.Expanded && ActiveTab == "clipboard") RenderClipboard();
        }));
        _clipboard.Start();
    }

    private void SetWeatherModule(bool enabled)
    {
        _weather?.Dispose();
        _weather = null;
        if (!enabled) return;
        _weather = new WeatherService();
        _weather.Updated += () => Dispatcher.BeginInvoke(new Action(RenderWeather));
        _weather.Start();
    }

    private void SetGamingModule(bool enabled)
    {
        _gaming?.Dispose();
        _gaming = null;
        if (!enabled) return;
        _gaming = new GamingService();
        _gaming.GameStarted += () => Dispatcher.BeginInvoke(new Action(OnGameStarted));
        _gaming.GameEnded += () => Dispatcher.BeginInvoke(new Action(OnGameEnded));
        _gaming.Start();
    }

    private void SetLauncherModule(bool enabled)
    {
        _launcher = null;
        if (!enabled) return;
        _launcher = new LauncherService();
        _launcher.Changed += () => Dispatcher.BeginInvoke(new Action(RenderLauncher));
        _launcher.Load(SettingsStore.Current.FavoriteApps);
    }

    private void SetAppsModule(bool enabled)
    {
        _appsMonitor?.Dispose();
        _appsMonitor = null;
        _apps = Array.Empty<RunningApp>();
        if (!enabled) { RenderApps(); return; }
        _appsMonitor = new ProcessMonitorService();
        _appsMonitor.Changed += () => Dispatcher.BeginInvoke(new Action(OnAppsChanged));
        _appsMonitor.Start();
    }

    private void SetVisualizerModule(bool enabled)
    {
        _visualizer?.Dispose();
        _visualizer = null;
        if (!enabled) return;
        _visualizer = new AudioVisualizer();
        _visualizer.Start();
    }

    private static void AddSection(Panel panel, string title)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateFrozen(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 14, 0, 6),
        });
    }

    private static void AddInfoRow(Panel panel, string text)
    {
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            Foreground = CreateFrozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 4),
        });
    }

    private CheckBox AddSwitchRow(Panel panel, string label, bool value)
    {
        var grid = new Grid { Style = (Style)FindResource("SettingRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = value,
        };
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        panel.Children.Add(grid);
        return toggle;
    }

    private void AddSwitchRow(Panel panel, string label, bool value, Action<bool> onChanged)
    {
        var grid = new Grid { Style = (Style)FindResource("SettingRow") };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var toggle = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            IsChecked = value,
        };
        Grid.SetColumn(toggle, 1);
        toggle.Checked += (_, _) => onChanged(true);
        toggle.Unchecked += (_, _) => onChanged(false);
        grid.Children.Add(toggle);
        panel.Children.Add(grid);
    }

    private void AddSliderRow(Panel panel, string label, double value, double min, double max, Action<double> onChanged)
    {
        var grid = new Grid { Height = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 13.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var slider = new Slider
        {
            Style = (Style)FindResource("DarkSlider"),
            Minimum = min,
            Maximum = max,
            Value = value,
            Width = 180,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(slider, 1);
        slider.ValueChanged += (_, _) =>
        {
            if (!_syncingUi)
            {
                onChanged(slider.Value);
            }
        };

        grid.Children.Add(slider);
        panel.Children.Add(grid);
    }

    private void SyncSettingsUi()
    {
        // значения читались при построении; при повторных открытиях — просто синхронизация DND
        SyncDndUi();
    }

    private void SyncDndUi()
    {
        _syncingUi = true;
        try
        {
            bool dnd = SettingsStore.Current.Dnd;
            if (DndToggle.IsChecked != dnd)
            {
                DndToggle.IsChecked = dnd;
            }

            if (GamingDndToggle.IsChecked != SettingsStore.Current.AutoDndOnGame)
            {
                GamingDndToggle.IsChecked = SettingsStore.Current.AutoDndOnGame;
            }
        }
        finally
        {
            _syncingUi = false;
        }
    }

    // ---------- переключатели из XAML ----------

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingUi || !IsLoaded)
        {
            return;
        }

        var store = SettingsStore.Current;
        if (ReferenceEquals(sender, DndToggle))
        {
            store.Dnd = DndToggle.IsChecked == true;
            SettingsStore.Save();
        }
        else if (ReferenceEquals(sender, GamingDndToggle))
        {
            store.AutoDndOnGame = GamingDndToggle.IsChecked == true;
            SettingsStore.Save();
        }
    }

    // ---------- печатная машинка ----------

    private void ResetTypewriter()
    {
        _shownCount = 0;
        _typePhase = 0;
        _typeTick = 0;
    }

    private void RefreshTitle()
    {
        if (_view == View.Settings)
        {
            TitleText.Text = "Настройки";
            return;
        }

        if (!SettingsStore.Current.Typewriter)
        {
            TitleText.Text = _titleTarget;
            return;
        }

        _lastAnimatedTitle = _titleTarget;
        ResetTypewriter();
        TitleText.Opacity = 1;
        TitleText.BeginAnimation(UIElement.OpacityProperty, null);
        TitleText.RenderTransform = Transform.Identity;
    }

    private void OnTypeTick(object? sender, EventArgs e)
    {
        if (_view == View.Settings || !SettingsStore.Current.Typewriter)
        {
            return;
        }

        string target = _titleTarget;
        _typeTick++;

        switch (_typePhase)
        {
            case 0: // набор по буквам (~90 мс на букву)
                if (_typeTick % 3 == 0 && _shownCount < target.Length)
                {
                    _shownCount++;
                }

                if (_shownCount >= target.Length)
                {
                    _typePhase = 1;
                    _typeTick = 0;
                }

                break;
            case 1: // пауза с текстом (~2 с)
                if (_typeTick >= 67)
                {
                    _typePhase = 2;
                    _typeTick = 0;
                }

                break;
            case 2: // стирание (~60 мс)
                if ((_typeTick & 1) == 0 && _shownCount > 0)
                {
                    _shownCount--;
                }

                if (_shownCount == 0)
                {
                    _typePhase = 3;
                    _typeTick = 0;
                }

                break;
            default: // пауза пустой (~0.4 с)
                if (_typeTick >= 14)
                {
                    _typePhase = 0;
                    _typeTick = 0;
                }

                break;
        }

        if (_shownCount > target.Length)
        {
            _shownCount = target.Length;
        }

        string text = target.Substring(0, _shownCount);
        bool typing = _typePhase == 0 || _typePhase == 2;
        if (target.Length > 0 && typing && (_typeTick / 8) % 2 == 0)
        {
            text += "▌";
        }

        TitleText.Text = text;
    }

    // ---------- индикаторы звука ----------

    private void CreateIndicators()
    {
        var white = Brushes.White;

        for (int i = 0; i < DotCount; i++)
        {
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = white,
                Opacity = 0.95,
                RenderTransformOrigin = new Point(0.5, 0.5),
                Margin = new Thickness(0, 0, i == DotCount - 1 ? 0 : 5, 0),
            };

            var scale = new ScaleTransform(0.4, 0.4);
            dot.RenderTransform = scale;
            _dotScales[i] = scale;
            _dotValues[i] = 0.4;
            DotsPanel.Children.Add(dot);
        }

        for (int i = 0; i < SmallBarCount; i++)
        {
            var bar = new Border
            {
                Width = 3,
                Height = 18,
                CornerRadius = new CornerRadius(1.5),
                Background = white,
                Opacity = 0.95,
                VerticalAlignment = VerticalAlignment.Bottom,
                RenderTransformOrigin = new Point(0, 1),
                Margin = new Thickness(0, 0, i == SmallBarCount - 1 ? 0 : 3, 0),
            };

            var scale = new ScaleTransform(1, 0.3);
            bar.RenderTransform = scale;
            _smallBarScales[i] = scale;
            _smallBarValues[i] = 0.3;
            SmallBarsPanel.Children.Add(bar);
        }
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_dotScales[0] is null)
        {
            return;
        }

        bool live = _visualizer is { IsRunning: true };
        if (live)
        {
            _visualizer!.CopyLevels(_levels);
        }
        else
        {
            _idlePhase += 0.09;
        }

        for (int d = 0; d < DotCount; d++)
        {
            double level = live
                ? BandMax(d * 8, d * 8 + 8)
                : 0.2 + 0.2 * (0.5 + 0.5 * Math.Sin(_idlePhase + d * 1.1));
            double target = 0.35 + Math.Clamp(level, 0, 1) * 0.75;
            double current = Smooth(_dotValues[d], target);
            _dotValues[d] = current;
            _dotScales[d].ScaleX = current;
            _dotScales[d].ScaleY = current;
        }

        for (int b = 0; b < SmallBarCount; b++)
        {
            double level = live
                ? BandMax(b * 10, b * 10 + 10)
                : 0.25 + 0.25 * (0.5 + 0.5 * Math.Sin(_idlePhase + b * 1.3));
            double target = 0.2 + Math.Clamp(level, 0, 1) * 0.9;
            double current = Smooth(_smallBarValues[b], target);
            _smallBarValues[b] = current;
            _smallBarScales[b].ScaleY = current;
        }

        // «вода»: покачивание подложки и дыхание силы размытия
        if (SettingsStore.Current.Blur &&
            BackdropHost.Visibility == Visibility.Visible &&
            BackdropBlur is not null)
        {
            double t = DateTime.Now.TimeOfDay.TotalSeconds;
            BackdropShift.X = Math.Sin(t * 0.7) * 2.5;
            BackdropShift.Y = Math.Cos(t * 0.9) * 2.0;
            BackdropBlur.Radius = SettingsStore.Current.BlurStrength + Math.Sin(t * 1.3) * 3;
        }
    }

    private double BandMax(int from, int to)
    {
        float max = 0f;
        for (int i = from; i < to && i < _levels.Length; i++)
        {
            if (_levels[i] > max)
            {
                max = _levels[i];
            }
        }

        return max;
    }

    private static double Smooth(double current, double target) =>
        current + (target - current) * (target > current ? 0.55 : 0.16);

    // ---------- уведомления: баннер ----------

    private void ShowNotification(NotificationInfo info)
    {
        if (SettingsStore.Current.Dnd)
        {
            return;
        }

        BannerApp.Text = info.AppName;
        BannerText.Text = string.IsNullOrEmpty(info.Text) ? "Новое уведомление" : info.Text;
        Banner.Visibility = Visibility.Visible;

        Banner.Opacity = 0;
        BannerShift.X = -30;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Banner.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        BannerShift.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        _bannerTimer.Stop();
        _bannerTimer.Start();
    }

    private void HideBanner()
    {
        _bannerTimer.Stop();
        if (Banner.Visibility != Visibility.Visible)
        {
            return;
        }

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
        fade.Completed += (_, _) =>
        {
            Banner.Visibility = Visibility.Collapsed;
            BannerShift.X = -30;
            Banner.BeginAnimation(UIElement.OpacityProperty, null);
            BannerShift.BeginAnimation(TranslateTransform.XProperty, null);
        };
        Banner.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------- пузырьки от кликов ----------

    private void OnAnyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (SettingsStore.Current.DragEnabled && e.ChangedButton == MouseButton.Left && _view != View.Settings &&
            (e.OriginalSource is not DependencyObject source || !IsInteractiveElement(source)))
        {
            _isDragging = true;
            _dragMoved = false;
            _dragStartedOnButton = e.OriginalSource is DependencyObject dragSource && IsInteractiveElement(dragSource);
            GetCursorPos(out NativePoint cursor);
            _dragScreenStart = new Point(cursor.X, cursor.Y);
            _dragWindowLeftStart = Left;
            _dragWindowTopStart = Top;
            _suppressNextClick = false;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (!SettingsStore.Current.Bubbles)
        {
            return;
        }

        try
        {
            Point p = e.GetPosition(EffectsLayer);
            SpawnBubble(p);
        }
        catch
        {
            // анимация не критична
        }
    }

    private static bool IsInteractiveElement(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase or Slider or TextBox or ComboBox or ScrollBar)
            {
                return true;
            }
        }

        return false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        GetCursorPos(out NativePoint cursor);
        Point p = new(cursor.X, cursor.Y);
        double dx = p.X - _dragScreenStart.X;
        double dy = p.Y - _dragScreenStart.Y;
        if (Math.Abs(dx) + Math.Abs(dy) > 3)
        {
            _dragMoved = true;
        }

        Left = _dragWindowLeftStart + dx;
        Top = _dragWindowTopStart + dy;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        _suppressNextClick = _dragMoved && _dragStartedOnButton;
        SettingsStore.Current.WindowLeft = Left;
        SettingsStore.Current.WindowTop = Top;
        SettingsStore.Save();
    }

    private void OnIslandClick(object? sender, RoutedEventArgs e)
    {
        if (_suppressNextClick)
        {
            _suppressNextClick = false;
            e.Handled = true;
        }
    }

    /// <summary>Пузырёк: вырастает в точке клика, всплывает и растворяется.</summary>
    private void SpawnBubble(Point p)
    {
        double size = 9 + Random.Shared.NextDouble() * 9;

        var bubble = new Ellipse
        {
            Width = size,
            Height = size,
            StrokeThickness = 1.6,
            Stroke = (Brush)FindResource("AccentBrush"),
            Fill = CreateFrozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        };

        double left = p.X - size / 2;
        double top = p.Y - size / 2;
        Canvas.SetLeft(bubble, left);
        Canvas.SetTop(bubble, top);

        var scale = new ScaleTransform(0.3, 0.3);
        bubble.RenderTransform = scale;
        bubble.RenderTransformOrigin = new Point(0.5, 0.5);
        EffectsLayer.Children.Add(bubble);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(720);

        var grow = new DoubleAnimation(0.3, 1.0, duration) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        bubble.BeginAnimation(
            Canvas.TopProperty,
            new DoubleAnimation(top, top - 30 - Random.Shared.NextDouble() * 14, duration)
            {
                EasingFunction = ease,
            });

        bubble.BeginAnimation(
            Canvas.LeftProperty,
            new DoubleAnimation(left, left + (Random.Shared.NextDouble() - 0.5) * 26, duration)
            {
                EasingFunction = ease,
            });

        var fade = new DoubleAnimation(1, 0, duration)
        {
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(50),
        };
        fade.Completed += (_, _) => EffectsLayer.Children.Remove(bubble);
        bubble.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------- панель «Система» ----------

    private void RenderSystem()
    {
        if (_system is null || !StatsPanel.IsVisible)
        {
            return;
        }

        var s = _system.Current;
        StatsPanel.Children.Clear();

        if (s.CpuPercent.HasValue)
        {
            AddStatRow("CPU", $"{s.CpuPercent:0}%", s.CpuPercent.Value / 100.0);
        }

        if (s.GpuPercent.HasValue)
        {
            AddStatRow("GPU", $"{s.GpuPercent:0}%", s.GpuPercent.Value / 100.0);
        }

        double ramPercent = s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : 0;
        AddStatRow("RAM", $"{s.RamUsedGb:0.0} / {s.RamTotalGb:0.0} ГБ", ramPercent / 100.0);
        AddStatRow("Сеть", $"↓ {FormatRate(s.NetDownBps)} ↑ {FormatRate(s.NetUpBps)}", -1);

        if (s.DiskPercent.HasValue)
        {
            AddStatRow("Диск", $"{s.DiskPercent:0}%", s.DiskPercent.Value / 100.0);
        }

        if (s.BatteryPercent >= 0)
        {
            AddStatRow(
                s.OnBattery ? "Батарея (автономно)" : "Батарея (сеть)",
                $"{s.BatteryPercent:0}%",
                s.BatteryPercent / 100.0);
        }

        AddStatRow("Аптайм", FormatTime(s.Uptime), -1);

        RenderSparkline();
    }

    private void AddStatRow(string label, string value, double ratio01)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = CreateFrozen(Color.FromRgb(0x9A, 0x9A, 0x9E)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (ratio01 >= 0)
        {
            var track = new Border
            {
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = CreateFrozen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            Grid.SetColumn(track, 1);

            var fill = new Border
            {
                Height = 7,
                CornerRadius = new CornerRadius(3.5),
                Background = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, Math.Clamp(ratio01, 0, 1) * 240),
            };
            track.Child = fill;
            grid.Children.Add(track);
        }
        else
        {
            var empty = new Border { Background = Brushes.Transparent };
            Grid.SetColumn(empty, 1);
            grid.Children.Add(empty);
        }

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);

        StatsPanel.Children.Add(grid);
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / (1024 * 1024):0.0} МБ/с";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0} КБ/с";
        }

        return $"{bytesPerSecond:0} Б/с";
    }

    private void RenderSparkline()
    {
        if (_system is null)
        {
            return;
        }

        var history = _system.CpuHistory;
        double width = SparkHost.ActualWidth > 10 ? SparkHost.ActualWidth : 400;
        double height = SparkHost.ActualHeight > 10 ? SparkHost.ActualHeight : 54;

        SparkLine.Points.Clear();
        if (history.Count < 2)
        {
            return;
        }

        for (int i = 0; i < history.Count; i++)
        {
            double x = i / (double)(history.Count - 1) * width;
            double y = height - 3 - Math.Clamp(history[i], 0, 100) / 100.0 * (height - 6);
            SparkLine.Points.Add(new Point(Math.Round(x, 1), Math.Round(y, 1)));
        }
    }

    // ---------- панель «Уведомления» ----------

    private void RenderNotifications()
    {
        NotifyListPanel.Children.Clear();
        if (_notifications is null)
        {
            return;
        }

        var history = _notifications.History;
        NotifyCountText.Text = history.Count == 0
            ? "история пуста"
            : $"{history.Count} всего · {_notifications.UnreadCount} новых";

        foreach (var record in history.Take(30))
        {
            NotifyListPanel.Children.Add(MakeNotifyRow(record));
        }
    }

    private Border MakeNotifyRow(NotificationRecord record)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = record.AppName,
            FontSize = 11,
            Foreground = (Brush)FindResource("AccentBrush"),
        });
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(record.Text) ? "(без текста)" : record.Text,
            FontSize = 12.5,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
        });
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(new TextBlock
        {
            Text = record.Time.ToString("HH:mm"),
            FontSize = 10.5,
            Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 2, 8, 0),
        });

        var remove = new Button
        {
            Style = (Style)FindResource("PlainButton"),
            Width = 20,
            Height = 20,
            Content = new PathShape
            {
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(1, 1), new[] { new LineSegment(new Point(9, 9), true) }, false),
                    new PathFigure(new Point(9, 1), new[] { new LineSegment(new Point(1, 9), true) }, false),
                }),
                Stroke = Brushes.White,
                StrokeThickness = 1.4,
                Stretch = Stretch.Uniform,
                Width = 9,
                Height = 9,
            },
        };
        remove.Click += (_, _) =>
        {
            _notifications?.Remove(record);
            RenderNotifications();
            UpdateTabBadges();
        };
        right.Children.Add(remove);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        return new Border
        {
            Background = CreateFrozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 8, 6),
            Child = grid,
        };
    }

    // ---------- панель «Звук» ----------

    private void OnAudioChanged()
    {
        RefreshAudioUi();
        if (_view == View.Expanded && ActiveTab == "audio" && !_audioDragging)
        {
            RenderSessions();
        }
    }

    private void RefreshAudioUi()
    {
        if (_audio is null)
        {
            return;
        }

        _syncingUi = true;
        try
        {
            double percent = Math.Round(_audio.MasterVolume * 100);
            if (Math.Abs(MasterVolumeSlider.Value - percent) > 0.5)
            {
                MasterVolumeSlider.Value = percent;
            }

            if (Math.Abs(VolumeSlider.Value - percent) > 0.5)
            {
                VolumeSlider.Value = percent;
            }

            MasterVolumeText.Text = $"{percent:0}%";
            VolumeValueText.Text = $"{percent:0}%";
            MuteGlyph.Text = _audio.Muted ? "🔇" : "🔊";
            DeviceText.Text = string.IsNullOrEmpty(_audio.DeviceName)
                ? "Устройство воспроизведения"
                : _audio.DeviceName;
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private void RenderSessions()
    {
        SessionsPanel.Children.Clear();
        if (_audio is null)
        {
            return;
        }

        foreach (var session in _audio.Sessions)
        {
            SessionsPanel.Children.Add(MakeSessionRow(session));
        }

        if (_audio.Sessions.Count == 0)
        {
            SessionsPanel.Children.Add(new TextBlock
            {
                Text = "Нет активных сессий воспроизведения",
                FontSize = 12.5,
                Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private Border MakeSessionRow(AppSession session)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = session.Name,
            FontSize = 12.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 110,
        });

        var slider = new Slider
        {
            Style = (Style)FindResource("DarkSlider"),
            Minimum = 0,
            Maximum = 100,
            Value = Math.Round(session.Volume * 100),
            Tag = session.Pid,
        };
        Grid.SetColumn(slider, 1);
        slider.PreviewMouseLeftButtonDown += (_, _) => _audioDragging = true;
        slider.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _audioDragging = false;
            RenderSessions();
        };
        slider.ValueChanged += (_, _) =>
        {
            if (_syncingUi || _audioDragging)
            {
                return;
            }

            _audio?.SetSessionVolume(session.Pid, (float)(slider.Value / 100));
        };
        grid.Children.Add(slider);

        var valueText = new TextBlock
        {
            Text = $"{Math.Round(session.Volume * 100):0}%",
            FontSize = 11.5,
            Foreground = CreateFrozen(Color.FromRgb(0x9A, 0x9A, 0x9E)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0),
            Width = 36,
        };
        Grid.SetColumn(valueText, 2);
        grid.Children.Add(valueText);

        var mute = new Button
        {
            Style = (Style)FindResource("PlainButton"),
            Width = 26,
            Height = 26,
            Content = new TextBlock
            {
                Text = session.Muted ? "🔇" : "🔊",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        mute.Click += (_, _) => _audio?.ToggleSessionMute(session.Pid);
        Grid.SetColumn(mute, 3);
        grid.Children.Add(mute);

        return new Border
        {
            Background = CreateFrozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 4, 8, 4),
            Child = grid,
        };
    }

    private void MasterVolume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi)
        {
            return;
        }

        _audio?.SetMasterVolume((float)(MasterVolumeSlider.Value / 100));
    }

    private void VolumeSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingUi)
        {
            return;
        }

        _audio?.SetMasterVolume((float)(VolumeSlider.Value / 100));
    }

    private void Mute_Click(object sender, RoutedEventArgs e) => _audio?.ToggleMute();

    // ---------- панель «Буфер обмена» ----------

    private void ClipboardSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RenderClipboard();
        }
    }

    private void ClipboardClear_Click(object sender, RoutedEventArgs e) => _clipboard?.Clear(false);

    private void RenderClipboard()
    {
        ClipboardListPanel.Children.Clear();
        if (_clipboard is null)
        {
            return;
        }

        string query = ClipboardSearch.Text?.Trim() ?? string.Empty;
        var items = _clipboard.Items
            .Where(i => query.Length == 0 || i.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(40);

        foreach (var entry in items)
        {
            ClipboardListPanel.Children.Add(MakeClipboardRow(entry));
        }

        if (!ClipboardListPanel.Children.OfType<UIElement>().Any())
        {
            ClipboardListPanel.Children.Add(new TextBlock
            {
                Text = "Пока ничего не скопировано",
                FontSize = 12.5,
                Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private Border MakeClipboardRow(ClipboardEntry entry)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var main = new StackPanel();
        main.Children.Add(new TextBlock
        {
            Text = entry.Text.Replace("\n", " ").Replace("\r", string.Empty),
            FontSize = 12.5,
            Foreground = Brushes.White,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 300,
        });
        main.Children.Add(new TextBlock
        {
            Text = entry.Time.ToString("dd.MM HH:mm") + (entry.Pinned ? "  • закреплено" : string.Empty),
            FontSize = 10.5,
            Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
            Margin = new Thickness(0, 2, 0, 0),
        });

        var clickArea = new Button
        {
            Style = (Style)FindResource("PlainButton"),
            Content = main,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(0),
        };
        clickArea.Click += (_, _) =>
        {
            _clipboard?.Copy(entry.Text);
            ShowNotification(new NotificationInfo("Буфер обмена",
                "Скопировано: " + (entry.Text.Length > 60 ? entry.Text[..60] + "…" : entry.Text)));
        };
        Grid.SetColumn(clickArea, 0);
        grid.Children.Add(clickArea);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };

        var pin = new Button
        {
            Style = (Style)FindResource("PlainButton"),
            Width = 22,
            Height = 22,
            Opacity = entry.Pinned ? 1 : 0.5,
            Content = new TextBlock { Text = "📌", FontSize = 11 },
        };
        pin.Click += (_, _) =>
        {
            _clipboard?.TogglePin(entry);
            RenderClipboard();
        };
        actions.Children.Add(pin);

        var remove = new Button
        {
            Style = (Style)FindResource("PlainButton"),
            Width = 22,
            Height = 22,
            Content = new PathShape
            {
                Data = new PathGeometry(new[]
                {
                    new PathFigure(new Point(1, 1), new[] { new LineSegment(new Point(9, 9), true) }, false),
                    new PathFigure(new Point(9, 1), new[] { new LineSegment(new Point(1, 9), true) }, false),
                }),
                Stroke = Brushes.White,
                StrokeThickness = 1.4,
                Stretch = Stretch.Uniform,
                Width = 9,
                Height = 9,
            },
        };
        remove.Click += (_, _) =>
        {
            _clipboard?.Remove(entry);
            RenderClipboard();
        };
        actions.Children.Add(remove);

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            Background = CreateFrozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 5, 6, 5),
            Child = grid,
        };
    }

    // ---------- панель «Действия» ----------

    private void BuildActions()
    {
        if (_actionsBuilt)
        {
            return;
        }

        _actionsBuilt = true;
        ActionsPanel.Children.Clear();

        AddActionTile("🔒", "Заблокировать", () => QuickActions.LockWorkstation());
        AddActionTile("📋", "Диспетчер задач", () => QuickActions.OpenTaskManager());
        AddActionTile("⚙", "Настройки", () => QuickActions.OpenWindowsSettings());
        AddActionTile("📁", "Проводник", () => QuickActions.OpenExplorer());
        AddActionTile("📸", "Скриншот", DoScreenshot);
        AddActionTile("🔊", "Звук", ToggleMuteAction);
        AddActionTile("🌙", "Не беспокоить", ToggleDndAction);
        AddActionTile("🔵", "Bluetooth", async () => await ToggleBluetoothAsync());
        AddActionTile("📶", "Wi-Fi", () => ToggleWifi());
        AddActionTile("⚡", "Питание: макс.", () => QuickActions.SetPowerScheme("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"));
        AddActionTile("🔋", "Питание: эконом", () => QuickActions.SetPowerScheme("a1841308-3541-4fab-bc81-f71556f20b4a"));
        AddActionTile("⚖", "Питание: баланс", () => QuickActions.SetPowerScheme("381b4222-f694-41f0-9685-ff5bb260df2e"));
    }

    private void AddActionTile(string icon, string label, Action action)
    {
        AddActionTile(icon, label, () =>
        {
            try
            {
                action();
            }
            catch
            {
                // действие не сработало
            }

            RefreshActionStates();
            return Task.CompletedTask;
        });
    }

    private void AddActionTile(string icon, string label, Func<Task> action)
    {
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new TextBlock
        {
            Text = icon,
            FontSize = 17,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        content.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 84,
            Margin = new Thickness(0, 3, 0, 0),
        });

        var tile = new Button
        {
            Style = (Style)FindResource("ActionTile"),
            Width = 92,
            Height = 66,
            Content = content,
            Tag = label,
        };
        tile.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch
            {
                // действие не сработало
            }
        };

        _actionTiles.Add(tile);
        ActionsPanel.Children.Add(tile);
    }

    private void DoScreenshot()
    {
        string? path = QuickActions.CaptureScreenshot();
        if (path is not null)
        {
            ShowNotification(new NotificationInfo("Скриншот", "Сохранено: " + Path.GetFileName(path)));
        }
        else
        {
            ShowNotification(new NotificationInfo("Скриншот", "Не удалось сделать снимок"));
        }
    }

    private void ToggleMuteAction()
    {
        _audio?.ToggleMute();
        ShowNotification(new NotificationInfo("Звук",
            _audio?.Muted == true ? "Микрофон/звук выключен" : "Звук включён"));
    }

    private void ToggleDndAction()
    {
        var store = SettingsStore.Current;
        store.Dnd = !store.Dnd;
        SettingsStore.Save();
        SyncDndUi();
        ShowNotification(new NotificationInfo("Не беспокоить",
            store.Dnd ? "Уведомления отключены" : "Уведомления включены"));
    }

    private async Task ToggleBluetoothAsync()
    {
        var (available, on) = await QuickActions.GetBluetoothAsync();
        if (!available)
        {
            ShowNotification(new NotificationInfo("Bluetooth", "Bluetooth недоступен"));
            return;
        }

        bool ok = await QuickActions.SetBluetoothAsync(!on);
        var after = await QuickActions.GetBluetoothAsync();
        ShowNotification(new NotificationInfo("Bluetooth",
            ok ? (after.On ? "Bluetooth включён" : "Bluetooth выключен") : "Нет доступа к Bluetooth"));
        RefreshActionStates();
    }

    private void ToggleWifi()
    {
        var (available, connected, ssid) = QuickActions.GetWifi();
        if (!available)
        {
            ShowNotification(new NotificationInfo("Wi-Fi", "Wi-Fi адаптер не найден"));
            return;
        }

        ShowNotification(new NotificationInfo("Wi-Fi",
            connected ? $"Подключено: {ssid}" : "Не подключено"));
        RefreshActionStates();
    }

    private void RefreshActionStates()
    {
        // синхронизация подписей динамических плиток
        foreach (var tile in _actionTiles)
        {
            if (tile.Content is StackPanel stack && stack.Children.Count >= 2 &&
                stack.Children[1] is TextBlock label)
            {
                switch (tile.Tag as string)
                {
                    case "Звук":
                        label.Text = _audio?.Muted == true ? "Звук выкл" : "Звук";
                        break;
                    case "Не беспокоить":
                        label.Text = SettingsStore.Current.Dnd ? "DND вкл" : "Не беспокоить";
                        break;
                }
            }
        }
    }

    // ---------- панель «Погода» ----------

    private void RenderWeather()
    {
        if (_weather is null)
        {
            return;
        }

        var now = _weather.Now;
        if (now is not null)
        {
            WeatherTemp.Text = $"{now.Emoji} {now.TempC:0}°";
            WeatherCity.Text = string.IsNullOrEmpty(now.City) ? now.Description : $"{now.City} · {now.Description}";
        }
        else
        {
            WeatherTemp.Text = "—";
            WeatherCity.Text = "погода недоступна";
        }

        ForecastPanel.Children.Clear();
        if (_weather.Forecast is null)
        {
            return;
        }

        foreach (var day in _weather.Forecast.Take(3))
        {
            var stack = new StackPanel
            {
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            stack.Children.Add(new TextBlock
            {
                Text = day.DayName,
                FontSize = 11,
                Foreground = CreateFrozen(Color.FromRgb(0x9A, 0x9A, 0x9E)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = day.Emoji,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = $"{day.MaxC:0}° / {day.MinC:0}°",
                FontSize = 11.5,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            ForecastPanel.Children.Add(new Border
            {
                Background = CreateFrozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Child = stack,
            });
        }
    }

    private void TimerToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_timerRunning)
        {
            _timerRunning = false;
            _timerLeftSeconds = Math.Max(0, (int)Math.Round((_timerEnd - DateTime.Now).TotalSeconds));
            TimerToggleBtn.Content = "Старт";
        }
        else
        {
            if (_timerLeftSeconds <= 0)
            {
                _timerLeftSeconds = 300;
            }

            _timerEnd = DateTime.Now.AddSeconds(_timerLeftSeconds);
            _timerRunning = true;
            TimerToggleBtn.Content = "Пауза";
        }
    }

    private void TimerReset_Click(object sender, RoutedEventArgs e)
    {
        _timerRunning = false;
        _timerLeftSeconds = 300;
        _timerEnd = DateTime.Now.AddSeconds(300);
        TimerToggleBtn.Content = "Старт";
        TimerText.Text = "05:00";
    }

    private void Stopwatch_Click(object sender, RoutedEventArgs e)
    {
        if (_stopwatchRunning)
        {
            _stopwatchElapsed += DateTime.Now - _stopwatchStart;
            _stopwatchRunning = false;
            StopwatchBtn.Content = "Старт";
        }
        else
        {
            _stopwatchStart = DateTime.Now;
            _stopwatchRunning = true;
            StopwatchBtn.Content = "Стоп";
        }
    }

    private void UpdateTimerDisplays()
    {
        if (_timerRunning)
        {
            TimeSpan left = _timerEnd - DateTime.Now;
            if (left <= TimeSpan.Zero)
            {
                left = TimeSpan.Zero;
                _timerRunning = false;
                TimerToggleBtn.Content = "Старт";
                _timerLeftSeconds = 0;
                try
                {
                    System.Media.SystemSounds.Exclamation.Play();
                }
                catch
                {
                    // звук не критичен
                }

                ShowNotification(new NotificationInfo("Таймер", "Время вышло!"));
            }

            TimerText.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"mm\:ss");
        }
        else
        {
            TimerText.Text = TimeSpan.FromSeconds(_timerLeftSeconds).ToString(@"mm\:ss");
        }

        TimeSpan sw = _stopwatchElapsed + (_stopwatchRunning ? DateTime.Now - _stopwatchStart : TimeSpan.Zero);
        StopwatchText.Text = $"{(int)sw.TotalMinutes:00}:{sw.Seconds:00}.{sw.Milliseconds / 100}";
    }

    // ---------- панель «Игры» ----------

    private void OnGameStarted()
    {
        var store = SettingsStore.Current;
        _dndBeforeGame = store.Dnd;
        if (store.AutoDndOnGame && !store.Dnd)
        {
            store.Dnd = true;
            SettingsStore.Save();
            SyncDndUi();
        }

        ShowNotification(new NotificationInfo("Игровой режим", $"Запущена игра: {_gaming?.GameName ?? "?"}"));
        RenderGaming();
    }

    private void OnGameEnded()
    {
        var store = SettingsStore.Current;
        if (store.AutoDndOnGame && store.Dnd && !_dndBeforeGame)
        {
            store.Dnd = false;
            SettingsStore.Save();
            SyncDndUi();
        }

        RenderGaming();
    }

    private void RenderGaming()
    {
        if (_gaming is null)
        {
            return;
        }

        GamingStatus.Text = _gaming.InGame
            ? $"Игра: {_gaming.GameName ?? "Игра"}"
            : "Игра не обнаружена";
        GamingTimer.Text = _gaming.InGame
            ? $"Сессия: {FormatTime(_gaming.SessionDuration)}"
            : "Сессия: —";
        RenderGamingStats();
    }

    private void RenderGamingStats()
    {
        if (_system is null)
        {
            return;
        }

        var s = _system.Current;
        string cpu = s.CpuPercent.HasValue ? $"CPU {s.CpuPercent:0}%" : "CPU —";
        string ram = s.RamTotalGb > 0
            ? $"RAM {s.RamUsedGb:0.0} ГБ"
            : "RAM —";
        GamingStats.Text = $"{cpu} · {ram}";
    }

    // ---------- панель «Лаунчер» ----------

    private void LauncherSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RenderLauncher();
        }
    }

    private void RenderLauncher()
    {
        LauncherList.Children.Clear();
        if (_launcher is null)
        {
            return;
        }

        string query = LauncherSearch.Text?.Trim() ?? string.Empty;
        var apps = _launcher.Apps
            .Where(a => query.Length == 0 || a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(36);

        foreach (var app in apps)
        {
            LauncherList.Children.Add(MakeLauncherTile(app));
        }

        if (!LauncherList.Children.OfType<UIElement>().Any())
        {
            LauncherList.Children.Add(new TextBlock
            {
                Text = "Ничего не найдено",
                FontSize = 12.5,
                Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private Button MakeLauncherTile(LauncherApp app)
    {
        if (app.IconPath is not null)
        {
            GetAppIcon(app.IconPath); // заполняем кэш иконок
        }

        var content = new StackPanel { Width = 84, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = CreateFrozen(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            Child = app.IconPath is not null && _iconCache.TryGetValue(app.IconPath, out ImageSource? icon)
                ? new Image { Source = icon, Width = 22, Height = 22, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("AccentBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        content.Children.Add(new TextBlock
        {
            Text = (app.Favorite ? "★ " : string.Empty) + app.Name,
            FontSize = 10.5,
            Foreground = app.Favorite
                ? (Brush)FindResource("AccentBrush")
                : CreateFrozen(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 30,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        });

        var tile = new Button
        {
            Style = (Style)FindResource("ActionTile"),
            Width = 88,
            Height = 84,
            Content = content,
        };
        tile.Click += (_, _) => _launcher?.Launch(app);

        var menu = new ContextMenu();
        var favoriteItem = new MenuItem
        {
            Header = app.Favorite ? "Убрать из избранного" : "В избранное",
        };
        favoriteItem.Click += (_, _) =>
        {
            _launcher?.ToggleFavorite(app);
            SettingsStore.Current.FavoriteApps = _launcher?.FavoritePaths.ToList() ?? new List<string>();
            SettingsStore.Save();
            RenderLauncher();
        };
        menu.Items.Add(favoriteItem);
        tile.ContextMenu = menu;

        return tile;
    }

    // ---------- история треков и источники ----------

    private void RenderTrackHistory()
    {
        if (TrackHistoryPanel is null)
        {
            return;
        }

        TrackHistoryPanel.Children.Clear();
        var tracks = _media.RecentTracks;
        TrackHistoryScroll.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (tracks.Count == 0)
        {
            return;
        }

        foreach (var track in tracks.Take(6))
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = track.Title,
                FontSize = 12.5,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 300,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var time = new TextBlock
            {
                Text = track.Time.ToString("HH:mm"),
                FontSize = 10.5,
                Foreground = CreateFrozen(Color.FromRgb(0x7A, 0x7A, 0x80)),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(time, 1);
            row.Children.Add(time);

            TrackHistoryPanel.Children.Add(new Border
            {
                Background = CreateFrozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 2),
                Child = row,
            });
        }
    }

    private void UpdateSourcesText()
    {
        try
        {
            var sources = _media.GetActiveSources();
            SourcesText.Text = sources.Count == 0
                ? "Источник: —"
                : "Источники: " + string.Join(", ", sources);
        }
        catch
        {
            SourcesText.Text = string.Empty;
        }
    }

    // ---------- запущенные приложения ----------

    private void OnAppsChanged()
    {
        if (_appsMonitor is null || !SettingsStore.Current.RunningApps)
        {
            return;
        }

        _apps = _appsMonitor.Apps;
        if (_view == View.Expanded)
        {
            RenderApps();
        }
    }

    private void RenderApps()
    {
        AppsRow.Children.Clear();
        if (_apps.Count == 0)
        {
            return;
        }

        int visibleCount = _appsExpanded ? _apps.Count : Math.Min(3, _apps.Count);
        for (int i = 0; i < visibleCount; i++)
        {
            AppsRow.Children.Add(MakeAppChip(_apps[i]));
        }

        if (_appsExpanded && _apps.Count > 3)
        {
            AppsRow.Children.Add(MakeCollapseChip());
        }
        else if (!_appsExpanded && _apps.Count > 3)
        {
            AppsRow.Children.Add(MakeMoreChip(_apps.Count - 3));
        }
    }

    private Border MakeAppChip(RunningApp app)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        ImageSource? icon = GetAppIcon(app.ExePath);
        if (icon is not null)
        {
            row.Children.Add(new Image
            {
                Source = icon,
                Width = 14,
                Height = 14,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = app.DisplayName,
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 110,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        string metric = app.CpuPercent >= 0.05
            ? app.CpuPercent.ToString("0") + "%"
            : app.MemoryMb.ToString("0") + " МБ";

        row.Children.Add(new TextBlock
        {
            Text = metric,
            FontSize = 11,
            Foreground = ChipSubBrush,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = ChipBrush,
            Padding = new Thickness(9, 0, 9, 0),
            Margin = new Thickness(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"{app.MemoryMb:0} МБ памяти",
            Child = row,
        };
    }

    private Border MakeMoreChip(int extra)
    {
        var chip = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = ChipMoreBrush,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "+" + extra,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        chip.MouseLeftButtonDown += (_, _) =>
        {
            _appsExpanded = true;
            RenderApps();
        };
        return chip;
    }

    private Border MakeCollapseChip()
    {
        var chip = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = ChipMoreBrush,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 6, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = "−",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        chip.MouseLeftButtonDown += (_, _) =>
        {
            _appsExpanded = false;
            RenderApps();
        };
        return chip;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref ShFileInfo psFileInfo,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiSmallIcon = 0x1;

    private ImageSource? GetAppIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (_iconCache.TryGetValue(path, out ImageSource? cached))
        {
            return cached;
        }

        try
        {
            string file = path;
            int index = 0;
            int comma = path.LastIndexOf(',');
            if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out int parsedIndex))
            {
                file = path[..comma];
                index = parsedIndex;
            }

            // 1) приватное извлечение — даёт иконку до 256px (чёткость без «пикселей»)
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                uint extracted = PrivateExtractIcons(file, (uint)index, 256, 256, out hIcon, out _, 1, 0);
                if ((extracted == 0 || hIcon == IntPtr.Zero))
                {
                    extracted = PrivateExtractIcons(file, (uint)index, 48, 48, out hIcon, out _, 1, 0);
                }

                if (extracted > 0 && hIcon != IntPtr.Zero)
                {
                    BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    _iconCache[path] = source;
                    return source;
                }
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                {
                    DestroyIcon(hIcon);
                }
            }

            // 2) фолбэк: SHGetFileInfo без SMALLICON — 32px вместо 16px
            var info = new ShFileInfo();
            SHGetFileInfo(file, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon);
            if (info.hIcon != IntPtr.Zero)
            {
                BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                DestroyIcon(info.hIcon);
                _iconCache[path] = source;
                return source;
            }
        }
        catch
        {
            // иконка не обязательна
        }

        return null;
    }

    /// <summary>Публичный кэш иконок — используется лаунчером.</summary>
    public ImageSource? GetCachedIcon(string? path) => GetAppIcon(path);

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ---------- медиа ----------

    private void OnClockTick(object? sender, EventArgs e)
    {
        UpdateActiveApp();

        _media.RefreshTimeline();

        TimeSpan position = _media.Position;
        TimeSpan duration = _media.Duration;

        TimeText.Text = FormatTime(position);
        TimeSpan remaining = duration > position ? duration - position : TimeSpan.Zero;
        RemainText.Text = "-" + FormatTime(remaining);

        double ratio = duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalMilliseconds / duration.TotalMilliseconds, 0, 1)
            : 0;

        double trackWidth = SliderTrack.ActualWidth;
        ProgressFill.Width = Math.Round(ratio * trackWidth, 1);
        Thumb.Visibility = trackWidth > 10 ? Visibility.Visible : Visibility.Collapsed;
        Thumb.Margin = new Thickness(Math.Round(ratio * trackWidth - 5.5, 1), 0, 0, 0);

        // часы
        DateTime now = DateTime.Now;
        WeatherTime.Text = now.ToString("HH:mm");
        WeatherDate.Text = now.ToString("dddd, d MMMM", Ru);

        UpdateTimerDisplays();

        if (_gaming is { InGame: true })
        {
            GamingTimer.Text = $"Сессия: {FormatTime(_gaming.SessionDuration)}";
        }
    }

    private void Slider_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        double trackWidth = SliderTrack.ActualWidth;
        if (trackWidth <= 10 || _media.Duration <= TimeSpan.Zero)
        {
            return;
        }

        double ratio = Math.Clamp(e.GetPosition(SliderTrack).X / trackWidth, 0, 1);
        _ = _media.SeekAsync(TimeSpan.FromMilliseconds(ratio * _media.Duration.TotalMilliseconds));
    }

    private void OnSessionChanged()
    {
        if (!IsLoaded)
        {
            return;
        }

        bool hasSession = _media.HasSession;
        bool musicActive = !ForceAppHeader && hasSession && !string.IsNullOrWhiteSpace(_media.Title);
        _musicActive = musicActive;

        if (musicActive)
        {
            if (!string.Equals(_titleTarget, _media.Title, StringComparison.Ordinal))
            {
                _titleTarget = _media.Title;
                RefreshTitle();
                AnimateTrackChange();
            }

            ArtistText.Text = _media.ArtistLine;

            if (_media.Cover is not null)
            {
                if (!ReferenceEquals(CoverImage.Source, _media.Cover))
                {
                    FadeCover(_media.Cover);
                }
            }
            else
            {
                ShowPlaceholderCover();
            }
        }
        else
        {
            // музыки нет — показываем активное приложение
            UpdateActiveApp(force: true);
            ApplyAppHeader();
        }

        bool playing = _media.IsPlaying;
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;

        PrevButton.IsEnabled = hasSession;
        PlayButton.IsEnabled = hasSession;
        NextButton.IsEnabled = hasSession;

        UpdateSourcesText();
    }

    private void AnimateTrackChange()
    {
        if (_view == View.Settings) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        TitleText.BeginAnimation(UIElement.OpacityProperty, null);
        TitleText.RenderTransform = new TranslateTransform(0, 4);
        TitleText.Opacity = 0.25;
        TitleText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        ((TranslateTransform)TitleText.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });

        if (CoverImage.Source is not null)
        {
            CoverImage.Opacity = 0.7;
            CoverImage.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = ease,
            });
        }
    }

    // ---------- активное окно (когда музыки нет) ----------

    /// <summary>Читает foreground-окно; при смене обновляет шапку (если не играет музыка).</summary>
    private void UpdateActiveApp(bool force = false)
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            GetWindowThreadProcessId(hwnd, out uint pidValue);
            int pid = (int)pidValue;
            if (pid <= 0 || pid == Environment.ProcessId)
            {
                return; // собственное окно не считаем
            }

            var className = new StringBuilder(128);
            GetClassName(hwnd, className, className.Capacity);
            if (ShellClasses.Contains(className.ToString()))
            {
                return; // рабочий стол, панель задач, всплывающие окна — не показываем
            }

            var titleBuilder = new StringBuilder(512);
            GetWindowText(hwnd, titleBuilder, titleBuilder.Capacity);
            string title = titleBuilder.ToString();

            if (!force && pid == _activePid && string.Equals(title, _activeTitle, StringComparison.Ordinal))
            {
                return; // ничего не поменялось
            }

            string processName;
            string? path = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                processName = process.ProcessName;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // нет доступа к чужому процессу
                }
            }
            catch
            {
                return; // процесс завершился
            }

            _activePid = pid;
            _activePath = path;
            _activeName = GetFriendlyName(path, processName);
            _activeTitle = title;

            if (!_musicActive)
            {
                ApplyAppHeader();
            }
        }
        catch
        {
            // определение окна не критично
        }
    }

    /// <summary>Шапка без музыки: иконка приложения + имя + название окна.</summary>
    private void ApplyAppHeader()
    {
        if (_musicActive)
        {
            return;
        }

        string name = string.IsNullOrEmpty(_activeName) ? "Ничего не играет" : _activeName;
        if (!string.Equals(_titleTarget, name, StringComparison.Ordinal))
        {
            _titleTarget = name;
            RefreshTitle();
        }

        string windowTitle = _activeTitle ?? string.Empty;
        if (windowTitle.Length > 70)
        {
            windowTitle = windowTitle[..67] + "…";
        }

        if (!string.Equals(ArtistText.Text, windowTitle, StringComparison.Ordinal))
        {
            ArtistText.Text = windowTitle;
        }

        ImageSource? icon = string.IsNullOrEmpty(_activePath) ? null : GetAppIcon(_activePath);
        if (icon is not null)
        {
            if (_appCoverPid != _activePid || !ReferenceEquals(CoverImage.Source, icon))
            {
                _appCoverPid = _activePid;
                FadeCover(icon);
            }
        }
        else if (_appCoverPid != _activePid)
        {
            _appCoverPid = _activePid;
            ShowPlaceholderCover();
        }
    }

    private void FadeCover(ImageSource source)
    {
        CoverImage.Source = source;
        CoverImage.Visibility = Visibility.Visible;
        CoverPlaceholder.Visibility = Visibility.Collapsed;
        CoverImage.Opacity = 0;
        CoverImage.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void ShowPlaceholderCover()
    {
        CoverImage.BeginAnimation(UIElement.OpacityProperty, null);
        CoverImage.Source = null;
        CoverImage.Visibility = Visibility.Collapsed;
        CoverPlaceholder.Visibility = Visibility.Visible;
    }

    private string GetFriendlyName(string? path, string fallback)
    {
        if (string.IsNullOrEmpty(path))
        {
            return fallback;
        }

        if (_friendlyNameCache.TryGetValue(path, out string? cached))
        {
            return cached;
        }

        string name = fallback;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
            {
                name = description;
            }
        }
        catch
        {
            // описание недоступно — оставляем имя процесса
        }

        _friendlyNameCache[path] = name;
        return name;
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Play_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();

    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- автозапуск ----------

    private static readonly string StartupShortcut = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "MusicOverlay.lnk");

    private static void ApplyAutostart(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (File.Exists(StartupShortcut))
                {
                    return;
                }

                Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null)
                {
                    return;
                }

                object? shell = Activator.CreateInstance(shellType);
                if (shell is null)
                {
                    return;
                }

                object? link = shell.GetType().InvokeMember(
                    "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { StartupShortcut });
                if (link is null)
                {
                    return;
                }

                string exe = Environment.ProcessPath ?? string.Empty;
                link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, new object[] { exe });
                link.GetType().InvokeMember(
                    "WorkingDirectory",
                    BindingFlags.SetProperty,
                    null,
                    link,
                    new object[] { Path.GetDirectoryName(exe) ?? string.Empty });
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

                // отпускаем COM, иначе файл .lnk останется заблокированным
                Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
            else if (File.Exists(StartupShortcut))
            {
                File.Delete(StartupShortcut);
            }
        }
        catch
        {
            // автозапуск не критичен
        }
    }

    // ---------- Win32 ----------

    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>Извлекает иконку заданного размера (до 256px) — для чётких плиток.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(
        string lpszFile,
        uint nIconIndex,
        uint cxIcon,
        uint cyIcon,
        out IntPtr phicon,
        out uint piconid,
        uint nIcons,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private double _idlePhase;
}
