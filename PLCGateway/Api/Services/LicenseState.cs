namespace PlcApi.Services;

// Shared, thread-safe license status. LicenseCheckService updates it; LicenseLockMiddleware reads it.
public class LicenseState
{
    private readonly object _lock = new();
    private bool _locked;
    private DateTime? _lastSuccessUtc;

    public bool Locked { get { lock (_lock) return _locked; } }
    public DateTime? LastSuccessUtc { get { lock (_lock) return _lastSuccessUtc; } }

    public void Set(bool locked, DateTime? lastSuccessUtc)
    {
        lock (_lock) { _locked = locked; _lastSuccessUtc = lastSuccessUtc; }
    }
}
