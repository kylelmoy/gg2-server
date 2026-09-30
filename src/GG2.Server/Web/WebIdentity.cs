using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace GG2.Server;

/// <summary>
/// What the admin panel keeps on disk about itself: a self-signed certificate
/// (<c>[Web] Certificate=auto</c>) and a fleet member's pairing token
/// (<c>[Web] FleetMember=1</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Both live beside the config file, never IN it.</b> A fleet hub renders and
/// pushes every member's <c>gg2.ini</c>; if the certificate or the token were a
/// setting, a push would be able to change the very identity the hub pins, and a
/// push that dropped the line would unpair the host it was sent to. Kept as their
/// own files they survive any config push, any panel save and any restart, and a
/// pairing is undone only by deleting the file.
/// </para>
/// <para>
/// <b>Why the certificate has to persist.</b> A hub trusts a member by the SHA-256
/// of its certificate, the way ssh trusts a host key, so a certificate generated
/// fresh on every start would look to the hub exactly like somebody in the middle.
/// With no CA and no domain name, pinning is what makes a self-signed certificate
/// worth having; that is the whole of "TLS with no setup".
/// </para>
/// <para>
/// ⚠️ <b>Both files are written owner-only on Unix</b> (0600). The certificate's
/// private key and the token are each a credential by themselves.
/// </para>
/// </remarks>
public static class WebIdentity
{
    /// <summary>The self-signed certificate's file name, in <see cref="Directory"/>.</summary>
    public const string CertificateFile = "web-cert.pfx";

    /// <summary>The fleet member token's file name, in <see cref="Directory"/>.</summary>
    public const string TokenFile = "fleet-member.token";

    /// <summary>
    /// Where the identity files live: the config file's directory, or the working
    /// directory for a server started with no <c>--config</c>.
    /// </summary>
    public static string Directory(ServerOptions options) =>
        options.ConfigFile.Length != 0
            ? Path.GetDirectoryName(Path.GetFullPath(options.ConfigFile)) ?? "."
            : System.IO.Directory.GetCurrentDirectory();

    /// <summary>
    /// Loads the self-signed certificate from <paramref name="directory"/>, making
    /// one first if there is none.
    /// </summary>
    /// <remarks>
    /// ECDSA P-256, ten years. The expiry is not the protection here — a hub
    /// matches the exact certificate, so a stolen one is as good as new until
    /// the file is deleted — and a short one would unpair every member on the day
    /// it lapsed, silently, on somebody else's server.
    /// </remarks>
    public static X509Certificate2 LoadOrCreateCertificate(string directory, out bool created)
    {
        var path = Path.Combine(directory, CertificateFile);
        created = false;

        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=gg2-server", key, HashAlgorithmName.SHA256);

            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("gg2-server");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

            var now = DateTimeOffset.UtcNow;
            using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));

            WriteOwnerOnly(path, certificate.Export(X509ContentType.Pkcs12));
            created = true;
        }

        // Loaded back from the file even when it was just made: on Windows, a
        // certificate straight out of CreateSelfSigned holds an ephemeral key that
        // SslStream cannot use as a server, and one read from PKCS#12 can.
        return X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
    }

    /// <summary>The certificate's SHA-256, as lower-case hex — the value a hub pins.</summary>
    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>
    /// Loads the member token from <paramref name="directory"/>, making one first
    /// if there is none.
    /// </summary>
    /// <remarks>
    /// 256 random bits as base64url, so it is one word on a command line and in a
    /// URL-safe alphabet. A file that exists but holds something too short to be
    /// one of these is REFUSED rather than replaced: replacing it would quietly
    /// unpair the host from its hub, and a short one would be a weak credential.
    /// </remarks>
    public static string LoadOrCreateToken(string directory, out bool created)
    {
        var path = Path.Combine(directory, TokenFile);
        created = false;

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();

            if (existing.Length < 32)
            {
                throw new InvalidOperationException(
                    $"{path} holds {existing.Length} characters, which is not a fleet member token. "
                    + "Delete it to have a new one generated; the hub will need the new pairing code.");
            }

            return existing;
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        WriteOwnerOnly(path, Encoding.ASCII.GetBytes(token + "\n"));
        created = true;
        return token;
    }

    /// <summary>
    /// What an operator pastes into the hub: the token, then the certificate's
    /// fingerprint, joined by a dot.
    /// </summary>
    /// <remarks>
    /// One string, so pairing is a single paste and the hub cannot be given a
    /// token without the fingerprint that says which server it belongs to.
    /// </remarks>
    public static string PairingCode(string token, string fingerprint) => token + "." + fingerprint;

    /// <summary>Splits a <see cref="PairingCode"/>; false if it is not one.</summary>
    public static bool TryParsePairingCode(string? code, out string token, out string fingerprint)
    {
        token = fingerprint = string.Empty;

        if (code is null)
            return false;

        var dot = code.Trim().LastIndexOf('.');

        if (dot < 32)
            return false;

        var candidate = code.Trim()[(dot + 1)..];

        if (candidate.Length != 64 || !candidate.All(char.IsAsciiHexDigit))
            return false;

        token = code.Trim()[..dot];
        fingerprint = candidate.ToLowerInvariant();
        return true;
    }

    private static void WriteOwnerOnly(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };

        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(bytes);
        }

        File.Move(temporary, path, overwrite: false);
    }
}
