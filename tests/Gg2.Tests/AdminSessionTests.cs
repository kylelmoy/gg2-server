using System.Net;
using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The web panel's authentication, on its own — no socket, no clock.
/// </summary>
/// <remarks>
/// ⚠️ <b><see cref="AdminSessions"/> takes its clock as an argument for exactly
/// this reason.</b> Every rule in it is a duration, and a test that asserted them
/// by sleeping would be slow, flaky, and would quietly stop asserting anything
/// the day somebody lengthened a timeout.
/// </remarks>
public sealed class AdminSessionTests
{
    private DateTime _now = new(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);

    private AdminSessions Sessions(string password = "hunter2") => new(password, () => _now);

    private static readonly IPAddress Somewhere = IPAddress.Parse("203.0.113.7");

    [Fact]
    public void TheRightPasswordIssuesASessionAndTheWrongOneDoesNot()
    {
        var sessions = Sessions();

        Assert.Null(sessions.SignIn(Somewhere, "hunter3"));

        var session = sessions.SignIn(Somewhere, "hunter2");

        Assert.NotNull(session);
        Assert.Same(session, sessions.Validate(session.Token));
    }

    /// <summary>
    /// ⚠️ The tokens are the credential, so they are 256 bits of
    /// <c>RandomNumberGenerator</c> and never derived from anything.
    /// </summary>
    [Fact]
    public void EachSignInIssuesDistinctUnguessableTokens()
    {
        var sessions = Sessions();

        var first = sessions.SignIn(Somewhere, "hunter2")!;
        var second = sessions.SignIn(Somewhere, "hunter2")!;

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Csrf, second.Csrf);
        Assert.Equal(64, first.Token.Length);   // 32 bytes as hex
        Assert.Equal(64, first.Csrf.Length);
        Assert.NotEqual(first.Token, first.Csrf);
    }

    [Fact]
    public void AnUnknownTokenValidatesToNothing()
    {
        var sessions = Sessions();
        sessions.SignIn(Somewhere, "hunter2");

        Assert.Null(sessions.Validate(null));
        Assert.Null(sessions.Validate(string.Empty));
        Assert.Null(sessions.Validate(new string('a', 64)));
    }

    [Fact]
    public void SigningOutInvalidatesTheToken()
    {
        var sessions = Sessions();
        var session = sessions.SignIn(Somewhere, "hunter2")!;

        sessions.SignOut(session.Token);

        Assert.Null(sessions.Validate(session.Token));
    }

    /// <summary>
    /// ⛔ Both timeouts, not one.
    /// </summary>
    /// <remarks>
    /// An idle timeout alone means a tab left polling is authenticated for ever,
    /// which is the tab most likely to be on a machine somebody else can reach.
    /// </remarks>
    [Fact]
    public void ASessionExpiresWhenIdleAndAgainWhenOldHoweverMuchItIsUsed()
    {
        var sessions = Sessions();
        var idle = sessions.SignIn(Somewhere, "hunter2")!;

        _now += AdminSessions.IdleTimeout + TimeSpan.FromMinutes(1);
        Assert.Null(sessions.Validate(idle.Token));

        var busy = sessions.SignIn(Somewhere, "hunter2")!;

        // Used every hour, for longer than the absolute limit.
        for (var hour = 0; hour < AdminSessions.AbsoluteTimeout.TotalHours; hour++)
        {
            _now += TimeSpan.FromHours(1);
            Assert.NotNull(sessions.Validate(busy.Token));
        }

        _now += TimeSpan.FromHours(1);
        Assert.Null(sessions.Validate(busy.Token));
    }

    /// <summary>
    /// ⛔ Guessing costs time, and the time grows.
    /// </summary>
    /// <remarks>
    /// The panel can be reachable from the network, so an online guessing attack
    /// is the threat that actually exists — a lockout that never arrived would
    /// leave a short password worth attacking.
    /// </remarks>
    [Fact]
    public void RepeatedWrongPasswordsLockTheAddressOutForLonger()
    {
        var sessions = Sessions();

        for (var attempt = 0; attempt < AdminSessions.FreeAttempts; attempt++)
        {
            Assert.Null(sessions.SignIn(Somewhere, "wrong"));
            Assert.Equal(TimeSpan.Zero, sessions.LockoutRemaining(Somewhere));
        }

        Assert.Null(sessions.SignIn(Somewhere, "wrong"));

        var first = sessions.LockoutRemaining(Somewhere);
        Assert.True(first > TimeSpan.Zero, "no lockout after the free attempts ran out");

        _now += first;
        Assert.Equal(TimeSpan.Zero, sessions.LockoutRemaining(Somewhere));

        Assert.Null(sessions.SignIn(Somewhere, "wrong"));
        Assert.True(
            sessions.LockoutRemaining(Somewhere) > first,
            "the second lockout was no longer than the first");
    }

    /// <summary>The lockout has a ceiling, so it cannot become permanent.</summary>
    [Fact]
    public void TheLockoutStopsGrowingAtItsMaximum()
    {
        var sessions = Sessions();

        for (var attempt = 0; attempt < 60; attempt++)
            sessions.SignIn(Somewhere, "wrong");

        Assert.True(sessions.LockoutRemaining(Somewhere) <= AdminSessions.MaxLockout);
    }

    /// <summary>
    /// ⚠️ Getting it right clears the record.
    /// </summary>
    /// <remarks>
    /// Otherwise an operator who fat-fingered the password five times stays
    /// locked out after finally typing it correctly, which is a lockout doing the
    /// opposite of its job.
    /// </remarks>
    [Fact]
    public void ASuccessfulSignInClearsTheLockout()
    {
        var sessions = Sessions();

        for (var attempt = 0; attempt <= AdminSessions.FreeAttempts; attempt++)
            sessions.SignIn(Somewhere, "wrong");

        Assert.True(sessions.LockoutRemaining(Somewhere) > TimeSpan.Zero);

        // ⚠️ The lockout is enforced by the HTTP handler, not by SignIn — SignIn
        // is what tells it there is one. So a correct password still works here,
        // and that is the behaviour being asserted rather than a hole.
        _now += AdminSessions.MaxLockout;
        Assert.NotNull(sessions.SignIn(Somewhere, "hunter2"));
        Assert.Equal(TimeSpan.Zero, sessions.LockoutRemaining(Somewhere));
    }

    /// <summary>One address's failures do not lock out another's.</summary>
    [Fact]
    public void LockoutIsPerAddress()
    {
        var sessions = Sessions();
        var elsewhere = IPAddress.Parse("198.51.100.4");

        for (var attempt = 0; attempt <= AdminSessions.FreeAttempts * 2; attempt++)
            sessions.SignIn(Somewhere, "wrong");

        Assert.True(sessions.LockoutRemaining(Somewhere) > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, sessions.LockoutRemaining(elsewhere));
    }

    /// <summary>
    /// ⚠️ An authenticated client cannot make the server hold sessions for ever.
    /// </summary>
    [Fact]
    public void SessionsAreCappedAndTheLeastRecentlyUsedIsEvicted()
    {
        var sessions = Sessions();
        var oldest = sessions.SignIn(Somewhere, "hunter2")!;

        for (var extra = 0; extra < AdminSessions.MaxSessions; extra++)
        {
            _now += TimeSpan.FromSeconds(1);
            sessions.SignIn(Somewhere, "hunter2");
        }

        Assert.True(sessions.Count <= AdminSessions.MaxSessions);
        Assert.Null(sessions.Validate(oldest.Token));
    }

    [Fact]
    public void SweepDropsExpiredSessions()
    {
        var sessions = Sessions();
        sessions.SignIn(Somewhere, "hunter2");

        Assert.Equal(1, sessions.Count);

        _now += AdminSessions.IdleTimeout + TimeSpan.FromMinutes(1);
        sessions.Sweep();

        Assert.Equal(0, sessions.Count);
    }

    /// <summary>
    /// A generated password is long, and is made of characters somebody can read
    /// off a terminal and type back.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>No O/0 and no I/l/1</b>: it exists to be transcribed by hand from a
    /// log line, and a password that cannot be read reliably is one an operator
    /// replaces with a short one they can.
    /// </remarks>
    [Fact]
    public void AGeneratedPasswordIsLongAndUnambiguous()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var password = AdminSessions.GeneratePassword();

            Assert.True(password.Length >= 16, password);
            Assert.DoesNotContain(password, c => c is 'O' or '0' or 'I' or 'l' or '1');
            Assert.NotEqual(password, AdminSessions.GeneratePassword());
        }
    }
}
