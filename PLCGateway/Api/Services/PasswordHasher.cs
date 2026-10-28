using System.Security.Cryptography;
using System.Text;

namespace PlcApi.Services;

public enum PasswordCheck
{
    Failed,
    Success,
    /// <summary>Correct password, but stored in an older or weaker format — save a fresh hash.</summary>
    SuccessNeedsRehash,
}

// Dashboard password hashing.
//
// Stored format: pbkdf2-sha256$<iterations>$<salt base64>$<hash base64>
// The iteration count travels with each hash, so it can be raised later without breaking existing
// logins: a hash below the current count verifies, then reports SuccessNeedsRehash.
//
// Until 2026-10 hashes were plain unsalted SHA-256 hex — fast to brute-force, and identical for two
// accounts with the same password. Those still verify (so nobody is locked out), report
// SuccessNeedsRehash, and AuthController replaces them at that user's next sign-in.
// tools/Set-DashboardPassword.ps1 writes this same format; keep the two in step.
public static class PasswordHasher
{
    /// <summary>Shortest password accepted anywhere one is SET (installer, CLI, scripts).</summary>
    public const int MinLength = 12;

    private const string Scheme     = "pbkdf2-sha256";
    private const int    Iterations = 600_000;   // OWASP 2023 guidance for PBKDF2-HMAC-SHA256
    private const int    SaltBytes  = 16;
    private const int    HashBytes  = 32;

    // Verified against when the login name does not exist, so an unknown name costs the same time
    // as a wrong password and response timing does not reveal which names are real.
    private static readonly Lazy<string> DummyHash = new(() => Hash(Guid.NewGuid().ToString("N")));

    public static string Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static PasswordCheck Verify(string password, string? storedHash)
    {
        if (string.IsNullOrEmpty(storedHash)) return PasswordCheck.Failed;

        if (storedHash.StartsWith(Scheme + "$", StringComparison.Ordinal))
            return VerifyPbkdf2(password, storedHash);

        // Legacy: 64 lowercase/uppercase hex characters of unsalted SHA-256.
        if (storedHash.Length == 64 && IsHex(storedHash))
        {
            byte[] expected = Convert.FromHexString(storedHash);
            byte[] actual   = SHA256.HashData(Encoding.UTF8.GetBytes(password));
            return CryptographicOperations.FixedTimeEquals(actual, expected)
                ? PasswordCheck.SuccessNeedsRehash
                : PasswordCheck.Failed;
        }

        return PasswordCheck.Failed;
    }

    /// <summary>Burns the same time as a real check. Call it when the login name was not found.</summary>
    public static void VerifyAgainstDummy(string password) => Verify(password, DummyHash.Value);

    private static PasswordCheck VerifyPbkdf2(string password, string storedHash)
    {
        string[] parts = storedHash.Split('$');
        if (parts.Length != 4 || !int.TryParse(parts[1], out int iterations) || iterations < 1)
            return PasswordCheck.Failed;

        byte[] salt, expected;
        try
        {
            salt     = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return PasswordCheck.Failed; }
        if (expected.Length == 0) return PasswordCheck.Failed;

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return PasswordCheck.Failed;

        return iterations < Iterations ? PasswordCheck.SuccessNeedsRehash : PasswordCheck.Success;
    }

    private static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
