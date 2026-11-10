using System.Security.Cryptography;
using System.Text;

namespace Shmoice.Services;

/// <summary>
/// Encrypts the API key with Windows DPAPI so it's only readable by the current Windows user,
/// instead of sitting in commands.json as plain text.
/// </summary>
public static class SecretStore
{
    public static string Protect(string secret) =>
        string.IsNullOrEmpty(secret) ? "" :
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    public static string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret)) return "";
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return ""; // saved by another user/machine, or corrupted — treat as not set
        }
    }
}
