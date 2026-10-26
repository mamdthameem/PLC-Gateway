using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using PlcApi.Auth;
using PlcApi.Services;

namespace PlcApi.Controllers;

// Dashboard sign-in. Email sign-in itself happens in the browser, against Firebase; the gateway only
// checks the resulting token on each request (GatewayAuth). The endpoints here are what the login page
// needs around that, plus the one login whose token this gateway signs itself: the emergency local
// admin. The shared username/password login was removed on 2026-10-26. See GatewayAuth for the rules.
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly JwtSigningKey _signingKey;
    private readonly LoginThrottle _throttle;
    private readonly FirebaseLogin _firebase;

    public AuthController(IConfiguration config, ILogger<AuthController> logger,
        JwtSigningKey signingKey, LoginThrottle throttle, FirebaseLogin firebase)
    {
        _config     = config;
        _logger     = logger;
        _signingKey = signingKey;
        _throttle   = throttle;
        _firebase   = firebase;
    }

    // Anonymous: what the login page should offer. The Firebase values are public identifiers (the
    // same ones any Firebase web page carries). localAdmin is true only when the emergency login is set
    // up AND this request comes from the plant network, so the internet never even learns it exists.
    [HttpGet("config")]
    public IActionResult LoginOptions() => Ok(new
    {
        firebase = _firebase.Enabled
            ? new { apiKey = _firebase.ApiKey, authDomain = _firebase.AuthDomain, projectId = _firebase.ProjectId, appId = _firebase.AppId }
            : null,
        emailLogin  = _firebase.Enabled && _firebase.ClientId.Length > 0,
        localAdmin  = GatewayAuth.LocalAdminHash(_config) is not null && NetworkOrigin.IsLocalNetwork(HttpContext),
    });

    // Who is signed in, as this gateway sees them. The dashboard calls it right after an email sign-in,
    // so a person from another client is told at once (401 with the reason) instead of on the first
    // data request, and to show the name and role in the header.
    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        name  = User.Identity?.Name ?? "",
        email = User.FindFirst(ClaimTypes.Email)?.Value ?? "",
        role  = User.FindFirst(ClaimTypes.Role)?.Value ?? GatewayAuth.RoleViewer,
        login = User.FindFirst(GatewayAuth.LoginClaim)?.Value ?? GatewayAuth.LoginFirebase,
    });

    // Anonymous: the emergency local admin, for when the site's internet is down and email sign-in
    // cannot work. One account, its password hash in Auth:LocalAdmin:PasswordHash (set by the installer,
    // or later with PLCGateway.exe --set-local-admin). Plant network only: through the Cloudflare tunnel
    // this answers 404, as if it did not exist, and a token it issued is refused there too (GatewayAuth).
    //
    // Two limits guard it, both answering 429 with a message the login page shows as-is:
    //   - per visitor IP: 5 attempts a minute (rate-limit policy "login", see Program.cs);
    //   - per account:    10 failures lock it for 15 minutes (LoginThrottle).
    [HttpPost("local-admin")]
    [EnableRateLimiting("login")]
    public IActionResult LocalAdmin([FromBody] LoginRequest request)
    {
        string? hash = GatewayAuth.LocalAdminHash(_config);
        if (hash is null || !NetworkOrigin.IsLocalNetwork(HttpContext))
            return NotFound();

        string username = GatewayAuth.LocalAdminUsername(_config);
        string throttleId = "local-admin:" + username;
        if (LockedOut(throttleId) is { } locked) return locked;

        bool nameMatches = string.Equals(request.Username?.Trim(), username, StringComparison.OrdinalIgnoreCase);
        // The password is checked even for a wrong name, so the time taken does not reveal which was wrong.
        bool passwordMatches = PasswordHasher.Verify(request.Password ?? "", hash) != PasswordCheck.Failed;
        if (!nameMatches || !passwordMatches)
        {
            _throttle.RecordFailure(throttleId);
            _logger.LogWarning("Failed local admin sign-in from {ip}.", ClientAddress.Of(HttpContext));
            return Unauthorized(new { message = "Invalid credentials" });
        }

        _throttle.RecordSuccess(throttleId);
        _logger.LogWarning("Local admin signed in from {ip}.", ClientAddress.Of(HttpContext));
        return Ok(new { token = IssueToken("Local admin", GatewayAuth.RoleAdmin, "local-admin") });
    }

    /// <summary>429 while this login name is locked after too many failures; null otherwise.</summary>
    private IActionResult? LockedOut(string loginId)
    {
        // Checked BEFORE the password, so guesses made during a lock tell the guesser nothing.
        if (_throttle.LockedFor(loginId) is not { } remaining) return null;
        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            message = $"Too many failed sign-ins for this account. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}."
        });
    }

    private string IssueToken(string name, string role, string userId)
    {
        var credentials = new SigningCredentials(_signingKey.SecurityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, userId),
            new Claim(GatewayAuth.LoginClaim, GatewayAuth.LoginLocalAdmin),
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
