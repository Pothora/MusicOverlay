using System.IO;
using System.Text.Json;

namespace MusicOverlay.Core;

/// <summary>
/// Единое хранилище настроек в JSON (%LOCALAPPDATA%\MusicOverlay\config.json)
/// с миграцией из старого config.txt.
/// </summary>
public static class SettingsStore
{
    public sealed class SettingsData
    {
        // общее
        public string View { get; set; } = "mini"; // mini | full
        public string Tab { get; set; } = "music";
        public bool Autostart { get; set; }

        // модули
        public bool Music { get; set; } = true;
        public bool System { get; set; } = true;
        public bool Notifications { get; set; } = true;
        public bool Audio { get; set; } = true;
        public bool Clipboard { get; set; } = true;
        public bool Actions { get; set; } = true;
        public bool Weather { get; set; } = true;
        public bool Gaming { get; set; } = true;
        public bool Launcher { get; set; } = true;
        public bool RunningApps { get; set; } = true;
        public bool Typewriter { get; set; } = true;
        public bool Viz { get; set; } = true;

        // поведение
        public bool Dnd { get; set; }
        public bool AutoDndOnGame { get; set; } = true;
        public int HistoryLimit { get; set; } = 30;

        // внешний вид
        public string Accent { get; set; } = "#0FA3E0";
        public string TextColor { get; set; } = "#FFFFFFFF";
        public string BackgroundColor { get; set; } = "#FF111115";
        public double Opacity { get; set; } = 0.95;
        public double Radius { get; set; } = 16;
        public bool Blur { get; set; } = true;
        public double BlurStrength { get; set; } = 18;
        public bool Bubbles { get; set; } = true;
        public string DockEdge { get; set; } = "None"; // None | Top | Bottom | Left | Right
        public bool DragEnabled { get; set; }
        public double WindowLeft { get; set; } = -1;
        public double WindowTop { get; set; } = -1;

        // данные
        public List<string> FavoriteApps { get; set; } = new();
    }

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MusicOverlay");

    private static readonly string LegacyPath = Path.Combine(Dir, "config.txt");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string ConfigPath => Path.Combine(Dir, "config.json");

    public static SettingsData Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(ConfigPath), Options);
                if (loaded is not null)
                {
                    Current = loaded;
                }

                return;
            }

            MigrateLegacy();
        }
        catch
        {
            // повреждённый конфиг — используем значения по умолчанию
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, Options));
        }
        catch
        {
            // запись не критична
        }
    }

    private static void MigrateLegacy()
    {
        try
        {
            if (!File.Exists(LegacyPath))
            {
                return;
            }

            string[] parts = File.ReadAllText(LegacyPath).Split('|');
            Current.View = parts[0].Trim() == "full" ? "full" : "mini";

            if (parts.Length < 2)
            {
                Save();
                return;
            }

            foreach (string pair in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=');
                if (kv.Length != 2)
                {
                    continue;
                }

                bool value = kv[1].Trim() == "1";
                switch (kv[0].Trim())
                {
                    case "notif":
                        Current.Notifications = value;
                        break;
                    case "apps":
                        Current.RunningApps = value;
                        break;
                    case "type":
                        Current.Typewriter = value;
                        break;
                    case "viz":
                        Current.Viz = value;
                        break;
                }
            }

            Save();
        }
        catch
        {
            // миграция не критична
        }
    }
}
