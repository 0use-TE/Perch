namespace Perch.Platform.Linux;

internal static class LinuxKeyring
{
    internal const string Prefix = "secret-service:";
    private static readonly Dictionary<string, string> Cache = new();

    internal static string Protect(string text, string? previous)
    {
        var id = Reference(previous) ?? Guid.NewGuid().ToString("N");
        if (Cache.GetValueOrDefault(id) == text) return Prefix + id;
        var result = LinuxProcess.ReadAsync("secret-tool", ["store", "--label=Perch API key", "application", "net.0use.Perch", "id", id], text).GetAwaiter().GetResult();
        if (result == null) throw new InvalidOperationException("Cannot save the API key. Install libsecret and unlock a Secret Service keyring (GNOME Keyring or KeePassXC). The key has not been saved.");
        Cache[id] = text;
        return Prefix + id;
    }

    internal static string Unprotect(string stored)
    {
        var id = Reference(stored);
        if (id == null) return "";
        if (Cache.TryGetValue(id, out var cached)) return cached;
        var value = LinuxProcess.ReadAsync("secret-tool", ["lookup", "application", "net.0use.Perch", "id", id]).GetAwaiter().GetResult();
        if (value != null) Cache[id] = value;
        return value ?? "";
    }

    private static string? Reference(string? value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal)
        && Guid.TryParseExact(value[Prefix.Length..], "N", out _) ? value[Prefix.Length..] : null;
}
