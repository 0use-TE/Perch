using Windows.Devices.Geolocation;

namespace Perch.Services;

// The device's position from Windows Location (needs Settings > Privacy > Location to allow desktop apps).
// Nothing is sent anywhere except the coordinates to Open-Meteo for the forecast.
public static class DeviceLocation
{
    public static async Task<(double Latitude, double Longitude)?> GetAsync()
    {
        try
        {
            if (await Geolocator.RequestAccessAsync() != GeolocationAccessStatus.Allowed) return null;
            var locator = new Geolocator { DesiredAccuracyInMeters = 5000 };
            var position = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(12));
            var point = position.Coordinate.Point.Position;
            return (point.Latitude, point.Longitude);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
