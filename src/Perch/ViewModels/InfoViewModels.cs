using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Threading;
using Perch.Services;
using Forms = System.Windows.Forms;

namespace Perch.ViewModels;

public sealed record WeatherDay(string Label, string Glyph, string Text, string Range, string Rain);

// Weather from Open-Meteo (free, no key). The city comes from settings and is geocoded once.
public sealed class WeatherViewModel : Observable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private readonly Store store;
    private readonly AlertQueue alerts;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(30) };
    private readonly HashSet<string> alerted = [];
    private string? geocoded;
    private double latitude, longitude;
    private bool loading;

    public bool HasData { get; private set; }
    public string Place { get; private set; } = "";
    public string Temperature { get; private set; } = "--°";
    public string Glyph { get; private set; } = "☁";
    public string Condition { get; private set; } = "";
    public string Details { get; private set; } = "";
    public string HighLow { get; private set; } = "";
    public string Status { get; private set; } = "";
    public string ShortText => HasData ? $"{Temperature} {Condition}" : Loc.T("Info.Weather");
    public ObservableCollection<WeatherDay> Days { get; } = [];
    public ICommand Refresh { get; }

    public WeatherViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        Refresh = new RelayCommand(_ => _ = RefreshAsync());
        timer.Tick += (_, _) => _ = RefreshAsync();
        timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var s = store.State.Settings;
        await UpdateDeviceLocationAsync(s);
        var city = s.City.Trim();
        var picked = s.Latitude is not null && s.Longitude is not null;
        if (!picked && city.Length == 0)
        {
            HasData = false;
            Status = Loc.T("Info.NoCity");
            Changed(string.Empty);
            return;
        }
        if (loading) return;
        loading = true;
        Status = Loc.T("Info.Updating");
        Changed(nameof(Status));
        try
        {
            if (picked)
            {
                latitude = s.Latitude!.Value;
                longitude = s.Longitude!.Value;
                Place = s.CityName;
            }
            else if (!string.Equals(city, geocoded, StringComparison.OrdinalIgnoreCase))
            {
                using var geo = JsonDocument.Parse(await Http.GetStringAsync($"https://geocoding-api.open-meteo.com/v1/search?count=1&language={Loc.Culture.TwoLetterISOLanguageName}&name={Uri.EscapeDataString(city)}"));
                if (!geo.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                {
                    HasData = false;
                    Status = Loc.F("Info.CityNotFound", city);
                    return;
                }
                var place = results[0];
                latitude = place.GetProperty("latitude").GetDouble();
                longitude = place.GetProperty("longitude").GetDouble();
                Place = place.TryGetProperty("name", out var name) ? name.GetString() ?? city : city;
                geocoded = city;
                // From now on this is a stored, typed place rather than text to be looked up again.
                s.CityName = Place; s.Latitude = latitude; s.Longitude = longitude;
                store.Touch();
            }
            var unit = s.Fahrenheit ? "&temperature_unit=fahrenheit" : "";
            var url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=5{unit}");
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var current = doc.RootElement.GetProperty("current");
            var temp = current.GetProperty("temperature_2m").GetDouble();
            var code = current.GetProperty("weather_code").GetInt32();
            var wind = current.GetProperty("wind_speed_10m").GetDouble();
            (Glyph, Condition) = Describe(code);
            Temperature = $"{temp:0}°";
            Details = Loc.F("Info.Details", Round(current.GetProperty("apparent_temperature").GetDouble()), Round(current.GetProperty("relative_humidity_2m").GetDouble()), Round(wind));

            var daily = doc.RootElement.GetProperty("daily");
            var dates = daily.GetProperty("time");
            var codes = daily.GetProperty("weather_code");
            var highs = daily.GetProperty("temperature_2m_max");
            var lows = daily.GetProperty("temperature_2m_min");
            var rain = daily.GetProperty("precipitation_probability_max");
            Days.Clear();
            for (var i = 0; i < dates.GetArrayLength(); i++)
            {
                var date = DateTime.Parse(dates[i].GetString()!, CultureInfo.InvariantCulture);
                var (glyph, text) = Describe(Number(codes[i]) is { } c ? (int)c : 0);
                var label = i == 0 ? Loc.T("Common.Today") : i == 1 ? Loc.T("Common.Tomorrow") : date.ToString("ddd", Loc.Culture);
                var chance = Number(rain[i]) is { } r ? $"{r:0}%" : "";
                Days.Add(new(label, glyph, text, $"{Number(lows[i]):0}° / {Number(highs[i]):0}°", chance));
            }
            var high = Number(highs[0]) ?? temp;
            var low = Number(lows[0]) ?? temp;
            HighLow = $"{low:0}° / {high:0}°";
            HasData = true;
            Status = Loc.F("Info.UpdatedAt", DateTime.Now.ToString("HH:mm"));
            if (s.WeatherAlerts) CheckAlerts(Number(codes[0]) is { } today ? (int)today : code, high, low, wind, s.Fahrenheit);
        }
        catch
        {
            Status = Loc.T(HasData ? "Info.UpdateFailed" : "Info.LoadFailed");
        }
        finally
        {
            loading = false;
            Changed(string.Empty);
        }
    }

    private DateTime lastLocated = DateTime.MinValue;
    private bool nameUpgradeTried;

    // "Follow my location": the device position is re-read at most every 25 minutes and the place renamed when it moved.
    // A place saved earlier as bare coordinates ("Current location (43.26°, -79.92°)") gets its city name here too.
    private async Task UpdateDeviceLocationAsync(Settings s)
    {
        var language = Loc.Culture.TwoLetterISOLanguageName;
        if (s.UseDeviceLocation && DateTime.Now - lastLocated > TimeSpan.FromMinutes(25))
        {
            lastLocated = DateTime.Now;
            if (await DeviceLocation.GetAsync() is { } here)
            {
                var moved = s.Latitude is not { } lat || s.Longitude is not { } lon || PlaceSearch.KilometersBetween(lat, lon, here.Latitude, here.Longitude) > 3;
                if (moved || string.IsNullOrWhiteSpace(s.CityName))
                {
                    s.Latitude = here.Latitude; s.Longitude = here.Longitude;
                    s.CityName = await PlaceSearch.ReverseAsync(here.Latitude, here.Longitude, language) ?? $"{Loc.T("Set.CurrentLocation")} ({PlaceSearch.Coordinates(here.Latitude, here.Longitude)})";
                    store.Touch();
                }
            }
        }
        if (!nameUpgradeTried && s.CityName.Contains('°') && s.Latitude is { } la && s.Longitude is { } lo)
        {
            nameUpgradeTried = true;
            if (await PlaceSearch.ReverseAsync(la, lo, language) is { } name) { s.CityName = name; store.Touch(); }
        }
    }

    private static int Round(double value) => (int)Math.Round(value);

    // Called after settings or language change: the city is geocoded again (place names are localized).
    public void SettingsChanged() { geocoded = null; _ = RefreshAsync(); }
    public void Relocalize() => SettingsChanged();

    private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    // Open-Meteo has no official warnings, so alerts come from today's forecast: storms, heavy rain or snow, strong wind, heat and frost.
    private void CheckAlerts(int code, double high, double low, double wind, bool fahrenheit)
    {
        if (fahrenheit) { high = (high - 32) * 5 / 9; low = (low - 32) * 5 / 9; }
        var today = DateTime.Today.ToString("yyyyMMdd");
        void Once(string kind, string text)
        {
            if (alerted.Add(today + kind)) alerts.Show(AlertQueue.Warning, text);
        }
        if (code is 95 or 96 or 99) Once("storm", Loc.T("Info.AlertStorm"));
        else if (code is 65 or 82) Once("rain", Loc.T("Info.AlertRain"));
        else if (code is 75 or 86) Once("snow", Loc.T("Info.AlertSnow"));
        if (wind >= 50) Once("wind", Loc.F("Info.AlertWind", Round(wind)));
        if (high >= 35) Once("heat", Loc.T("Info.AlertHeat"));
        if (low <= -10) Once("cold", Loc.T("Info.AlertCold"));
    }

    internal static (string Glyph, string Text) Describe(int code)
    {
        var (glyph, key) = code switch
        {
            0 => ("☀", "Clear"),
            1 => ("☀", "MostlyClear"),
            2 => ("⛅", "PartlyCloudy"),
            3 => ("☁", "Overcast"),
            45 or 48 => ("☁", "Fog"),
            51 or 53 or 55 => ("☂", "Drizzle"),
            56 or 57 => ("☂", "FreezingDrizzle"),
            61 => ("☂", "LightRain"),
            63 => ("☔", "Rain"),
            65 => ("☔", "HeavyRain"),
            66 or 67 => ("☔", "FreezingRain"),
            71 => ("❄", "LightSnow"),
            73 => ("❄", "Snow"),
            75 => ("❄", "HeavySnow"),
            77 => ("❄", "SnowGrains"),
            80 or 81 => ("☂", "Showers"),
            82 => ("☔", "HeavyShowers"),
            85 or 86 => ("❄", "SnowShowers"),
            95 => ("⚡", "Thunderstorm"),
            96 or 99 => ("⚡", "Hail"),
            _ => ("☁", "Unknown"),
        };
        return (glyph, Loc.T("Weather." + key));
    }
}

// Battery and network throughput, sampled once a second.
public sealed class SystemStatusViewModel : Observable
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Store store;
    private readonly AlertQueue alerts;
    private long lastReceived = -1, lastSent;
    private DateTime lastSample;
    private bool? wasCharging;
    private int lowWarned = 101;

    public bool HasBattery { get; private set; }
    public bool Charging { get; private set; }
    public int Percent { get; private set; }
    public string BatteryText => HasBattery ? $"{Percent}%" : Loc.T("Info.NoBattery");
    public string BatteryState => Loc.T(!HasBattery ? "Info.Mains" : Charging ? (Percent >= 100 ? "Info.Full" : "Info.Charging") : "Info.OnBattery");
    public string BatteryGlyph => !HasBattery ? "" : Charging ? "" : Percent switch
    {
        >= 90 => "",
        >= 60 => "",
        >= 30 => "",
        >= 10 => "",
        _ => "",
    };
    public string Download { get; private set; } = "0 B/s";
    public string Upload { get; private set; } = "0 B/s";
    public string NetText => $"↓ {Download}  ↑ {Upload}";

    public SystemStatusViewModel(Store store, AlertQueue alerts)
    {
        this.store = store;
        this.alerts = alerts;
        timer.Tick += (_, _) => Sample();
        timer.Start();
        Sample();
    }

    private void Sample()
    {
        SampleNetwork();
        SampleBattery();
        Changed(string.Empty);
    }

    private void SampleNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = adapter.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch { return; }
        var now = DateTime.Now;
        if (lastReceived >= 0)
        {
            var seconds = Math.Max(0.2, (now - lastSample).TotalSeconds);
            Download = Rate(Math.Max(0, received - lastReceived) / seconds);
            Upload = Rate(Math.Max(0, sent - lastSent) / seconds);
        }
        lastReceived = received; lastSent = sent; lastSample = now;
    }

    private static string Rate(double bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024 / 1024:0.0} MB/s",
        >= 1024 => $"{bytes / 1024:0} KB/s",
        _ => $"{bytes:0} B/s",
    };

    private void SampleBattery()
    {
        var power = Forms.SystemInformation.PowerStatus;
        HasBattery = (power.BatteryChargeStatus & Forms.BatteryChargeStatus.NoSystemBattery) == 0
            && power.BatteryChargeStatus != Forms.BatteryChargeStatus.Unknown;
        Charging = power.PowerLineStatus == Forms.PowerLineStatus.Online;
        Percent = Math.Clamp((int)Math.Round(power.BatteryLifePercent * 100), 0, 100);
        if (!HasBattery) return;

        var s = store.State.Settings;
        if (wasCharging is { } before && before != Charging && s.BatteryAlerts)
            alerts.Show(AlertQueue.Bolt, Loc.F(Charging ? "Info.PluggedIn" : "Info.Unplugged", Percent));
        wasCharging = Charging;

        if (Charging) lowWarned = 101;
        else if (s.BatteryAlerts && (Percent <= 10 && lowWarned > 10 || Percent <= 20 && lowWarned > 20))
        {
            lowWarned = Percent <= 10 ? 10 : 20;
            alerts.Show(AlertQueue.Warning, Loc.F("Info.LowBattery", Percent));
        }
    }
}
