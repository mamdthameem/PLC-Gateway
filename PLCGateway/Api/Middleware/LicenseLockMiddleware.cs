using PlcApi.Services;

namespace PlcApi.Middleware;

// When the licence is locked (LicenseCheckService: the key was rejected, or the licence server has
// not answered for longer than the grace period) the dashboard's data API answers 402 and the
// dashboard shows its lock screen. The PLC pipeline keeps recording regardless, and the admin,
// auth, licence-status and health endpoints stay reachable.
public class LicenseLockMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LicenseState _state;

    public LicenseLockMiddleware(RequestDelegate next, LicenseState state)
    {
        _next = next;
        _state = state;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        bool isGuardedApi = path.StartsWithSegments("/api")
                            && !path.StartsWithSegments("/api/admin")
                            && !path.StartsWithSegments("/api/auth")
                            && !path.StartsWithSegments("/api/license")
                            && !path.StartsWithSegments("/api/health");

        var status = _state.Current;
        if (isGuardedApi && status.Locked)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await context.Response.WriteAsJsonAsync(new { error = "license_locked", reason = status.Reason });
            return;
        }

        await _next(context);
    }
}
