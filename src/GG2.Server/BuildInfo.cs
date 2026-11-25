using System.Reflection;
using System.Runtime.InteropServices;

namespace GG2.Server;

/// <summary>
/// Which build this process is: the commit it was compiled from, the runtime
/// under it, and the architecture it was published for.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists so a deployment can be VERIFIED rather than assumed.</b>
/// Pushing new binaries to several machines and restarting them is easy; being
/// able to say afterwards which commit each one is actually running is the part
/// that was missing. <c>scripts/deploy.sh</c> reads it twice — once by running
/// <c>gg2-server --version</c> on the target before the symlink is flipped,
/// which also proves the binary executes there at all, and once from the
/// startup log line afterwards.
/// </para>
/// <para>
/// ⚠️ <b>The commit is HEAD at build time and says nothing about the working
/// tree.</b> The SDK stamps <see cref="AssemblyInformationalVersionAttribute"/>
/// as <c>&lt;version&gt;+&lt;sha&gt;</c> from git, so a build with uncommitted edits
/// carries the sha of the commit it is NOT. That is why
/// <c>scripts/deploy.sh</c> refuses a dirty tree instead of trusting this
/// string to be self-policing — the check has to happen on the machine that
/// still has the working tree.
/// </para>
/// <para>
/// ⚠️ <b>The assembly read is this one, deliberately</b>, not
/// <c>GetEntryAssembly</c>. Under the test runner the entry assembly is the
/// runner, and under a future host it is whatever embeds the server; the one
/// assembly guaranteed to have been built from this repository is the one this
/// type is declared in.
/// </para>
/// </remarks>
public static class BuildInfo
{
    /// <summary>
    /// The short commit hash this was built from, or <c>"unknown"</c> outside a
    /// git checkout.
    /// </summary>
    public static string Commit { get; } = ReadCommit();

    /// <summary>
    /// The assembly's version without the commit — <c>1.1.1</c>: the nearest
    /// <c>v</c> tag unless the build passed <c>-p:Version</c>
    /// (<c>GG2.Server.csproj</c>, <c>VersionFromGitTag</c>).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>
    /// The one line <c>--version</c> prints and the server logs at startup:
    /// version, commit, runtime and RID.
    /// </summary>
    /// <remarks>
    /// The runtime and the architecture are on it because the two failures a
    /// deployment actually hits are a missing .NET runtime and an x86-64 build
    /// on an arm64 box, and both are invisible in a commit hash.
    /// </remarks>
    public static string Description { get; } =
        $"gg2-server {Version} ({Commit}), .NET {Environment.Version}, "
        + $"{RuntimeInformation.RuntimeIdentifier}";

    private static string ReadVersion()
    {
        var informational = Informational();
        var plus = informational.IndexOf('+');

        return plus < 0 ? informational : informational[..plus];
    }

    private static string ReadCommit()
    {
        var informational = Informational();
        var plus = informational.IndexOf('+');

        if (plus < 0 || plus + 1 >= informational.Length)
            return "unknown";

        var sha = informational[(plus + 1)..];

        // Seven characters, which is what `git rev-parse --short` gives here and
        // what scripts/deploy.sh names a release directory after. The two have
        // to agree or a deploy cannot check that the host is running what it
        // just pushed.
        return sha.Length > 7 ? sha[..7] : sha;
    }

    private static string Informational() =>
        typeof(BuildInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";
}
