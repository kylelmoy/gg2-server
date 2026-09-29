using System.Text.RegularExpressions;
using GG2.Host;
using GG2.Server;

namespace GG2.Tests;

/// <summary>
/// What <c>gg2-server --version</c> says, and the contract
/// <c>scripts/deploy.sh</c> reads it under.
/// </summary>
/// <remarks>
/// <para>
/// <b>The deploy script parses this string on the target.</b> It names each
/// release directory for <c>git rev-parse --short HEAD</c>, runs the unpacked
/// binary's <c>--version</c> on the machine it will run on, extracts the commit
/// from the parentheses and refuses to flip the symlink unless the two agree.
/// That is the check that catches a stale <c>--from</c> directory, a truncated
/// transfer and a missing .NET runtime — and it is a regex in a shell script
/// against a format string in C#, with nothing but these tests between them.
/// </para>
/// <para>
/// ⚠️ <b>What is NOT asserted here: that the commit equals HEAD.</b> It would
/// be true and it would be a flake — the assembly carries the sha it was
/// compiled at, so any test run over a build older than the last commit fails
/// for a reason that is not a defect. The equality is enforced where it means
/// something: on the target, by the script, against the release it just pushed.
/// </para>
/// </remarks>
public class BuildInfoTests
{
    /// <summary>
    /// The regex in <c>scripts/deploy.sh</c>'s <c>commit_of</c>, character for
    /// character apart from sed's backslashes.
    /// </summary>
    private const string DeployScriptPattern = ".*\\(([0-9a-f]*)\\).*";

    /// <summary>
    /// <c>--version</c>'s line survives the extraction the deploy script does
    /// to it, and yields the commit rather than something that merely looks like
    /// one.
    /// </summary>
    /// <remarks>
    /// The two ways this has to be able to go wrong: a format change that moves
    /// the commit out of the parentheses, and a runtime identifier or a .NET
    /// version that puts an earlier bracketed group in front of it. Both are
    /// invisible until a deploy refuses on a machine, which is the worst place
    /// to discover a format change.
    /// </remarks>
    [Fact]
    public void TheDeployScriptCanReadTheCommitOutOfTheVersionLine()
    {
        var match = Regex.Match(BuildInfo.Description, DeployScriptPattern);

        Assert.True(match.Success, $"no bracketed commit in '{BuildInfo.Description}'");
        Assert.Equal(BuildInfo.Commit, match.Groups[1].Value);
    }

    /// <summary>
    /// The commit is seven lowercase hex characters — what <c>git rev-parse
    /// --short</c> gives and what a release directory is named — or the honest
    /// <c>unknown</c> outside a checkout.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Seven is the contract, not a coincidence.</b> The script names
    /// <c>releases/&lt;short sha&gt;</c> from git and compares it to what the
    /// binary reports; a build that started reporting the full forty would
    /// refuse every deploy, and one that reported five would collide sooner than
    /// git does.
    /// </remarks>
    [Fact]
    public void TheCommitIsAShortShaOrAdmitsItIsNotKnown()
    {
        if (BuildInfo.Commit == "unknown")
            return;

        Assert.Matches("^[0-9a-f]{7}$", BuildInfo.Commit);
    }

    /// <summary>
    /// The line carries the runtime and the architecture, because those are the
    /// two deployment failures a commit hash cannot see.
    /// </summary>
    [Fact]
    public void TheVersionLineNamesTheRuntimeAndTheArchitecture()
    {
        Assert.Contains(".NET ", BuildInfo.Description);
        Assert.Contains(Environment.Version.ToString(), BuildInfo.Description);
        Assert.Contains(
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            BuildInfo.Description);
    }

    /// <summary>
    /// <c>--version</c> has to be answered BEFORE the parser, and this is what
    /// says so: the parser refuses it.
    /// </summary>
    /// <remarks>
    /// <c>Program.Main</c> short-circuits on it ahead of
    /// <see cref="CommandLine.Parse"/> deliberately — the flag is asked of a
    /// binary that has just been copied to a machine, where there is no config
    /// file, no maps directory and nothing else to be right about. Moving the
    /// check below the parser would turn it into exit 2 and a page of usage
    /// text, and every deploy would then fail its pre-flip probe.
    /// </remarks>
    [Fact]
    public void TheParserItselfDoesNotAcceptVersion()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["--version"]));
    }
}
