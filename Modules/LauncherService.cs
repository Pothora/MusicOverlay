using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MusicOverlay.Modules;

/// <summary>Приложение-ярлык для быстрого запуска.</summary>
public sealed record LauncherApp(string Name, string Path, string Arguments, string? IconPath, bool Favorite);

/// <summary>
/// Сбор ярлыков «Пуск», избранное, запуск и иконки (SHGetFileInfo → BitmapSource).
/// </summary>
public sealed class LauncherService
{
    private const int MaxApps = 300;
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiSmallIcon = 0x00000001;

    private readonly List<LauncherApp> _all = new();
    private readonly List<string> _favorites = new();
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LauncherApp> Apps { get; private set; } = Array.Empty<LauncherApp>();

    public IReadOnlyList<string> FavoritePaths { get; private set; } = Array.Empty<string>();

    public event Action? Changed;

    /// <summary>Пересканировать ярлыки «Пуск» и применить список избранного.</summary>
    public void Load(IEnumerable<string> favoritePaths)
    {
        try
        {
            _favorites.Clear();
            if (favoritePaths is not null)
            {
                foreach (var path in favoritePaths)
                {
                    if (!string.IsNullOrWhiteSpace(path) &&
                        !_favorites.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        _favorites.Add(path);
                    }
                }
            }

            _all.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // один COM-объект на весь проход — существенно быстрее, чем на каждый .lnk
            object? shell = CreateShell();
            if (shell is null)
            {
                Rebuild();
                RaiseChanged();
                return;
            }

            try
            {
                foreach (var lnk in EnumerateShortcuts())
                {
                    if (_all.Count >= MaxApps)
                    {
                        break;
                    }

                    var info = ReadShortcut(shell, lnk);
                    if (info is null || string.IsNullOrWhiteSpace(info.Target))
                    {
                        continue;
                    }

                    AddShortcut(info, lnk, seen);
                }
            }
            finally
            {
                ReleaseCom(shell);
            }

            Rebuild();
            RaiseChanged();
        }
        catch
        {
            // список останется прежним
        }
    }

    private void AddShortcut(ShortcutInfo info, string lnk, HashSet<string> seen)
    {
        try
        {
            // дубликати по TargetPath+Arguments
            if (!seen.Add(info.Target + "\u0001" + info.Arguments))
            {
                return;
            }

            string name = Path.GetFileNameWithoutExtension(lnk);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            string iconPath = CleanIconLocation(info.IconLocation) ?? info.Target;

            // если путь не дал иконку — пробуем путь к самому ярлыку
            if (GetIcon(iconPath) is null && GetIcon(lnk) is not null)
            {
                iconPath = lnk;
            }

            bool favorite = _favorites.Contains(lnk, StringComparer.OrdinalIgnoreCase);
            _all.Add(new LauncherApp(name, lnk, info.Arguments, iconPath, favorite));
        }
        catch
        {
            // один битый ярлык не должен ломать весь обход
        }
    }

    /// <summary>Запуск приложения. Для .lnk запускается сам ярлык.</summary>
    public void Launch(LauncherApp app)
    {
        try
        {
            if (app is null || string.IsNullOrWhiteSpace(app.Path))
            {
                return;
            }

            var info = new ProcessStartInfo
            {
                FileName = app.Path,
                UseShellExecute = true,
            };

            // у ярлыка аргументы уже зашиты внутри — дублировать не нужно
            if (!app.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(app.Arguments))
            {
                info.Arguments = app.Arguments;
            }

            Process.Start(info);
        }
        catch
        {
            // цель недоступна или запрещена — молча игнорируем
        }
    }

    public void ToggleFavorite(LauncherApp app)
    {
        try
        {
            if (app is null)
            {
                return;
            }

            int index = _all.FindIndex(x =>
                ReferenceEquals(x, app) ||
                (string.Equals(x.Path, app.Path, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(x.Arguments, app.Arguments, StringComparison.Ordinal)));
            if (index < 0)
            {
                return;
            }

            var old = _all[index];
            bool favorite = !old.Favorite;
            _all[index] = new LauncherApp(old.Name, old.Path, old.Arguments, old.IconPath, favorite);

            if (favorite)
            {
                if (!_favorites.Contains(old.Path, StringComparer.OrdinalIgnoreCase))
                {
                    _favorites.Add(old.Path);
                }
            }
            else
            {
                _favorites.RemoveAll(p => string.Equals(p, old.Path, StringComparison.OrdinalIgnoreCase));
            }

            Rebuild();
            RaiseChanged();
        }
        catch
        {
            // пересборка не удалась — оставляем как есть
        }
    }

    // ---------------- Сборка списка ----------------

    private void Rebuild()
    {
        var ordered = new List<LauncherApp>(_all.Count);
        ordered.AddRange(_all.Where(x => x.Favorite)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));
        ordered.AddRange(_all.Where(x => !x.Favorite)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase));

        Apps = ordered;
        FavoritePaths = new List<string>(_favorites);
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

    // ---------------- Ярлыки ----------------

    private sealed record ShortcutInfo(string Target, string Arguments, string IconLocation);

    private static IEnumerable<string> EnumerateShortcuts()
    {
        foreach (var root in StartMenuRoots())
        {
            foreach (var file in SafeFiles(root))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        var roots = new List<string>(3);

        try
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(user))
            {
                roots.Add(Path.Combine(user, "Microsoft", "Windows", "Start Menu", "Programs"));
            }
        }
        catch
        {
            // пропускаем
        }

        try
        {
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(common))
            {
                roots.Add(Path.Combine(common, "Microsoft", "Windows", "Start Menu", "Programs"));
            }
        }
        catch
        {
            // пропускаем
        }

        roots.Add(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs");
        return roots;
    }

    /// <summary>Рекурсивный обход *.lnk с перехватом правовых/IO-ошибок.</summary>
    private static IEnumerable<string> SafeFiles(string root)
    {
        var dirs = new Stack<string>();
        dirs.Push(root);

        while (dirs.Count > 0)
        {
            string dir = dirs.Pop();

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk");
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                dirs.Push(sub);
            }
        }
    }

    /// <summary>Создаёт общий COM-объект WScript.Shell.</summary>
    private static object? CreateShell()
    {
        try
        {
            var progId = Type.GetTypeFromProgID("WScript.Shell");
            return progId is null ? null : Activator.CreateInstance(progId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Чтение .lnk через COM WScript.Shell.</summary>
    private static ShortcutInfo? ReadShortcut(object shell, string lnkPath)
    {
        object? shortcut = null;

        try
        {
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut is null)
            {
                return null;
            }

            return new ShortcutInfo(
                ComString(shortcut, "TargetPath"),
                ComString(shortcut, "Arguments"),
                ComString(shortcut, "IconLocation"));
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseCom(shortcut);
        }
    }

    private static string ComString(object comObject, string propertyName)
    {
        try
        {
            object? value = comObject.GetType().InvokeMember(
                propertyName, BindingFlags.GetProperty, null, comObject, null);
            return Convert.ToString(value) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch
        {
            // объект уже мог быть освобождён
        }
    }

    /// <summary>"path.ico,0" → "path.ico".</summary>
    private static string? CleanIconLocation(string iconLocation)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(iconLocation))
            {
                return null;
            }

            string path = iconLocation.Trim();
            int comma = path.LastIndexOf(',');
            if (comma >= 0)
            {
                path = path[..comma];
            }

            path = path.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- Иконки ----------------

    private ImageSource? GetIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (_icons.TryGetValue(path, out var cached))
            {
                return cached;
            }

            var icon = LoadIcon(path);
            if (icon is not null)
            {
                _icons[path] = icon;
            }

            return icon;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? LoadIcon(string path)
    {
        IntPtr hIcon = IntPtr.Zero;

        try
        {
            var info = new ShellFileInfo();
            var result = SHGetFileInfo(
                path, 0, ref info, (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiSmallIcon);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            {
                return null;
            }

            hIcon = info.hIcon;

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero)
            {
                try
                {
                    DestroyIcon(hIcon);
                }
                catch
                {
                    // утечка одного хэндла не критична
                }
            }
        }
    }

    // ---------------- P/Invoke ----------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr SHGetFileInfoW(
        string pszPath, uint dwFileAttributes, ref ShellFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private static IntPtr SHGetFileInfo(
        string path, uint attributes, ref ShellFileInfo info, uint infoSize, uint flags)
    {
        return SHGetFileInfoW(path, attributes, ref info, infoSize, flags);
    }
}
