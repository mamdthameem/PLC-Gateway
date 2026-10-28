using System.Net;
using Microsoft.Extensions.Caching.Memory;

namespace PlcApi.Services;

// Per-account lockout for dashboard sign-in. The other half of the brute-force defence is the
// per-IP rate limit on POST /api/auth/login (policy "login" in Program.cs): that one stops a single
// address hammering, this one stops many addresses sharing out guesses against one account.
//
// Keyed on the login name AS TYPED (lower-cased), whether or not such a user exists. Keying on real
// accounts only would let the "locked" reply reveal which names are real.
//
// In memory on purpose: a restart clears it, which is acceptable — the IP limit still applies, and
// persisting it would give an attacker a way to lock the admin out across restarts.
public sealed class LoginThrottle
{
    public const int MaxFailures = 10;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    // Failures older than this are forgotten, so a user who mistypes occasionally never trips it.
    private static readonly TimeSpan FailureMemory = TimeSpan.FromMinutes(30);

    private readonly IMemoryCache _cache;

    public LoginThrottle(IMemoryCache cache) => _cache = cache;

    private sealed class State
    {
        public int Failures;
        public DateTimeOffset? LockedUntil;
    }

    private static string Key(string loginId) => "login-throttle:" + loginId.Trim().ToLowerInvariant();

    /// <summary>How long the account stays locked, or null when sign-in is allowed.</summary>
    public TimeSpan? LockedFor(string loginId)
    {
        if (!_cache.TryGetValue(Key(loginId), out State? state) || state is null) return null;
        lock (state)
        {
            if (state.LockedUntil is { } until && until > DateTimeOffset.UtcNow)
                return until - DateTimeOffset.UtcNow;
            return null;
        }
    }

    public void RecordFailure(string loginId)
    {
        string key = Key(loginId);
        var state = _cache.GetOrCreate(key, e => { e.SlidingExpiration = FailureMemory; return new State(); })!;
        lock (state)
        {
            var now = DateTimeOffset.UtcNow;
            if (state.LockedUntil is { } until && until > now) return;   // already locked

            state.LockedUntil = null;
            state.Failures++;
            if (state.Failures >= MaxFailures)
            {
                state.Failures    = 0;
                state.LockedUntil = now + LockDuration;
            }
        }
        // Re-set so the sliding window outlives a lock that was just started.
        _cache.Set(key, state, new MemoryCacheEntryOptions { SlidingExpiration = FailureMemory + LockDuration });
    }

    public void RecordSuccess(string loginId) => _cache.Remove(Key(loginId));
}

// Who is actually asking. Behind the Cloudflare tunnel every request reaches Kestrel from
// cloudflared on this same PC, so RemoteIpAddress is always 127.0.0.1 — a rate limit keyed on it
// would put the whole internet in one bucket, and one attacker would lock everyone out. Cloudflare
// passes the visitor's address in CF-Connecting-IP.
//
// That header is trusted ONLY on a loopback connection. Port 5200 can be open to the plant network,
// and anyone there connecting directly could otherwise send a made-up CF-Connecting-IP to get a
// fresh bucket on every request.
public static class ClientAddress
{
    public static string Of(HttpContext http)
    {
        IPAddress? remote = http.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();

        if (remote is not null && IPAddress.IsLoopback(remote)
            && http.Request.Headers.TryGetValue("CF-Connecting-IP", out var forwarded)
            && IPAddress.TryParse(forwarded.ToString().Trim(), out var visitor))
            return visitor.ToString();

        return remote?.ToString() ?? "unknown";
    }
}
