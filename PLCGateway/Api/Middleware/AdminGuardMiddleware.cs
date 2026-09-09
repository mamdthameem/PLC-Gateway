namespace PlcApi.Middleware;

// Guards /api/admin/* with a single check (Part E4): the request must carry a matching
// X-Api-Key (Admin:ApiKey). There is deliberately no IP allowlist — the cloud caller
// (Firebase Cloud Functions) has no fixed egress IP on the current plan, so key-over-HTTPS
// is the whole model.
public class AdminGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<AdminGuardMiddleware> _logger;
    private readonly string? _apiKey;

    public AdminGuardMiddleware(RequestDelegate next, IConfiguration config, ILogger<AdminGuardMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _apiKey = config["Admin:ApiKey"];
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/admin"))
        {
            await _next(context);
            return;
        }

        bool keyOk = !string.IsNullOrEmpty(_apiKey) &&
                     context.Request.Headers.TryGetValue("X-Api-Key", out var provided) &&
                     string.Equals(provided.ToString(), _apiKey, StringComparison.Ordinal);

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
