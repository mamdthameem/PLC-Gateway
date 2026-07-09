using System.Security.Cryptography;
using System.Text;

namespace PlcApi.Services;

public static class PasswordHasher
{
    public static string Hash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool Verify(string input, string storedHash)
    {
        var inputHash = Hash(input);
        return string.Equals(inputHash, storedHash, StringComparison.OrdinalIgnoreCase);
    }
}
