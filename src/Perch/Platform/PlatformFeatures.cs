namespace Perch.Platform;

public static class PlatformFeatures
{
    public static bool WindowsIntegration => OperatingSystem.IsWindows();
}
