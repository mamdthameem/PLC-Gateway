using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using PlcApi.Services;

namespace PlcApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly IUserService _users;
    private readonly JwtSigningKey _signingKey;
    private readonly LoginThrottle _throttle;

    public AuthController(IConfiguration config, ILogger<AuthController> logger, IUserService users,
        JwtSigningKey signingKey, LoginThrottle throttle)
    {
        _config     = config;
        _logger     = logger;
        _users      = users;
        _signingKey = signingKey;
        _throttle   = throttle;
    }

    // Anonymous: issues a JWT for a valid local dashboard user.
    //
    // Two limits guard it, both answering 429 with a message the login page shows as-is:
    //   - per visitor IP: 5 attempts a minute (rate-limit policy "login", see Program.cs);
    //   - per account:    10 failures lock that login name for 15 minutes (LoginThrottle).
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var loginId = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(request.Password))
            return Unauthorized(new { message = "Invalid credentials" });

        // Checked BEFORE the password, so guesses made during a lock tell the guesser nothing.
        if (_throttle.LockedFor(loginId) is { } remaining)
        {
            int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests, new
            {
                message = $"Too many failed sign-ins for this account. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}."
            });
        }

        var user = await _users.FindByLoginAsync(loginId);
        PasswordCheck check;
        if (user is null)
        {
            PasswordHasher.VerifyAgainstDummy(request.Password);   // same cost as a real check
            check = PasswordCheck.Failed;
        }
        else
        {
            check = PasswordHasher.Verify(request.Password, user.PasswordHash);
        }

        if (check == PasswordCheck.Failed)
        {
            _throttle.RecordFailure(loginId);
            _logger.LogWarning("Failed dashboard sign-in for '{login}' from {ip}.", loginId, ClientAddress.Of(HttpContext));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        _throttle.RecordSuccess(loginId);

        // An old-format hash (unsalted SHA-256, or fewer PBKDF2 iterations than today) is replaced
        // now, while the plain password is in hand. A failure here must not block the sign-in.
        if (check == PasswordCheck.SuccessNeedsRehash)
        {
            try
            {
                await _users.UpdatePasswordHashAsync(user!.Username, PasswordHasher.Hash(request.Password));
                _logger.LogInformation("Upgraded the stored password hash for '{user}'.", user.Username);
            }
            catch (Exception ex) { _logger.LogError(ex, "Could not upgrade the password hash for '{user}'.", user!.Username); }
        }

        if (!user!.IsApproved)
            return Unauthorized(new { message = "User is not approved for access" });

        if (user.ValidUntilUtc.HasValue && DateTime.UtcNow > user.ValidUntilUtc.Value)
            return Unauthorized(new { message = "User access has expired" });

        return Ok(new { token = GenerateJwtToken(user.Username, user.Role, user.Id.ToString()) });
    }

    private string GenerateJwtToken(string username, string role, string userId)
    {
        var credentials = new SigningCredentials(_signingKey.SecurityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, userId)
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
