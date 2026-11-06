using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Text.Json;

namespace Perch.Services;

public sealed record Place(string Name, string Region, string Country, double Latitude, double Longitude)
{
    // "Halifax, Nova Scotia, Canada"
    public string Label => string.Join(", ", new[] { Name, Region, Country }.Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
    public override string ToString() => Label;
}

// City suggestions from the Open-Meteo geocoding service (free, no key), so a place is picked from a list
// instead of typed blind and hoped for.
public static class PlaceSearch
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<List<Place>> SearchAsync(string text, string language, CancellationToken cancel)
    {
        var found = new List<Place>();
        text = text.Trim();
        if (text.Length < 2) return found;
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=8&language={language}&name={Uri.EscapeDataString(text)}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, cancel));
        if (!doc.RootElement.TryGetProperty("results", out var results)) return found;
        foreach (var item in results.EnumerateArray())
        {
            string Text(string name) => item.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
            found.Add(new Place(Text("name"), Text("admin1"), Text("country"), item.GetProperty("latitude").GetDouble(), item.GetProperty("longitude").GetDouble()));
        }
        return found;
    }

    // "Hamilton, Ontario, Canada" for a position, from OpenStreetMap's Nominatim (free, no key). Null when it can't be named.
    public static async Task<string?> ReverseAsync(double latitude, double longitude, string language, CancellationToken cancel = default)
    {
        try
        {
            var url = string.Create(CultureInfo.InvariantCulture, $"https://nominatim.openstreetmap.org/reverse?format=jsonv2&zoom=10&addressdetails=1&accept-language={language}&lat={latitude}&lon={longitude}");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Perch/1.0 (Windows desktop app)");
            using var response = await Http.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (!doc.RootElement.TryGetProperty("address", out var address)) return null;
            string Pick(params string[] names)
            {
                foreach (var name in names)
                    if (address.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text) return text;
                return "";
            }
            var city = Pick("city", "town", "village", "municipality", "county", "suburb", "city_district");
            if (city.Length == 0) return null;
            return new Place(city, Pick("state", "province", "region"), Pick("country"), latitude, longitude).Label;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static double KilometersBetween(double latA, double lonA, double latB, double lonB)
    {
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(latB - latA); var dLon = Rad(lonB - lonA);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(latA)) * Math.Cos(Rad(latB)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h)));
    }

    public static string Coordinates(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.00}°, {longitude:0.00}°");
}
