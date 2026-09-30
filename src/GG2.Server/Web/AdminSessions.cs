using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace GG2.Server;

/// <summary>
/// Who is signed in to the web panel, and who is being made to wait.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole of the panel's authentication.</b> Everything else in
/// the project answers to a game client on a LAN or to a tool on loopback; this
/// answers to whatever can reach the bind address. The three things that matter
/// are here rather than spread through the request handler: the password is
/// compared in constant time, a session is a random 256-bit token the server
/// issued, and a wrong password costs the guesser time that grows.
/// </para>
/// <para>
/// <b>Sessions live in memory only.</b> A restart signs everybody out, which
/// is the correct behaviour for an operator console and removes any question of
/// a token file with the wrong permissions on it.
/// </para>
/// </remarks>
public sealed class AdminSessions
{
    /// <summary>
    /// How long a session lasts without being used.
    /// </summary>
    /// <remarks>
    /// Long enough to leave the panel open on a second monitor through a match,
    /// short enough that a walked-away-from browser is not a standing key.
    /// </remarks>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(4);

    /// <summary>
    /// How long a session lasts however much it is used.
    /// </summary>
    /// <remarks>
    /// <b>Both limits, not one.</b> An idle timeout alone means a tab left
    /// polling is authenticated for ever, which is precisely the tab most likely
    /// to be on a machine somebody else can reach.
    /// </remarks>
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromDays(1);

    /// <summary>
    /// Sign-ins allowed at once. The oldest is evicted beyond this.
    /// </summary>
    /// <remarks>
    /// Not a security limit — the password is the security limit — but a bound on
    /// what an authenticated client can make the server hold.
    /// </remarks>
    public const int MaxSessions = 32;

    /// <summary>Wrong passwords tolerated from one address before it waits.</summary>
    public const int FreeAttempts = 5;

    /// <summary>The first lockout. It doubles, up to <see cref="MaxLockout"/>.</summary>
    public static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(15);

    /// <inheritdoc cref="FirstLockout"/>
    public static readonly TimeSpan MaxLockout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// What every failed sign-in costs, lockout or not.
    /// </summary>
    /// <remarks>
    /// <b>Paid before the answer is sent, not after.</b> It blunts online
    /// guessing on its own and it flattens the timing difference between "no
    /// such session" and "wrong password", which is the only side channel a
    /// login endpoint this small has.
    /// </remarks>
    public static readonly TimeSpan FailureDelay = TimeSpan.FromMilliseconds(250);

    private readonly byte[] _passwordHash;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _now;
    private readonly byte[]? _memberHash;

    /// <param name="password">The admin password. Never stored in the clear.</param>
    /// <param name="now">The clock, so the timeouts are testable without sleeping.</param>
    /// <param name="memberToken">
    /// The fleet member token a hub authenticates with, or null when this server
    /// is not a member. Hashed like the password.
    /// </param>
    public AdminSessions(string password, Func<DateTime>? now = null, string? memberToken = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _passwordHash = Hash(password);
        _memberHash = memberToken is null ? null : Hash(memberToken);
    }

    /// <summary>One signed-in browser.</summary>
    public sealed class Session
    {
        public required string Token { get; init; }

        /// <summary>
        /// The double-submit token every mutating request must echo.
        /// </summary>
        /// <remarks>
        /// <b>SameSite=Strict on the cookie is the first defence and this is
        /// the second.</b> The cookie stops a cross-site form post from carrying
        /// credentials at all; this stops anything that gets past it, because a
        /// page on another origin cannot read this value out of the JSON that
        /// issued it.
        /// </remarks>
        public required string Csrf { get; init; }

        public required DateTime Created { get; init; }

        public DateTime LastSeen { get; set; }
    }

    private sealed class Failures
    {
        public int Count;
        public DateTime LockedUntil;
    }

    /// <summary>How many browsers are signed in. For the panel's own display.</summary>
    public int Count => _sessions.Count;

    /// <summary>
    /// Whether <paramref name="address"/> must wait before trying again, and for
    /// how long.
    /// </summary>
    public TimeSpan LockoutRemaining(IPAddress address)
    {
        if (!_failures.TryGetValue(address.ToString(), out var failures))
            return TimeSpan.Zero;

        var remaining = failures.LockedUntil - _now();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Checks a password and, if it is right, issues a session.
    /// </summary>
    /// <remarks>
    /// <b><see cref="CryptographicOperations.FixedTimeEquals"/>, not
    /// <c>==</c>.</b> The panel is reachable from the network by design, so the
    /// comparison is one an attacker can time.
    /// </remarks>
    public Session? SignIn(IPAddress address, string password)
    {
        var key = address.ToString();

        if (!CryptographicOperations.FixedTimeEquals(_passwordHash, Hash(password)))
        {
            RecordFailure(key);
            return null;
        }

        // A correct password clears the record. Otherwise an operator who
        // fat-fingered it five times stays locked out after getting it right,
        // which is a lockout doing the opposite of its job.
        _failures.TryRemove(key, out _);

        var session = new Session
        {
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Csrf = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Created = _now(),
            LastSeen = _now(),
        };

        _sessions[session.Token] = session;
        Evict();

        return session;
    }

    /// <summary>
    /// Whether <paramref name="presented"/> is this server's fleet member token.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No session is issued</b>: a hub sends the token on every request, the
    /// way an API client does, so there is nothing to expire, evict or steal
    /// beyond the token itself. And <b>no CSRF token is needed</b> for the same
    /// reason — CSRF is an attack on a credential the browser attaches by itself,
    /// and a browser never attaches an <c>Authorization</c> header on its own.
    /// </para>
    /// <para>
    /// ⚠️ <b>A wrong token counts against the same lockout as a wrong
    /// password</b>, keyed by address. Otherwise the token would be a second
    /// door with no throttle on it.
    /// </para>
    /// </remarks>
    public bool AuthenticateMember(IPAddress address, string presented)
    {
        var key = address.ToString();

        if (_memberHash is null || !CryptographicOperations.FixedTimeEquals(_memberHash, Hash(presented)))
        {
            RecordFailure(key);
            return false;
        }

        _failures.TryRemove(key, out _);
        return true;
    }

    /// <summary>Whether a member token is configured at all.</summary>
    public bool AcceptsMemberToken => _memberHash is not null;

    private void RecordFailure(string key)
    {
        var failures = _failures.GetOrAdd(key, static _ => new Failures());

        lock (failures)
        {
            failures.Count++;

            if (failures.Count > FreeAttempts)
            {
                var doublings = Math.Min(failures.Count - FreeAttempts - 1, 16);
                var lockout = TimeSpan.FromTicks(
                    Math.Min(FirstLockout.Ticks * (1L << doublings), MaxLockout.Ticks));

                failures.LockedUntil = _now() + lockout;
            }
        }
    }

    /// <summary>
    /// The session that token names, if it is still valid — and touches it.
    /// </summary>
    public Session? Validate(string? token)
    {
        if (token is null || !_sessions.TryGetValue(token, out var session))
            return null;

        var now = _now();

        if (now - session.LastSeen > IdleTimeout || now - session.Created > AbsoluteTimeout)
        {
            _sessions.TryRemove(token, out _);
            return null;
        }

        session.LastSeen = now;
        return session;
    }

    public void SignOut(string? token)
    {
        if (token is not null)
            _sessions.TryRemove(token, out _);
    }

    /// <summary>Drops expired sessions and stale lockouts.</summary>
    public void Sweep()
    {
        var now = _now();

        foreach (var (token, session) in _sessions)
        {
            if (now - session.LastSeen > IdleTimeout || now - session.Created > AbsoluteTimeout)
                _sessions.TryRemove(token, out _);
        }

        foreach (var (key, failures) in _failures)
        {
            // Only a record that is not currently holding anybody back, or the
            // sweep would be a way to clear a lockout by waiting.
            lock (failures)
            {
                if (failures.LockedUntil < now - MaxLockout)
                    _failures.TryRemove(key, out _);
            }
        }
    }

    private void Evict()
    {
        while (_sessions.Count > MaxSessions)
        {
            var oldest = _sessions.Values.MinBy(session => session.LastSeen);

            if (oldest is null || !_sessions.TryRemove(oldest.Token, out _))
                return;
        }
    }

    /// <remarks>
    /// Salted with a per-process random value so that the stored bytes are not a
    /// bare password hash anybody could compare against a table. It is not a
    /// password store — the plaintext is in the operator's own config or command
    /// line — so a slow KDF would only make every request slower for no gain
    /// beyond this.
    /// </remarks>
    private byte[] Hash(string password)
    {
        var bytes = new byte[_salt.Length + Encoding.UTF8.GetByteCount(password)];
        _salt.CopyTo(bytes, 0);
        Encoding.UTF8.GetBytes(password, bytes.AsSpan(_salt.Length));
        return SHA256.HashData(bytes);
    }

    /// <summary>
    /// A password to use when the operator gave none.
    /// </summary>
    /// <remarks>
    /// <b>There is no unauthenticated configuration</b>, so "no password" has
    /// to mean something, and the only safe something is "one you did not
    /// choose". Base32-ish without the characters that are read wrongly out
    /// loud or off a terminal — no O/0, no I/l/1 — because it is going to be
    /// typed by hand from a log line.
    /// </remarks>
    public static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var password = new char[20];

        for (var i = 0; i < password.Length; i++)
        {
            // Grouped for reading aloud: xxxxx-xxxxx-xxxxx-xxxxx.
            password[i] = (i % 6) == 5 ? '-' : alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new string(password);
    }
}
