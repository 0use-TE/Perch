using System.Security.Cryptography;
using System.Text;

namespace Perch.Services;

// Keeps secrets such as API keys protected on disk. On Windows they are encrypted with DPAPI for the signed-in
// user (only this account on this machine can read them back); elsewhere they are only encoded, and a platform
// keyring (libsecret, Keychain) should replace that when Perch gets a Linux or macOS build.
public static class Secrets
{
    private const string Dpapi = "dpapi:", Plain = "b64:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Perch.Secrets.v1");

    public static string Protect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var bytes = Encoding.UTF8.GetBytes(text);
        if (OperatingSystem.IsWindows())
            try { return Dpapi + Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)); }
            catch (CryptographicException) { /* fall through to the plain encoding */ }
        return Plain + Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            if (stored.StartsWith(Dpapi, StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Dpapi.Length..]), Entropy, DataProtectionScope.CurrentUser));
            if (stored.StartsWith(Plain, StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[Plain.Length..]));
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { /* another account or damaged: treat as not set */ }
        return "";
    }
}
