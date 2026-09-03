using System.Net;

namespace Gg2.Server;

/// <summary>
/// The <c>[Web]</c> section — <c>gg2-server --web &lt;port&gt;</c>, the remote
/// admin panel. Off unless a port is given.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This is the one surface in the project that is deliberately reachable
/// from the network.</b> The control port binds <see cref="IPAddress.Loopback"/>
/// so that no configuration can expose it; this one takes a bind address
/// precisely because "manage the server from somewhere else" is what it is for.
/// Everything that follows from that — a password that cannot be empty, a
/// session cookie, a login throttle, optional TLS — is in
/// <see cref="WebAdminServer"/>, and none of it is optional.
/// </para>
/// <para>
/// ⚠️ <b><see cref="Password"/> empty does not mean "no password".</b> It means
/// "generate one and print it at startup". There is no way to configure an
/// unauthenticated panel, which is a property of
/// <see cref="WebAdminServer.Start"/> rather than of a check somebody can
/// forget — the same argument the control port's bind makes.
/// </para>
/// </remarks>
/// <param name="Enabled">Whether to host the panel at all.</param>
/// <param name="Port">The port to bind. 0 asks the OS for a free one.</param>
/// <param name="Bind">
/// The address to bind. ⚠️ <b>Defaults to loopback</b>, so <c>--web 8191</c> on
/// its own is not remotely reachable and turning it into a remote panel is a
/// second, explicit decision (<c>--web-bind 0.0.0.0</c>).
/// </param>
/// <param name="Password">
/// The admin password, or empty to have one generated and logged.
/// </param>
/// <param name="CertificatePath">
/// A PKCS#12 (<c>.pfx</c>) certificate to serve HTTPS with, or empty for plain
/// HTTP. ⚠️ <b>A password crosses the network on every login</b>, so a non-loopback
/// bind without one is warned about, loudly, every time the server starts.
/// </param>
/// <param name="CertificatePassword">The certificate's password, if it has one.</param>
public readonly record struct WebAdminOptions(
    bool Enabled,
    int Port,
    string Bind,
    string Password,
    string CertificatePath,
    string CertificatePassword)
{
    /// <summary>The port <c>--web</c> uses when it is given no number.</summary>
    /// <remarks>
    /// One above the game's own 8190, so the pair is memorable and neither
    /// collides with anything the reference uses.
    /// </remarks>
    public const int DefaultPort = 8191;

    /// <summary>Loopback: the default, and the one that is not a remote panel.</summary>
    public const string DefaultBind = "127.0.0.1";

    /// <summary>
    /// Off — the default, and what <c>new WebAdminOptions()</c> would <em>not</em>
    /// give you.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Use this, never <c>new WebAdminOptions()</c>.</b> A zeroed one has a
    /// null <see cref="Bind"/>, which throws inside
    /// <see cref="IPAddress.Parse(string)"/> at startup rather than at
    /// construction — the same trap <c>BotPopulationOptions.Default</c> exists
    /// for.
    /// </remarks>
    public static WebAdminOptions Disabled => new(
        Enabled: false,
        Port: DefaultPort,
        Bind: DefaultBind,
        Password: string.Empty,
        CertificatePath: string.Empty,
        CertificatePassword: string.Empty);

    /// <summary>Whether <see cref="Bind"/> reaches beyond this machine.</summary>
    /// <remarks>
    /// The question the TLS warning asks. <c>IPAddress.IsLoopback</c> covers
    /// 127.0.0.0/8 and <c>::1</c>; anything else — <c>0.0.0.0</c>, a LAN address,
    /// a public one — is remote.
    /// </remarks>
    public bool IsRemote =>
        !IPAddress.TryParse(Bind, out var address) || !IPAddress.IsLoopback(address);

    /// <summary>Throws if this configuration cannot be served.</summary>
    public void Validate()
    {
        if (!Enabled)
            return;

        if (Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "web port must be 0..65535");

        if (!IPAddress.TryParse(Bind, out _))
        {
            throw new ArgumentException(
                $"--web-bind: '{Bind}' is not an IP address. Use 127.0.0.1 for a local "
                + "panel, or 0.0.0.0 to accept connections from anywhere.",
                nameof(Bind));
        }

        if (CertificatePath.Length == 0 && CertificatePassword.Length != 0)
        {
            throw new ArgumentException(
                "--web-tls-password was given without --web-tls, so there is no "
                + "certificate for it to open.",
                nameof(CertificatePassword));
        }
    }
}
