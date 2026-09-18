namespace PlcApi.Services;

// Shared, thread-safe licence status. LicenseCheckService updates it; LicenseLockMiddleware and
// the dashboard's lock screen (GET /api/license) read it.
public class LicenseState
{
    private readonly object _lock = new();
    private LicenseStatus _current = LicenseStatus.NotChecked;

    public LicenseStatus Current { get { lock (_lock) return _current; } }
    public bool Locked => Current.Locked;

    public void Set(LicenseStatus status)
    {
        lock (_lock) _current = status;
    }
}

/// <summary>
/// One licence decision. <see cref="Reason"/> is what the lock screen explains:
/// "ok" (the licence server said yes), "not_configured" (no License:CheckUrl — the dashboard stays
/// open), "rejected" (the server said the key is wrong), "unreachable" (no answer: open until
/// <see cref="LockAfterUtc"/>, locked after it), or "not_checked" (before the first check).
/// </summary>
public record LicenseStatus(bool Locked, string Reason, DateTime? LastSuccessUtc, DateTime? LockAfterUtc)
{
    public static readonly LicenseStatus NotChecked = new(false, "not_checked", null, null);
}
