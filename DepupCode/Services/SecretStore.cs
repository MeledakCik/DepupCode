using System.Security.Cryptography;
using System.Text;

namespace DepupCode.Services;

public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Depup Code local secrets v1");

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return "";
        }
        catch (FormatException)
        {
            return "";
        }
    }
}
