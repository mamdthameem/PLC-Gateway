using Microsoft.AspNetCore.Mvc;
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

    public AuthController(IConfiguration config, ILogger<AuthController> logger, IUserService users)
    {
        _config = config;
        _logger = logger;
        _users  = users;
    }

    // Anonymous: issues a JWT for a valid local dashboard user.
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var loginId = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(request.Password))
            return Unauthorized(new { message = "Invalid credentials" });

        var user = await _users.FindByLoginAsync(loginId);
        if (user is null || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            return Unauthorized(new { message = "Invalid credentials" });

        if (!user.IsApproved)
            return Unauthorized(new { message = "User is not approved for access" });

        if (user.ValidUntilUtc.HasValue && DateTime.UtcNow > user.ValidUntilUtc.Value)
            return Unauthorized(new { message = "User access has expired" });

        return Ok(new { token = GenerateJwtToken(user.Username, user.Role, user.Id.ToString()) });
    }

    private string GenerateJwtToken(string username, string role, string userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            _config["Jwt:Key"] ?? "YourSuperSecretKeyWithAtLeast32Chars!!"));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

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
