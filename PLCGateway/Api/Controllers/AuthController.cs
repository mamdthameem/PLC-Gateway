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
// needs around that, plus the two logins whose tokens this gateway signs itself: the old shared
// username/password, and the emergency local admin. See GatewayAuth for the rules.
[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly ILogger<AuthController> _logger;
    private readonly IUserService _users;
    private readonly JwtSigningKey _signingKey;
    private readonly LoginThrottle _throttle;
    private readonly FirebaseLogin _firebase;

    public AuthController(IConfiguration config, ILogger<AuthController> logger, IUserService users,
        JwtSigningKey signingKey, LoginThrottle throttle, FirebaseLogin firebase)
    {
        _config     = config;
        _logger     = logger;
        _users      = users;
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
        legacyLogin = GatewayAuth.LegacyLoginEnabled(_config),
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
        login = User.FindFirst(GatewayAuth.LoginClaim)?.Value ?? GatewayAuth.LoginPassword,
    });

    // Anonymous: the OLD shared username/password login (users table). Kept until email sign-in has
    // been tested, then switched off with Auth:LegacyLoginEnabled=false — which also voids every token
    // it already issued (GatewayAuth.ValidateLocalToken).
    //
    // Two limits guard it, both answering 429 with a message the login page shows as-is:
    //   - per visitor IP: 5 attempts a minute (rate-limit policy "login", see Program.cs);
    //   - per account:    10 failures lock that login name for 15 minutes (LoginThrottle).
    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (!GatewayAuth.LegacyLoginEnabled(_config))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "The shared login has been switched off. Sign in with your email address." });

        var loginId = request.Username?.Trim();
        if (string.IsNullOrWhiteSpace(loginId) || string.IsNullOrWhiteSpace(request.Password))
            return Unauthorized(new { message = "Invalid credentials" });

        if (LockedOut(loginId) is { } locked) return locked;

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

        return Ok(new { token = IssueToken(user.Username, user.Role, user.Id.ToString(), GatewayAuth.LoginPassword) });
    }

    // Anonymous: the emergency local admin, for when the site's internet is down and email sign-in
    // cannot work. One account, its password hash in Auth:LocalAdmin:PasswordHash (made with
    // PLCGateway.exe --hash-password). Plant network only: through the Cloudflare tunnel this answers
    // 404, as if it did not exist, and a token it issued is refused there too (GatewayAuth).
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
        return Ok(new { token = IssueToken("Local admin", GatewayAuth.RoleAdmin, "local-admin", GatewayAuth.LoginLocalAdmin) });
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

    private string IssueToken(string name, string role, string userId, string login)
    {
        var credentials = new SigningCredentials(_signingKey.SecurityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Role, role),
            new Claim(JwtRegisteredClaimNames.Jti, userId),
            new Claim(GatewayAuth.LoginClaim, login),
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
