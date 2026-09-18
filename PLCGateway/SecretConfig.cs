using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Reads the gateway's secrets (Jwt:Key, Admin:ApiKey, License:Key). The placeholders shipped in
/// appsettings.json ("REPLACE_WITH_…") are public — they are in the repo — so they count as NOT
/// SET, never as a real key. Real values belong in appsettings.Production.json next to the app,
/// which git ignores and publish never overwrites (DEPLOYMENT-NOTES.md, section 9).
/// </summary>
public static class SecretConfig
{
    /// <summary>
    /// Shortest Jwt:Key / Admin:ApiKey accepted. HS256 needs a 32-byte key, and a short API key
    /// could simply be guessed over the internet.
    /// </summary>
    public const int MinLength = 32;

    /// <summary>
    /// The configured value, or null when it is missing, still a placeholder, or shorter than
    /// <paramref name="minLength"/>.
    /// </summary>
    public static string? Get(IConfiguration config, string key, int minLength = 1)
    {
        var value = config[key]?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase)) return null;
        return value.Length >= minLength ? value : null;
    }
}

/// <summary>
/// The key that signs dashboard login tokens (AuthController) and checks them (JwtBearer, in
/// Program.cs). One instance, so the two can never disagree. <see cref="Generated"/> is true when
/// no usable Jwt:Key was configured and a random key was made for this run instead.
/// </summary>
public sealed class JwtSigningKey
{
    public JwtSigningKey(string value, bool generated)
    {
        SecurityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(value));
        Generated   = generated;
    }

    public SymmetricSecurityKey SecurityKey { get; }
    public bool Generated { get; }
}
