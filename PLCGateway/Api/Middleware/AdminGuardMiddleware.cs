using System.Security.Cryptography;
using System.Text;

namespace PlcApi.Middleware;

// Guards /api/admin/* with a single check (Part E4): the request must carry a matching
// X-Api-Key (Admin:ApiKey). There is deliberately no IP allowlist — the cloud caller
// (Firebase Cloud Functions) has no fixed egress IP on the current plan, so key-over-HTTPS
// is the whole model.
//
// A missing key, the "REPLACE_WITH…" placeholder or anything under 32 characters is NOT a key
// (SecretConfig): until a real Admin:ApiKey is set, every /api/admin/* request is refused. The
// comparison is constant-time, so the response time says nothing about how close a guess was.
public class AdminGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AdminGuardMiddleware> _logger;
    private readonly byte[]? _apiKey;

    public AdminGuardMiddleware(RequestDelegate next, IConfiguration config, ILogger<AdminGuardMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        var key = SecretConfig.Get(config, "Admin:ApiKey", SecretConfig.MinLength);
        _apiKey = key is null ? null : Encoding.UTF8.GetBytes(key);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/admin"))
        {
            await _next(context);
            return;
        }

        bool keyOk = _apiKey is not null &&
                     context.Request.Headers.TryGetValue("X-Api-Key", out var provided) &&
                     CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided.ToString()), _apiKey);

        if (!keyOk)
        {
            _logger.LogWarning("Admin request denied (bad or missing X-Api-Key) path {path}", context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "forbidden" });
            return;
        }

        await _next(context);
    }
}
