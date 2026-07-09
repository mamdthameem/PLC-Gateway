using System.Net;

namespace PlcApi.Middleware;

// Guards /api/admin/* with two independent checks (Part E4):
//   1. the caller's remote IP must be in Admin:AllowedIps (the cloud's egress IPs), and
//   2. the request must carry a matching X-Api-Key (Admin:ApiKey).
// Deployment notes also recommend mirroring the IP allowlist as an IIS-level rule.
public class AdminGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AdminGuardMiddleware> _logger;
    private readonly HashSet<string> _allowedIps;
    private readonly string? _apiKey;

    public AdminGuardMiddleware(RequestDelegate next, IConfiguration config, ILogger<AdminGuardMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowedIps = new HashSet<string>(
            config.GetSection("Admin:AllowedIps").Get<string[]>() ?? Array.Empty<string>());
        _apiKey = config["Admin:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/admin"))
        {
            await _next(context);
            return;
        }

        IPAddress? remote = context.Connection.RemoteIpAddress;
        string remoteStr = remote is null ? "" :
            (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4().ToString() : remote.ToString());

        bool ipOk = _allowedIps.Count > 0 && _allowedIps.Contains(remoteStr);
        bool keyOk = !string.IsNullOrEmpty(_apiKey) &&
                     context.Request.Headers.TryGetValue("X-Api-Key", out var provided) &&
                     string.Equals(provided.ToString(), _apiKey, StringComparison.Ordinal);

        if (!ipOk || !keyOk)
        {
            _logger.LogWarning("Admin request denied from {ip} (ipOk={ipOk}, keyOk={keyOk}) path {path}",
                remoteStr, ipOk, keyOk, context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
            return;
        }

        await _next(context);
    }
}
