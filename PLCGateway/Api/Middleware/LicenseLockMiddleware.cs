using PlcApi.Services;

namespace PlcApi.Middleware;

// When the license is locked (cloud validation failed beyond the grace period, Part E5), the
// data API and dashboard are locked out, but the PLC pipeline keeps recording locally and the
// admin + auth + health endpoints stay reachable. The frontend shows a lock screen on 402.
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
                            && !path.StartsWithSegments("/api/health");

        if (isGuardedApi && _state.Locked)
        {
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await context.Response.WriteAsJsonAsync(new { error = "license_locked" });
            return;
        }

        await _next(context);
    }
}
