using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace MusicOverlay.Modules;

public sealed record WeatherNow(
    double TempC,
    double FeelsLikeC,
    string Description,
    string Emoji,
    double WindKmh,
    double Humidity,
    string City);

public sealed record WeatherDay(string DayName, double MaxC, double MinC, string Description, string Emoji);

/// <summary>
/// Погода: геолокация по IP (ipwho.is) + прогноз api.met.no (без ключей).
/// Выбраны эти провайдеры, т.к. wttr.in и open-meteo в этой сети недоступны.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(12),
    };

    private DispatcherTimer? _timer;
    private volatile bool _disposed;

    public WeatherNow? Now { get; private set; }

    public IReadOnlyList<WeatherDay>? Forecast { get; private set; }

    public event Action? Updated;

    static WeatherService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopIsland/1.0");
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    private async void OnTimerTick(object? sender, EventArgs e) => await RefreshAsync();

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // 1) координаты по IP
            double lat = 0, lon = 0;
            string city = string.Empty;
            try
            {
                using var geo = await Http.GetAsync("https://ipwho.is/");
                geo.EnsureSuccessStatusCode();
                using var geoJson = JsonDocument.Parse(await geo.Content.ReadAsStringAsync());
                var geoRoot = geoJson.RootElement;
                if (geoRoot.TryGetProperty("latitude", out var la) &&
                    geoRoot.TryGetProperty("longitude", out var lo))
                {
                    lat = la.GetDouble();
                    lon = lo.GetDouble();
                }

                if (geoRoot.TryGetProperty("city", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    city = c.GetString() ?? string.Empty;
                }
            }
            catch
            {
                return; // без координат погоду не получить
            }

            if (Math.Abs(lat) < 0.01 && Math.Abs(lon) < 0.01)
            {
                return;
            }

            // 2) прогноз от met.no (без ключей, нужен User-Agent)
            string url =
                $"https://api.met.no/weatherapi/locationforecast/2.0/compact?lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                $"&lon={lon.ToString(CultureInfo.InvariantCulture)}";

            using var response = await Http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var series = json.RootElement.GetProperty("properties").GetProperty("timeseries");

            double currentTemp = 0, currentWind = 0, currentHumidity = 0;
            string currentSymbol = string.Empty;
            bool haveCurrent = false;

            var order = new List<string>();
            var stats = new Dictionary<string, (double Min, double Max, string Symbol)>();

            foreach (var item in series.EnumerateArray())
            {
                string time = item.GetProperty("time").GetString() ?? string.Empty;
                if (time.Length < 10)
                {
                    continue;
                }

                string date = time[..10];
                var data = item.GetProperty("data");
                var details = data.GetProperty("instant").GetProperty("details");
                if (!details.TryGetProperty("air_temperature", out var t))
                {
                    continue;
                }

                double temp = t.GetDouble();

                if (!haveCurrent)
                {
                    haveCurrent = true;
                    currentTemp = temp;
                    if (details.TryGetProperty("wind_speed", out var w))
                    {
                        currentWind = w.GetDouble() * 3.6; // м/с → км/ч
                    }

                    if (details.TryGetProperty("relative_humidity", out var h))
                    {
                        currentHumidity = h.GetDouble();
                    }

                    currentSymbol = DaySymbol(data);
                }

                if (!stats.TryGetValue(date, out var agg))
                {
                    stats[date] = (temp, temp, DaySymbol(data));
                    order.Add(date);
                }
                else
                {
                    string symbol = agg.Symbol.Length > 0 ? agg.Symbol : DaySymbol(data);
                    stats[date] = (Math.Min(agg.Min, temp), Math.Max(agg.Max, temp), symbol);
                }
            }

            if (!haveCurrent)
            {
                return;
            }

            Now = new WeatherNow(
                currentTemp,
                FeelLike(currentTemp, currentWind / 3.6),
                Describe(currentSymbol),
                Emoji(currentSymbol),
                currentWind,
                currentHumidity,
                city);

            var days = new List<WeatherDay>();
            for (int i = 0; i < order.Count && days.Count < 3; i++)
            {
                string date = order[i];
                (double min, double max, string symbol) = stats[date];
                days.Add(new WeatherDay(DayName(date, i), max, min, Describe(symbol), Emoji(symbol)));
            }

            Forecast = days;
            Updated?.Invoke();
        }
        catch
        {
            // сеть недоступна — оставляем предыдущие значения
        }
    }

    /// <summary>Код погоды дня: берём сводку ближайших часов из записи.</summary>
    private static string DaySymbol(JsonElement data)
    {
        if (data.TryGetProperty("next_6_hours", out var n6) &&
            n6.TryGetProperty("summary", out var s6))
        {
            return s6.GetProperty("symbol_code").GetString() ?? string.Empty;
        }

        if (data.TryGetProperty("next_1_hours", out var n1) &&
            n1.TryGetProperty("summary", out var s1))
        {
            return s1.GetProperty("symbol_code").GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static double FeelLike(double temp, double windMs)
    {
        // простая формула ветрового холода для прохладной погоды
        if (temp <= 10 && windMs > 0.5)
        {
            double w = Math.Pow(windMs, 0.16);
            return 13.12 + 0.6215 * temp - 11.37 * w + 0.3965 * temp * w;
        }

        return temp;
    }

    private static string DayName(string isoDate, int index)
    {
        if (index == 0)
        {
            return "Сегодня";
        }

        if (DateTime.TryParseExact(
                isoDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date))
        {
            return CultureInfo.GetCultureInfo("ru-RU").DateTimeFormat.GetAbbreviatedDayName(date.DayOfWeek);
        }

        return isoDate;
    }

    /// <summary>Коды met.no: clearsky, fair, partlycloudy, cloudy, fog, rain*, snow*, sleet*, thunderstorm*.</summary>
    private static string Describe(string code)
    {
        code = code.ToLowerInvariant();
        if (code.Contains("thunder"))
        {
            return "Гроза";
        }

        if (code.Contains("snow"))
        {
            return "Снег";
        }

        if (code.Contains("sleet"))
        {
            return "Мокрый снег";
        }

        if (code.Contains("rain") || code.Contains("drizzle"))
        {
            return "Дождь";
        }

        if (code.Contains("fog"))
        {
            return "Туман";
        }

        if (code.Contains("clearsky"))
        {
            return "Ясно";
        }

        if (code.Contains("fair") || code.Contains("partly"))
        {
            return "Малооблачно";
        }

        if (code.Contains("cloudy"))
        {
            return "Облачно";
        }

        return "—";
    }

    private static string Emoji(string code)
    {
        code = code.ToLowerInvariant();
        if (code.Contains("thunder"))
        {
            return "⛈️";
        }

        if (code.Contains("snow"))
        {
            return "❄️";
        }

        if (code.Contains("sleet"))
        {
            return "🌨️";
        }

        if (code.Contains("rain") || code.Contains("drizzle"))
        {
            return "🌧️";
        }

        if (code.Contains("fog"))
        {
            return "🌫️";
        }

        if (code.Contains("clearsky"))
        {
            return "☀️";
        }

        if (code.Contains("fair") || code.Contains("partly"))
        {
            return "⛅";
        }

        if (code.Contains("cloudy"))
        {
            return "☁️";
        }

        return "☁️";
    }

    public void Dispose()
    {
        _disposed = true;
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        GC.SuppressFinalize(this);
    }
}
