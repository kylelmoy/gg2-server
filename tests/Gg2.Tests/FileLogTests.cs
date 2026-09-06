using Gg2.Server;

namespace Gg2.Tests;

/// <summary>
/// The optional on-disk log, for deployments with nothing capturing stdout.
/// </summary>
/// <remarks>
/// ⚠️ <b>The properties that matter are the failure ones.</b> A log sink runs
/// on the tick thread, so one that can throw is one that can kill the server —
/// and a log that a restart deletes is a log that is missing exactly when
/// somebody goes looking for why the process stopped.
/// </remarks>
public sealed class FileLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "gg2-filelog-" + Guid.NewGuid().ToString("N")[..8]);

    private string Path_ => Path.Combine(_directory, "server.log");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that would not go is not a test failure.
        }
    }

    /// <summary>Lines land in the file, with their level, and the console still gets them.</summary>
    [Fact]
    public void ItTeesRatherThanReplacing()
    {
        var console = new RecordingLog();

        using (var log = new FileLog(console, Path_))
        {
            log.Info("one");
            log.Warn("two");
            log.Error("three", new IOException("disk"));
        }

        // ⚠️ The console sink is still wired. A file log that silently became
        // the only log would fail exactly when the disk is what has broken.
        Assert.Equal(["one", "two", "three"], console.Lines);
        Assert.Equal(["three"], console.Errors);

        var text = File.ReadAllText(Path_);

        Assert.Contains("info  one", text, StringComparison.Ordinal);
        Assert.Contains("warn  two", text, StringComparison.Ordinal);
        Assert.Contains("error three", text, StringComparison.Ordinal);

        // The stack trace goes to the FILE and not to the panel's ring: this is
        // the sink with room for it and the one still around tomorrow.
        Assert.Contains("System.IO.IOException", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A restart appends rather than truncating.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>The log of why the last process stopped is the log most worth
    /// having</b>, and `FileMode.Create` would delete it at the exact moment a
    /// supervisor restarted the server to recover from whatever wrote it.
    /// </remarks>
    [Fact]
    public void ARestartDoesNotDeleteTheReasonTheLastOneStopped()
    {
        using (var first = new FileLog(new RecordingLog(), Path_))
            first.Error("the tick loop failed repeatedly");

        using (var second = new FileLog(new RecordingLog(), Path_))
            second.Info("listening on 8190");

        var text = File.ReadAllText(Path_);

        Assert.Contains("the tick loop failed repeatedly", text, StringComparison.Ordinal);
        Assert.Contains("listening on 8190", text, StringComparison.Ordinal);
    }

    /// <summary>Past the size limit the file is rotated and a new one started.</summary>
    [Fact]
    public void ItRotatesBySizeAndKeepsTheAgreedNumber()
    {
        var line = new string('x', 200);

        using (var log = new FileLog(new RecordingLog(), Path_, maxBytes: 2048, keep: 2))
        {
            for (var i = 0; i < 200; i++)
                log.Info(line);
        }

        Assert.True(File.Exists(Path_), "the live file should exist");
        Assert.True(File.Exists($"{Path_}.1"), "the most recent rotation should exist");
        Assert.True(File.Exists($"{Path_}.2"), "the second rotation should exist");

        // ⚠️ The bound actually held. Rotation that created files without ever
        // deleting one is the failure this is guarding against — it looks like
        // it works right up until the disk is full.
        Assert.False(File.Exists($"{Path_}.3"), "keep: 2 means two, not two and then some");

        // And the live file was restarted rather than appended past the limit.
        Assert.True(
            new FileInfo(Path_).Length < 2048 * 2,
            "the live file should have been rotated, not grown");
    }

    /// <summary>
    /// A keep of zero means "bounded file, no history" rather than "no
    /// rotation".
    /// </summary>
    [Fact]
    public void KeepZeroRotatesByDeleting()
    {
        using (var log = new FileLog(new RecordingLog(), Path_, maxBytes: 1024, keep: 0))
        {
            for (var i = 0; i < 200; i++)
                log.Info(new string('x', 200));
        }

        Assert.True(File.Exists(Path_));
        Assert.False(File.Exists($"{Path_}.1"));
        Assert.True(new FileInfo(Path_).Length < 1024 * 2);
    }

    /// <summary>
    /// ⚠️ <b>A sink whose rotation keeps failing must not throw and must not
    /// flood.</b>
    /// </summary>
    /// <remarks>
    /// A log sink runs on the tick thread, so one that can throw can kill the
    /// server. The flooding half is subtler and this test is what found it: a
    /// failed rotation leaves the file over its limit, so <em>every</em>
    /// subsequent write tries to rotate again — an unlatched warning there is
    /// one line per line of log, on the sink that still works. That is the log
    /// flood <c>LogThrottle</c> exists to prevent, arriving through the
    /// component that is supposed to be the safe one.
    /// </remarks>
    [Fact]
    public void ARotationThatKeepsFailingIsReportedOnceAndNeverThrows()
    {
        Directory.CreateDirectory(_directory);

        // A DIRECTORY where the first rotated file wants to be. File.Move onto
        // it fails on every platform, every time — which is the "keeps failing"
        // this is about, rather than a one-off.
        Directory.CreateDirectory($"{Path_}.1");

        var console = new RecordingLog();

        using (var log = new FileLog(console, Path_, maxBytes: 1024, keep: 3))
        {
            // Far more than enough writes to attempt many rotations.
            for (var i = 0; i < 300; i++)
                log.Info(new string('x', 200));
        }

        // It never threw — the load-bearing half. RecordingLog.Lines is every
        // level flattened, so the warning below is in there too.
        Assert.Equal(300, console.Lines.Count - console.Warnings.Count);

        // And it complained once, not three hundred times.
        Assert.Single(console.Warnings);
        Assert.Contains("could not rotate", console.Warnings[0], StringComparison.Ordinal);
    }

    /// <summary>Writes after Dispose are a no-op, not a failure to report.</summary>
    [Fact]
    public void WritingAfterDisposeIsSilent()
    {
        var console = new RecordingLog();
        var log = new FileLog(console, Path_);

        log.Dispose();
        log.Info("after dispose");

        // The inner sink still gets it — disposing the file half does not
        // disconnect the console — but nothing is warned about.
        Assert.Equal(["after dispose"], console.Lines);
        Assert.Empty(console.Warnings);
    }

    /// <summary>
    /// A path that cannot be opened at all is refused at startup, not absorbed.
    /// </summary>
    /// <remarks>
    /// The opposite policy from a failure mid-run, and deliberately so: an
    /// unopenable path is a typo in the command line, and starting anyway
    /// leaves the operator with a running server and a log they will go looking
    /// for and never find. Once the server is up, a working server is worth
    /// more than its log and failures are swallowed instead.
    /// </remarks>
    [Fact]
    public void AnUnopenablePathIsRefusedRatherThanSilentlyIgnored()
    {
        Directory.CreateDirectory(_directory);

        var blocker = Path.Combine(_directory, "blocker");
        File.WriteAllText(blocker, "a file, not a directory");

        // Opening "blocker/log" has to fail: blocker is a file.
        var log = new FileLog(new RecordingLog(), Path.Combine(blocker, "log"));

        Assert.True(log.IsBroken, "a path that cannot be opened should report itself broken");

        log.Dispose();
    }
}
