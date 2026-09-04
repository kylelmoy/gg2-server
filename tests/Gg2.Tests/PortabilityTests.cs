using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Gg2.Bots;
using Gg2.Host;
using Gg2.Maps;
using Gg2.Protocol;
using Gg2.Server;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The server runs on Linux, and these are the gates that say so mechanically.
/// </summary>
/// <remarks>
/// <b>Written because the claim had nothing behind it.</b> Nothing in the
/// repo asserted the server was portable; a <c>dotnet publish -r linux-x64</c>
/// happened to succeed, which is evidence about one afternoon rather than a
/// gate. The two ways it could quietly stop being true are an unguarded
/// P/Invoke and a hard-coded Windows path, and both are cheap to check by
/// reflection.
/// <para>
/// <b>What these do NOT establish.</b> They are static checks run on Windows.
/// They cannot see a case-sensitivity assumption, a permissions difference, or
/// anything about actually running the binary on Linux — <c>docs/DEPLOY.md</c>,
/// "What is verified and what is not", says which is which. Do not let a green
/// run here be quoted as "tested on Linux".
/// </para>
/// </remarks>
public class PortabilityTests
{
    /// <summary>Every assembly a deployed server actually loads.</summary>
    /// <remarks>
    /// <c>Gg2.Parity</c> and <c>Gg2.Env</c> are deliberately absent: they are
    /// development tooling that never ships in the server's publish output, so a
    /// platform dependency in one of them is not a deployment problem. Add an
    /// assembly here when it starts shipping, not when it starts existing.
    /// </remarks>
    private static Assembly[] ShippedAssemblies =>
    [
        typeof(GameServer).Assembly,
        typeof(ScriptedBot).Assembly,
        typeof(MatchSimulation).Assembly,
        typeof(GameConstants).Assembly,
        typeof(LevelMap).Assembly,
        typeof(ShutdownSignals).Assembly,
    ];

    private static IEnumerable<MethodInfo> PlatformInvokes(Assembly assembly) =>
        assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly))
            .Where(method => method.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

    /// <summary>
    /// Every native call in a shipped assembly is annotated Windows-only, so the
    /// platform analyzer refuses an unguarded call site.
    /// </summary>
    /// <remarks>
    /// The annotation is what does the work: <c>[SupportedOSPlatform("windows")]</c>
    /// makes CA1416 an ERROR at every unguarded caller under this repo's
    /// <c>TreatWarningsAsErrors</c>, so a guard that gets deleted fails the build
    /// rather than the Linux server. This test protects the annotation itself,
    /// which the analyzer cannot.
    /// </remarks>
    [Fact]
    public void EveryNativeCallIsAnnotatedWindowsOnly()
    {
        foreach (var assembly in ShippedAssemblies)
        {
            foreach (var method in PlatformInvokes(assembly))
            {
                var annotated =
                    method.GetCustomAttribute<SupportedOSPlatformAttribute>() is not null
                    || method.DeclaringType?.GetCustomAttribute<SupportedOSPlatformAttribute>() is not null;

                Assert.True(
                    annotated,
                    $"{method.DeclaringType?.FullName}.{method.Name} is a P/Invoke with no "
                    + "[SupportedOSPlatform]. Annotate it and guard every call with "
                    + "OperatingSystem.IsWindows(), or the Linux server takes the call.");
            }
        }
    }

    /// <summary>
    /// The test above passes trivially if the count is zero, so pin the one
    /// P/Invoke that exists.
    /// </summary>
    /// <remarks>
    /// CLAUDE.md: <b>ask what would go red if the claim were false.</b> A gate
    /// over an empty set is satisfied by deleting the thing it guards, which is
    /// exactly how it would stop protecting anything without failing. If this
    /// row moves, the change is real and the row is the re-record — say so in the
    /// commit, and check the new entry is guarded the way <c>TimerResolution</c>
    /// is.
    /// </remarks>
    [Fact]
    public void TheOnlyNativeCallsAreTheWindowsTimerResolution()
    {
        var found = ShippedAssemblies
            .SelectMany(PlatformInvokes)
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                // The web panel's "total / available RAM" tile. Guarded the way
                // TimerResolution is: the extern is annotated, ReadMemoryWindows
                // carries the same attribute, and its only call sits under an
                // OperatingSystem.IsWindows() branch that falls back to
                // /proc/meminfo.
                "SystemInfo.GlobalMemoryStatusEx",
                "TimerResolution.BeginPeriod",
                "TimerResolution.EndPeriod",
            ],
            found);
    }

    /// <summary>
    /// No shipped assembly has a Windows absolute path as a <em>string literal</em>,
    /// which is how the map and nav directories were hard-coded before they were
    /// resolved.
    /// </summary>
    /// <remarks>
    /// <b>This was a real defect, not a hypothetical one.</b> Every entry point
    /// once defaulted <c>MapDirectory</c> to
    /// <c>D:\code\Gang-Garrison-2\Source\gg2\Included Files</c> — see
    /// <c>MapDirectory</c>'s remarks — so the server could not serve a round on
    /// any machine but one, let alone another operating system. Reading the
    /// literals out of the metadata is the cheapest thing that would have caught
    /// it.
    /// <para>
    /// <b>The #US heap specifically, NOT the file's bytes.</b> A first draft
    /// scanned the whole assembly and failed immediately on
    /// <c>...\obj\Debug\net10.0\Gg2.Server.pdb</c> — the debug directory's
    /// path, which every build embeds and which no code ever reads. Scanning
    /// bytes cannot tell a literal the program uses from build metadata beside
    /// it, and the version of this test that reported the second would have been
    /// deleted rather than believed.
    /// </para>
    /// <para>
    /// Deliberately narrow even here: a drive-letter prefix, not "contains a
    /// backslash". Backslashes are legitimate literals in escape handling
    /// (<c>Ggon</c>) and in the map-name path-separator guard
    /// (<c>GameServer.LoadMap</c>), and a check that fired on those would be
    /// turned off rather than fixed.
    /// </para>
    /// </remarks>
    [Fact]
    public void NoShippedAssemblyHardCodesAWindowsPath()
    {
        foreach (var assembly in ShippedAssemblies)
        {
            using var stream = File.OpenRead(assembly.Location);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();

            for (var handle = MetadataTokens.UserStringHandle(0);
                 !handle.IsNil;
                 handle = reader.GetNextHandle(handle))
            {
                var literal = reader.GetUserString(handle);

                if (LooksLikeAWindowsPath(literal))
                {
                    Assert.Fail(
                        $"{Path.GetFileName(assembly.Location)} has the literal '{literal}'. "
                        + "Resolve the path at runtime instead — MapDirectory.Resolve is the "
                        + "pattern, and GG2_MAPS / GG2_NAVCACHE are the overrides.");
                }
            }
        }
    }

    /// <summary>
    /// The control arm for the check above: the detector fires on the literal
    /// that used to be in the code, and does not fire on the backslash literals
    /// that legitimately are.
    /// </summary>
    /// <remarks>
    /// A path detector that silently matched nothing would leave the test green
    /// for ever — the "unsatisfiable-by-the-right-evidence" gate CLAUDE.md warns
    /// about. This pins it in both directions and costs nothing.
    /// </remarks>
    [Fact]
    public void TheWindowsPathDetectorWorks()
    {
        Assert.True(LooksLikeAWindowsPath(@"D:\code\Gang-Garrison-2\Source\gg2\Included Files"));
        Assert.True(LooksLikeAWindowsPath(@"c:\temp"));

        Assert.False(LooksLikeAWindowsPath(@"\"));                   // Ggon's escape handling
        Assert.False(LooksLikeAWindowsPath("maps"));                 // MapDirectory's probe
        Assert.False(LooksLikeAWindowsPath("Source/gg2/Included Files"));
        Assert.False(LooksLikeAWindowsPath("a:b"));                  // a colon is not a drive
    }

    private static bool LooksLikeAWindowsPath(string value)
    {
        for (var i = 0; i + 2 < value.Length; i++)
        {
            if (value[i + 1] == ':'
                && value[i + 2] == '\\'
                && value[i] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'))
            {
                return true;
            }
        }

        return false;
    }
}
