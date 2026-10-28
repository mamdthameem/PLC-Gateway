using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PlcApi.Auth;

// Who may use the dashboard API, and how a request proves it (decided 2026-10-12/13, 2026-10-26).
//
// Every request carries "Authorization: Bearer <token>", and the token is one of two kinds:
//
//   Firebase   — a Firebase ID token: one person's own email login, managed from the cloud. Checked
//                against Google's published keys (issuer https://securetoken.google.com/<projectId>,
//                audience <projectId>). Accepted when its clientId claim is THIS gateway's client (role
//                "admin" or "viewer" from the token), or when it carries cloudAdmin=true — ShotSense
//                office staff, who may open every site's dashboard as admin.
//   GatewayToken — a token this gateway signed itself (Jwt:Key), issued only to the emergency local
//                  admin (Auth:LocalAdmin), which works only from the plant network, never through the
//                  Cloudflare tunnel (NetworkOrigin).
//
// The shared username/password login was REMOVED on 2026-10-26: with it in place, people kept using
// one shared account instead of their own email login. Its tokens (login=password, or no login claim
// at all) are refused.
//
// The default scheme "Gateway" reads the token's issuer (without trusting it) to pick which of the two
// checks it, so controllers just say [Authorize]. The cloud's /api/admin/* uses neither: it keeps its
// X-Api-Key (AdminGuardMiddleware).
public static class GatewayAuth
{
    public const string Scheme         = "Gateway";
    public const string LocalScheme    = "GatewayToken";
    public const string FirebaseScheme = "Firebase";

    /// <summary>Claim on every signed-in principal saying how the person signed in.</summary>
    public const string LoginClaim      = "login";
    public const string LoginFirebase   = "firebase";
    public const string LoginOffice     = "office";       // a ShotSense cloud admin, by email
    public const string LoginLocalAdmin = "local-admin";

    public const string RoleAdmin  = "admin";
    public const string RoleViewer = "viewer";

    /// <summary>The emergency admin's login name (Auth:LocalAdmin:Username, default "localadmin").</summary>
    public static string LocalAdminUsername(IConfiguration config) =>
        config["Auth:LocalAdmin:Username"]?.Trim() is { Length: > 0 } u ? u : "localadmin";

    /// <summary>The emergency admin's password hash, or null when the login is not set up.</summary>
    public static string? LocalAdminHash(IConfiguration config) => SecretConfig.Get(config, "Auth:LocalAdmin:PasswordHash");

    /// <summary>Firebase-issued tokens go to the Firebase check; everything else to this gateway's own.</summary>
    public static string SelectScheme(HttpContext http, FirebaseLogin firebase)
    {
        if (!firebase.Enabled) return LocalScheme;
        string header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return LocalScheme;
        try
        {
            // Read only to route the token. The chosen scheme then verifies it in full.
            var token = new JsonWebToken(header["Bearer ".Length..].Trim());
            return token.Issuer == firebase.Issuer ? FirebaseScheme : LocalScheme;
        }
        catch (ArgumentException)
        {
            return LocalScheme;   // not a JWT at all; the local check rejects it
        }
    }

    /// <summary>
    /// A Firebase token is for one client's people — refuse any other client's, and any without a role —
    /// or for ShotSense office staff (cloudAdmin claim, set by the cloud on every admins/{uid} account),
    /// who get admin on every site.
    /// </summary>
    public static Task ValidateFirebaseToken(TokenValidatedContext ctx, FirebaseLogin firebase)
    {
        var p = ctx.Principal!;
        string email = p.FindFirst("email")?.Value ?? "";
        string name  = p.FindFirst("name")?.Value is { Length: > 0 } n ? n : email;

        if (string.Equals(p.FindFirst("cloudAdmin")?.Value, "true", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Principal = Principal(FirebaseScheme, p.FindFirst("sub")?.Value ?? "", name, email, RoleAdmin, LoginOffice);
            return Task.CompletedTask;
        }

        if (firebase.ClientId.Length == 0)
        {
            ctx.Fail(new GatewayAuthException(
                "This gateway has no client id set, so email sign-in is not available here. Contact your administrator."));
            return Task.CompletedTask;
        }
        if (!string.Equals(p.FindFirst("clientId")?.Value, firebase.ClientId, StringComparison.Ordinal))
        {
            ctx.Fail(new GatewayAuthException(
                "This email has not been added for this site. Ask ShotSense to add it, then sign in again."));
            return Task.CompletedTask;
        }
        string? role = p.FindFirst("role")?.Value;
        if (role is not (RoleAdmin or RoleViewer))
        {
            ctx.Fail(new GatewayAuthException("This account has no role on this site. Ask your administrator."));
            return Task.CompletedTask;
        }

        ctx.Principal = Principal(FirebaseScheme, p.FindFirst("sub")?.Value ?? "", name, email, role, LoginFirebase);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A token this gateway signed. Only the emergency local admin's is accepted, and only on the plant
    /// network — so one obtained there is useless through the tunnel. Anything else this gateway ever
    /// signed came from the removed shared login.
    /// </summary>
    public static Task ValidateLocalToken(TokenValidatedContext ctx)
    {
        var p = ctx.Principal!;
        if (p.FindFirst(LoginClaim)?.Value != LoginLocalAdmin)
        {
            ctx.Fail(new GatewayAuthException("The shared login has been removed. Sign in with your email address."));
            return Task.CompletedTask;
        }
        if (!NetworkOrigin.IsLocalNetwork(ctx.HttpContext))
        {
            ctx.Fail(new GatewayAuthException("The local admin login works only on the plant network."));
            return Task.CompletedTask;
        }

        ctx.Principal = Principal(LocalScheme, p.FindFirst(JwtRegisteredClaimNames.Jti)?.Value ?? "",
            p.Identity?.Name ?? "", "", RoleAdmin, LoginLocalAdmin);
        return Task.CompletedTask;
    }

    private static ClaimsPrincipal Principal(string scheme, string id, string name, string email, string role, string login) =>
        new(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, id),
            new Claim(ClaimTypes.Name, name),
            new Claim(ClaimTypes.Email, email),
            new Claim(ClaimTypes.Role, role),
            new Claim(LoginClaim, login),
        }, scheme, ClaimTypes.Name, ClaimTypes.Role));

    /// <summary>
    /// A refused or missing sign-in answers 401 with one plain sentence, which the dashboard shows on
    /// its login page — instead of an empty body that could only say "something went wrong".
    /// </summary>
    public static async Task WriteChallengeAsync(JwtBearerChallengeContext ctx)
    {
        ctx.HandleResponse();
        string message = ctx.AuthenticateFailure switch
        {
            null                           => "Please sign in.",
            GatewayAuthException g         => g.Message,
            SecurityTokenExpiredException  => "Your sign-in has expired. Please sign in again.",
            InvalidOperationException      => "Email sign-in cannot be checked right now. The gateway may have no internet connection.",
            _                              => "Your sign-in was not accepted. Please sign in again.",
        };
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        await ctx.Response.WriteAsJsonAsync(new { error = message, message });
    }
}

/// <summary>A sign-in refused for a reason the person should read as-is.</summary>
public sealed class GatewayAuthException(string message) : Exception(message);

/// <summary>
/// The cloud project the dashboard's email logins belong to (Firebase:*), and which of its clients
/// this gateway is (Firebase:ClientId). ProjectId, ApiKey and AuthDomain are public identifiers —
/// the login page needs them — and the same for every gateway; ClientId is per installation.
/// </summary>
public sealed class FirebaseLogin
{
    public string ProjectId  { get; private init; } = "";
    public string ApiKey     { get; private init; } = "";
    public string AuthDomain { get; private init; } = "";
    public string AppId      { get; private init; } = "";
    public string ClientId   { get; private init; } = "";

    /// <summary>Where ClientId came from, for the startup log.</summary>
    public string ClientIdSource { get; private init; } = "";

    public bool Enabled => ProjectId.Length > 0 && ApiKey.Length > 0;
    public string Issuer => $"https://securetoken.google.com/{ProjectId}";

    public static FirebaseLogin FromConfig(IConfiguration config)
    {
        string projectId = config["Firebase:ProjectId"]?.Trim() ?? "";
        string clientId  = config["Firebase:ClientId"]?.Trim() ?? "";
        string source    = "Firebase:ClientId";

        // Installs from before Firebase:ClientId existed already carry their client id in the licence
        // address (…/licenseCheck?clientId=…), so they need no new setting.
        if (clientId.Length == 0 && ClientIdFromLicenseUrl(config["License:CheckUrl"]) is { } fromLicense)
        {
            clientId = fromLicense;
            source   = "License:CheckUrl";
        }

        return new FirebaseLogin
        {
            ProjectId      = projectId,
            ApiKey         = config["Firebase:ApiKey"]?.Trim() ?? "",
            AuthDomain     = config["Firebase:AuthDomain"]?.Trim() is { Length: > 0 } d ? d : $"{projectId}.firebaseapp.com",
            AppId          = config["Firebase:AppId"]?.Trim() ?? "",
            ClientId       = clientId,
            ClientIdSource = clientId.Length > 0 ? source : "",
        };
    }

    private static string? ClientIdFromLicenseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "clientId" && Uri.UnescapeDataString(kv[1]) is { Length: > 0 } id) return id;
        }
        return null;
    }
}

/// <summary>
/// Did a request come from the plant network, or through the Cloudflare tunnel?
///
/// Behind the tunnel every request reaches the gateway from cloudflared on the same PC (127.0.0.1),
/// so the address alone cannot tell. Cloudflare, however, adds its own headers to every request it
/// forwards (CF-Connecting-IP, CF-Ray, CDN-Loop, CF-Visitor), and a visitor on the internet cannot
/// remove them. So: any of those headers ⇒ the tunnel. Otherwise the connection itself must come from
/// this PC or a private (plant network) address. Someone on the plant network who adds such a header
/// only locks themselves out.
/// </summary>
public static class NetworkOrigin
{
    private static readonly string[] CloudflareHeaders = { "CF-Connecting-IP", "CF-Ray", "CDN-Loop", "CF-Visitor" };

    public static bool CameThroughTunnel(HttpContext http) =>
        CloudflareHeaders.Any(h => http.Request.Headers.ContainsKey(h));

    public static bool IsLocalNetwork(HttpContext http)
    {
        if (CameThroughTunnel(http)) return false;
        IPAddress? ip = http.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254);                // link-local
        }
        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }
}
