using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Short-lived launch authorization, the server half.
//
// The loader already proves the account online before it starts a program. That proof
// stops at the loader: a user who copies the installed program out of the launcher and
// double-clicks it never passes through it. So the program itself asks for a lease:
//
//   1. loader (session bearer)  -> POST .../launch-ticket   single-use ticket, 2 minutes
//   2. program (ticket)         -> POST /api/v1/launch/redeem  short lease, 30 minutes
//   3. program (lease bearer)   -> POST /api/v1/launch/heartbeat  sliding renewal
//
// Nothing in this file is a long-term credential and nothing here is a signing key: the
// ticket is consumed once, the lease slides forward only while the account is still
// allowed to run the program, and both live in memory next to the sessions they belong
// to (a restart of this service invalidates them, exactly like a session).
//
// The lease deliberately outlives a network outage but never outlives its own expiry: a
// running program may lose the server for up to LeaseSeconds and keep working, and then
// it must stop. A new start never uses that allowance - it has no lease until the server
// issues one.
sealed class LaunchGate
{
    private static readonly Regex ProgramIdPattern = new("^[a-z0-9][a-z0-9-]{1,31}$", RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex DevicePattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private const int MaxEntries = 50_000;
    private const int MaxFailuresPerWindow = 12;

    private readonly object stateGate = new();
    private readonly Store store;
    private readonly ILogger logger;
    private readonly ConcurrentDictionary<string, TicketRecord> tickets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> consumed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LeaseRecord> leases = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FailureWindow> failures = new(StringComparer.Ordinal);
    private readonly Timer sweep;

    public int TicketSeconds { get; }
    public int LeaseSeconds { get; }
    public int HeartbeatSeconds { get; }

    public LaunchGate(Store store, ILogger logger)
    {
        this.store = store;
        this.logger = logger;
        // Floors are a guard against a typo in the environment, not a policy: a 10 second ticket
        // still covers the loader's hand-off, and a 30 second lease is the shortest window that
        // still lets a heartbeat (5 s floor) renew it before it lapses.
        TicketSeconds = Bound(Environment.GetEnvironmentVariable("CHISPA_LAUNCH_TICKET_SECONDS"), 120, 10, 600);
        LeaseSeconds = Bound(Environment.GetEnvironmentVariable("CHISPA_LAUNCH_LEASE_SECONDS"), 1800, 30, 7200);
        var heartbeat = Bound(Environment.GetEnvironmentVariable("CHISPA_LAUNCH_HEARTBEAT_SECONDS"), 120, 5, 900);
        // A heartbeat longer than a third of the lease would let one lost beat eat the
        // whole offline allowance in a single step.
        HeartbeatSeconds = Math.Min(heartbeat, Math.Max(5, LeaseSeconds / 3));
        sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private static int Bound(string? raw, int fallback, int minimum, int maximum) =>
        int.TryParse(raw, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Token() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string SessionId(string bearer) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bearer))).ToLowerInvariant();

    // ---- ticket ---------------------------------------------------------------------------
    // Issued to the loader only: the account was authenticated on this very request, so the
    // ticket only has to carry who, which program and which version, for two minutes and one
    // redemption.
    public LaunchTicketIssued? Issue(UserRecord user, string programId, string version, string sessionId, Func<bool> sessionValid)
    {
        lock (stateGate)
        {
            if (!sessionValid()) return null;
            var now = DateTimeOffset.UtcNow;
            if (tickets.Count >= MaxEntries) Sweep(force: true);
            if (tickets.Count >= MaxEntries) return null;
            var token = Token();
            var expires = now.AddSeconds(TicketSeconds);
            tickets[token] = new TicketRecord(user.Username, user.Hash, sessionId, programId, version, expires, 0);
            logger.LogInformation("launch ticket issued account={Account} program={Program} version={Version}",
                user.Username, programId, version);
            return new LaunchTicketIssued(token, expires, now, TicketSeconds);

        }
    }

    // ---- redeem ---------------------------------------------------------------------------
    // Called by the program itself, with no session: the ticket IS the credential. Everything
    // that matters is re-read from the live store here - the ticket proves the loader's login
    // happened, not that the account is still allowed to run anything.
    public (int Status, object Body) Redeem(RedeemRequest? request, string remote)
    {
        lock (stateGate)
        {
            if (RateLimited(remote)) return (StatusCodes.Status429TooManyRequests, new { code = "rate_limited" });
            if (request is null || string.IsNullOrWhiteSpace(request.Ticket) || request.Ticket.Length is < 16 or > 128 ||
                request.Ticket.Any(c => !char.IsLetterOrDigit(c) && c is not ('-' or '_')) ||
                request.ProgramId is null || !ProgramIdPattern.IsMatch(request.ProgramId) ||
                request.Version is null || !VersionPattern.IsMatch(request.Version) ||
                request.Device is null || !DevicePattern.IsMatch(request.Device))
            {
                CountFailure(remote);
                return (StatusCodes.Status400BadRequest, new { code = "invalid_request" });
            }

            var token = request.Ticket!;
            if (!tickets.TryGetValue(token, out var ticket))
            {
                // A consumed ticket is its own answer: it is the only way a caller can reach this
                // branch with a token that was genuinely issued, and it is what a replay looks like.
                if (consumed.TryGetValue(token, out var usedAt))
                {
                    CountFailure(remote);
                    logger.LogWarning("launch ticket replay rejected program={Program} remote={Remote} issuedEarlierSeconds={Seconds}",
                        request.ProgramId, remote, (int)(DateTimeOffset.UtcNow - usedAt).TotalSeconds);
                    return (StatusCodes.Status409Conflict, new { code = "ticket_used" });
                }
                CountFailure(remote);
                return (StatusCodes.Status401Unauthorized, new { code = "ticket_unknown" });
            }

            if (ticket.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                tickets.TryRemove(token, out _);
                CountFailure(remote);
                return (StatusCodes.Status410Gone, new { code = "ticket_expired" });
            }

            if (!string.Equals(ticket.ProgramId, request.ProgramId, StringComparison.Ordinal) ||
                !string.Equals(ticket.Version, request.Version, StringComparison.Ordinal))
            {
                // The ticket was not issued for this program/version: refuse without consuming it,
                // but stop a caller from probing one ticket against many programs by invalidating it
                // after a few tries.
                var attempts = ticket.Attempts + 1;
                if (attempts >= 3) tickets.TryRemove(token, out _);
                else tickets.TryUpdate(token, ticket with { Attempts = attempts }, ticket);
                CountFailure(remote);
                logger.LogWarning("launch ticket refused for wrong target account={Account} ticketProgram={Program} requested={Requested}",
                    ticket.Username, ticket.ProgramId, request.ProgramId);
                return (StatusCodes.Status403Forbidden, new { code = "ticket_mismatch" });
            }

            var user = store.CurrentUser(ticket.Username, ticket.UserHash);
            if (user is null)
            {
                tickets.TryRemove(token, out _);
                CountFailure(remote);
                logger.LogInformation("launch refused, account no longer valid account={Account}", ticket.Username);
                return (StatusCodes.Status403Forbidden, new { code = "account_expired" });
            }
            if (!store.CanAccess(user, request.ProgramId))
            {
                tickets.TryRemove(token, out _);
                CountFailure(remote);
                logger.LogInformation("launch refused, program no longer authorised account={Account} program={Program}",
                    ticket.Username, request.ProgramId);
                return (StatusCodes.Status403Forbidden, new { code = "account_forbidden" });
            }
            var publish = store.GetRelease(request.ProgramId, request.Version);
            if (publish is null)
            {
                tickets.TryRemove(token, out _);
                CountFailure(remote);
                return (StatusCodes.Status404NotFound, new { code = "program_unavailable" });
            }
            // A version banned after the ticket was issued still must not start: the ticket proves the
            // login happened, not that the build is still allowed to run.
            if (publish.Status == ReleaseStatus.Banned)
            {
                tickets.TryRemove(token, out _);
                CountFailure(remote);
                logger.LogWarning("launch refused, version banned account={Account} program={Program} version={Version}",
                    ticket.Username, request.ProgramId, request.Version);
                return (StatusCodes.Status403Forbidden, new { code = "release_banned" });
            }

            if (leases.Count >= MaxEntries) Sweep(force: true);
            if (leases.Count >= MaxEntries)
                return (StatusCodes.Status503ServiceUnavailable, new { code = "launch_capacity" });

            // Consume before issuing: two redemptions racing on one ticket must not both succeed.
            if (!tickets.TryRemove(token, out _))
                return (StatusCodes.Status409Conflict, new { code = "ticket_used" });
            consumed[token] = DateTimeOffset.UtcNow;

            var now = DateTimeOffset.UtcNow;
            var leaseToken = Token();
            if (leases.Count >= MaxEntries) Sweep(force: true);
            var lease = new LeaseRecord(leaseToken, ticket.Username, ticket.UserHash, ticket.SessionId,
                request.ProgramId, request.Version, request.Device!, now, LeaseDeadline(now, store.ProgramExpiry(user, request.ProgramId)), RemoteAddress: remote);
            leases[leaseToken] = lease;
            failures.TryRemove(FailureKey(remote), out _);
            logger.LogInformation("launch lease issued account={Account} program={Program} version={Version} device={Device} remote={Remote}",
                ticket.Username, request.ProgramId, request.Version, Short(request.Device!), remote);
            return (StatusCodes.Status200OK, new LeaseIssued(leaseToken, lease.ExpiresAt, now,
                HeartbeatSeconds, LeaseSeconds, ticket.Username, request.ProgramId, request.Version, Api, store.ProgramExpiry(user, request.ProgramId)));

        }
    }

    public int Api => 1;

    public IReadOnlyList<OnlineProgram> OnlinePrograms()
    {
        lock (stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            return leases.Values.Where(lease => lease.RevokedReason is null && lease.ExpiresAt > now &&
                    (lease.LastSeen ?? lease.IssuedAt) > now.AddSeconds(-(HeartbeatSeconds * 2 + 15)))
                .Where(lease => store.CurrentUser(lease.Username, lease.UserHash) is { } user &&
                    store.CanAccess(user, lease.ProgramId) &&
                    store.GetRelease(lease.ProgramId, lease.Version) is { Status: not ReleaseStatus.Banned })
                .Select(lease => new OnlineProgram(lease.Username, lease.ProgramId,
                    store.FindProgram(lease.ProgramId)?.Name ?? lease.ProgramId, lease.Version,
                    lease.RemoteAddress, lease.IssuedAt, lease.LastSeen ?? lease.IssuedAt, lease.ExpiresAt))
                .OrderByDescending(lease => lease.LastSeen).ToArray();
        }
    }

    // ---- heartbeat ------------------------------------------------------------------------
    // The only place a running program re-earns the right to keep running. Every reason an
    // account can lose access is answered here with a code the program can act on.
    public (int Status, object Body) Heartbeat(string? bearer, string? device)
    {
        lock (stateGate)
        {
            if (string.IsNullOrWhiteSpace(bearer) || !leases.TryGetValue(bearer, out var lease))
                return (StatusCodes.Status401Unauthorized, new { code = "lease_unknown" });
            if (lease.RevokedReason is not null)
                return (StatusCodes.Status403Forbidden, new { code = "lease_revoked", reason = lease.RevokedReason });
            if (lease.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                leases.TryRemove(bearer, out _);
                return (StatusCodes.Status410Gone, new { code = "lease_expired" });
            }
            if (device is null || !string.Equals(device, lease.Device, StringComparison.Ordinal))
                return (StatusCodes.Status403Forbidden, new { code = "lease_device_mismatch" });

            var user = store.CurrentUser(lease.Username, lease.UserHash);
            if (user is null)
            {
                Revoke(bearer, "account_expired");
                return (StatusCodes.Status403Forbidden, new { code = "account_expired" });
            }
            if (!store.CanAccess(user, lease.ProgramId))
            {
                Revoke(bearer, "account_forbidden");
                return (StatusCodes.Status403Forbidden, new { code = "account_forbidden" });
            }
            var publish = store.GetRelease(lease.ProgramId, lease.Version);
            if (publish is null)
            {
                Revoke(bearer, "program_unavailable");
                return (StatusCodes.Status404NotFound, new { code = "program_unavailable" });
            }
            // Banning a version stops the programs that are already running on it, at the next beat.
            if (publish.Status == ReleaseStatus.Banned)
            {
                Revoke(bearer, "release_banned");
                return (StatusCodes.Status403Forbidden, new { code = "release_banned" });
            }

            var now = DateTimeOffset.UtcNow;
            var extended = lease with { ExpiresAt = LeaseDeadline(now, store.ProgramExpiry(user, lease.ProgramId)), LastSeen = now };
            leases[bearer] = extended;
            return (StatusCodes.Status200OK, new LeaseRenewed(true, extended.ExpiresAt, now, HeartbeatSeconds, LeaseSeconds,
                store.ProgramExpiry(user, lease.ProgramId)));

        }
    }

    // Checks the existing lease without renewing it: reports cannot extend authorisation.
    public DiagnosticOwner? DiagnosticIdentity(string? bearer, string? device)
    {
        lock (stateGate)
        {
            if (string.IsNullOrEmpty(bearer) || !leases.TryGetValue(bearer, out var lease) ||
                lease.RevokedReason is not null || lease.ExpiresAt <= DateTimeOffset.UtcNow ||
                !string.Equals(device, lease.Device, StringComparison.Ordinal)) return null;
            var user = store.CurrentUser(lease.Username, lease.UserHash);
            if (user is null || !store.CanAccess(user, lease.ProgramId)) return null;
            var release = store.GetRelease(lease.ProgramId, lease.Version);
            return release is null || release.Status == ReleaseStatus.Banned ? null :
                new DiagnosticOwner(lease.Username, lease.ProgramId, lease.Version);
        }
    }

    private DateTimeOffset LeaseDeadline(DateTimeOffset now, DateTimeOffset? accountExpiresAt)
    {
        var deadline = now.AddSeconds(LeaseSeconds);
        return accountExpiresAt is { } expiry && expiry < deadline ? expiry : deadline;
    }

    // ---- withdrawal and revocation --------------------------------------------------------
    public (int Status, object Body) Withdraw(string? bearer)
    {
        lock (stateGate)
        {
            if (!string.IsNullOrWhiteSpace(bearer)) leases.TryRemove(bearer, out _);
            return (StatusCodes.Status204NoContent, new { });

        }
    }

    public void Revoke(string leaseToken, string reason)
    {
        lock (stateGate)
        {
            if (leases.TryGetValue(leaseToken, out var lease)) leases[leaseToken] = lease with { RevokedReason = reason };

        }
    }

    // Signing out of the loader gives up every run it authorised. The program keeps working
    // only until its next heartbeat, which is the documented cost of not holding a session.
    // Revoked rather than removed: the reason survives to be told to the caller.
    public void RevokeSession(string sessionId)
    {
        lock (stateGate)
        {
            foreach (var (token, ticket) in tickets)
                if (ticket.SessionId == sessionId) tickets.TryRemove(token, out _);
            var revoked = 0;
            foreach (var (token, lease) in leases)
                if (lease.SessionId == sessionId && lease.RevokedReason is null)
                {
                    leases[token] = lease with { RevokedReason = "signed_out" };
                    revoked++;
                }
            if (revoked > 0) logger.LogInformation("launch leases revoked by sign-out count={Count}", revoked);

        }
    }

    private void Sweep(bool force = false)
    {
        lock (stateGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var (token, ticket) in tickets)
                if (ticket.ExpiresAt <= now) tickets.TryRemove(token, out _);
            foreach (var (token, lease) in leases)
                if (lease.ExpiresAt <= now) leases.TryRemove(token, out _);
            foreach (var (token, at) in consumed)
                if (now - at > TimeSpan.FromMinutes(10) || consumed.Count > MaxEntries) consumed.TryRemove(token, out _);
            foreach (var (key, window) in failures)
                if (window.Until <= now || failures.Count > MaxEntries) failures.TryRemove(key, out _);

        }
    }

    private static string FailureKey(string remote) => "redeem:" + remote;

    private bool RateLimited(string remote) =>
        failures.TryGetValue(FailureKey(remote), out var window) &&
        window.Count >= MaxFailuresPerWindow && window.Until > DateTimeOffset.UtcNow;

    private void CountFailure(string remote)
    {
        var now = DateTimeOffset.UtcNow;
        failures.AddOrUpdate(FailureKey(remote),
            _ => new FailureWindow(1, now.AddMinutes(1)),
            (_, old) => old.Until <= now ? new FailureWindow(1, now.AddMinutes(1)) : old with { Count = old.Count + 1 });
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}

sealed record LaunchTicketIssued(string Ticket, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, int TicketSeconds);
sealed record LeaseIssued(string Lease, DateTimeOffset LeaseExpiresAt, DateTimeOffset ServerTime,
    int HeartbeatSeconds, int LeaseSeconds, string Account, string ProgramId, string Version, int Api,
    DateTimeOffset? AccountExpiresAt);
sealed record LeaseRenewed(bool Ok, DateTimeOffset LeaseExpiresAt, DateTimeOffset ServerTime,
    int HeartbeatSeconds, int LeaseSeconds, DateTimeOffset? AccountExpiresAt);
sealed record RedeemRequest(string? Ticket, string? ProgramId, string? Version, string? Device);
sealed record TicketRecord(string Username, string UserHash, string SessionId, string ProgramId,
    string Version, DateTimeOffset ExpiresAt, int Attempts);
sealed record LeaseRecord(string Token, string Username, string UserHash, string SessionId,
    string ProgramId, string Version, string Device, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    string? RevokedReason = null, DateTimeOffset? LastSeen = null, string? RemoteAddress = null);
sealed record OnlineProgram(string Username, string ProgramId, string ProgramName, string Version,
    string? Address, DateTimeOffset StartedAt, DateTimeOffset LastSeen, DateTimeOffset ExpiresAt);
sealed record FailureWindow(int Count, DateTimeOffset Until);
