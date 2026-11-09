namespace Perch.Services;

// Whether this environment can show the translucent ("frosted") material. When it cannot, Perch simply uses the solid
// look instead of showing something half-working. Each platform plugs in its own check (Windows: not a Remote Desktop
// session, and "Transparency effects" turned on); the answer is cached because the island asks on every frame.
public static class Translucency
{
    public static Func<bool>? Probe { get; set; }
    public static bool Supported { get; private set; } = true;
    public static void Refresh() => Supported = Probe?.Invoke() ?? true;
}
