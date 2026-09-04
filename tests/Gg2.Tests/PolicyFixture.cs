using System.IO.Compression;
using System.Text;
using Gg2.Bots;
using Gg2.Simulation;

namespace Gg2.Tests;

/// <summary>
/// The one place that owns the checked-in checkpoint, and the only place that
/// knows its stamp is not this build's.
/// </summary>
/// <remarks>
/// <b><c>checkpoints/vlong_s0/ppo_final.npz</c>, checked in from `gg2-rl`.</b> Its
/// <see cref="SimulationStamp.Uuid"/> no longer matches this build's, so it
/// stopped describing this build's physics.
/// <para>
/// <b>The checked-in file is NOT re-stamped, and must not be.</b> It really was
/// trained against a different simulation and its bytes still say so. The
/// trained policy is no longer something this project intends to ship, so the
/// checkpoint here is a <b>fixture</b> — 19,355
/// real parameters and a real numpy oracle — rather than a policy anyone plays.
/// </para>
/// <para>
/// If a retrained
/// checkpoint ever does arrive, <c>PolicyNetworkTests</c> goes red and says so —
/// see <see cref="TrainedStamp"/>.
/// </para>
/// <para>
/// <b>Two accessors, and the difference matters.</b> <see cref="Load"/> takes
/// the file as it is and passes <c>ignoreSimulationStamp</c>, which is honest: the
/// caller wants a network, not a provenance. <see cref="MintedForThisBuild"/>
/// writes a temporary copy carrying this build's stamp, for the code paths that
/// refuse a foreign one <em>by design</em> — <c>EnvServer.LoadPolicy</c> is the
/// only one, and weakening that guard to suit a test would delete the trainer's
/// protection along with the red.
/// </para>
/// </remarks>
internal static class PolicyFixture
{
    /// <summary>
    /// The simulation stamp the checked-in checkpoint was trained against.
    /// </summary>
    /// <remarks>
    /// <b>Hard-coded rather than read out of the file, so that swapping the file
    /// re-opens the question.</b> A helper that discovered the stamp would absorb a
    /// retrain silently, which is the one event that should make somebody look at
    /// this type again.
    /// </remarks>
    internal const string TrainedStamp = "7ff5940d97aa7bed";

    private static readonly Lazy<string> Minted = new(Mint);

    /// <summary>The checked-in checkpoint, exactly as `gg2-rl` handed it over.</summary>
    internal static string Checkpoint =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy", "ppo_final.npz");

    /// <summary>The fixture's directory, which also holds the numpy oracle.</summary>
    internal static string Directory =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "policy");

    /// <summary>
    /// A copy of the fixture's weights carrying <b>this build's</b> stamp.
    /// </summary>
    /// <remarks>
    /// <b>Not a valid policy and never presented as one.</b> It exists so that
    /// <c>EnvServer.LoadPolicy</c>'s accept path — handle numbering, the stamp in
    /// the reply, a continuation reaching the rollout — can be tested without
    /// relaxing the refusal that path exists to perform. It is written under the
    /// temp directory and never committed.
    /// </remarks>
    internal static string MintedForThisBuild => Minted.Value;

    /// <summary>The fixture as a network, stamp mismatch and all.</summary>
    internal static PolicyNetwork Load() => PolicyNetwork.Load(Checkpoint, ignoreSimulationStamp: true);

    /// <summary>Rewrites the archive with this build's stamp in the metadata.</summary>
    /// <remarks>
    /// <b>The archive is rebuilt rather than patched in place.</b> The entries
    /// are stored uncompressed, so an in-place ASCII overwrite would work and leave
    /// every CRC wrong — which .NET happens not to check on read today. Rebuilding
    /// costs a few milliseconds once and does not depend on that.
    /// </remarks>
    private static string Mint()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "gg2-tests", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        System.IO.Directory.CreateDirectory(directory);

        // Keyed by process id so two test processes cannot write the same file,
        // and swept on exit — best effort: a killed test host never runs this, and
        // the next run's directory is a different one, so the sweep is a courtesy
        // rather than a guarantee. 79 KB under %TEMP% either way.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test run over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        };

        var minted = Path.Combine(directory, $"ppo_final-{SimulationStamp.Uuid}.npz");

        using var input = new MemoryStream(File.ReadAllBytes(Checkpoint));
        using var read = new ZipArchive(input, ZipArchiveMode.Read);
        using var output = new FileStream(minted, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var write = new ZipArchive(output, ZipArchiveMode.Create);

        foreach (var entry in read.Entries)
        {
            using var from = entry.Open();
            using var buffer = new MemoryStream();

            from.CopyTo(buffer);

            var bytes = buffer.ToArray();

            if (entry.Name.Equals("meta.npy", StringComparison.Ordinal))
                bytes = Restamp(bytes);

            using var to = write.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();

            to.Write(bytes, 0, bytes.Length);
        }

        return minted;
    }

    /// <summary>Replaces the one occurrence of the trained stamp with this build's.</summary>
    private static byte[] Restamp(byte[] meta)
    {
        var trained = Encoding.ASCII.GetBytes(TrainedStamp);
        var mine = Encoding.ASCII.GetBytes(SimulationStamp.Uuid);

        if (trained.Length != mine.Length)
        {
            throw new InvalidOperationException(
                $"the stamp changed length: {TrainedStamp} is {trained.Length} bytes and "
                + $"{SimulationStamp.Uuid} is {mine.Length}; this rewrite assumes they match");
        }

        var at = meta.AsSpan().IndexOf(trained);

        if (at < 0)
        {
            throw new InvalidOperationException(
                $"the fixture's metadata does not carry {TrainedStamp}; the checkpoint has been "
                + "replaced, so PolicyFixture.TrainedStamp needs updating along with whatever "
                + "else that replacement implies");
        }

        if (meta.AsSpan(at + trained.Length).IndexOf(trained) >= 0)
            throw new InvalidOperationException($"{TrainedStamp} appears more than once; a blind rewrite is unsafe");

        var copy = meta.ToArray();

        mine.CopyTo(copy.AsSpan(at));

        return copy;
    }
}
