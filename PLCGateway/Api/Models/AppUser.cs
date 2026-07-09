namespace PlcApi.Models;

// Local dashboard user (users table). No tenant/subscription concept — this is a single-site
// deployment; the multi-client model lives in a separate cloud codebase.
public sealed class AppUser
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "user";
    public bool IsApproved { get; set; } = true;
    public DateTime? ValidUntilUtc { get; set; }
}
