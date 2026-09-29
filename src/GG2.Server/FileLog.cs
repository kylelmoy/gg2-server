using System.Text;

namespace GG2.Server;

/// <summary>
/// A log sink that writes to a file and rotates it by size.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not needed under systemd or Docker</b>, and that is worth saying at the
/// top so nobody turns it on out of habit: journald and a container runtime
/// both capture stdout and stderr, already rotate, and already let an operator
/// filter by stream. This exists for the deployments that have neither — a
/// bare console, a Windows service, a screen session — where the alternative is
/// no log at all once the terminal scrolls.
/// </para>
/// <para>
/// ⚠️ <b>It tees; it never replaces.</b> The console sink stays wired, because
/// the console is the record an operator has when the thing that has broken is
/// the disk. A file log that silently became the only log would fail exactly
/// when it mattered.
/// </para>
/// <para>
/// <b>Every failure is swallowed after one warning.</b> A full disk, a
/// read-only path, a file someone deleted underneath it — none of those is a
/// reason to take a game server down, and a log sink that can throw is a log
/// sink that can kill the tick. The warning goes to the inner sink, which is
/// still working.
/// </para>
/// <para>
/// <b>Locked, like <see cref="LogRing"/> and for the same reason.</b> Most
/// logging is on the tick thread but not all of it —
/// <see cref="LobbyAnnouncer"/> and <see cref="RewardAuthClient"/> write from
/// their own — and interleaved writes would produce torn lines.
/// </para>
/// </remarks>
public sealed class FileLog : IServerLog, IDisposable
{
    /// <summary>Rotated files kept beside the live one, by default.</summary>
    public const int DefaultKeep = 5;

    /// <summary>Size at which the live file is rotated, by default.</summary>
    public const long DefaultMaxBytes = 16 * 1024 * 1024;

    private readonly IServerLog _inner;
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _keep;
    private readonly Lock _gate = new();

    private StreamWriter? _writer;
    private long _written;
    private bool _broken;
    private bool _disposed;

    /// <summary>
    /// Whether the last rotation failed, so the complaint is not repeated.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>A rotation that keeps failing keeps being attempted.</b> Reopening
    /// after a failed rotation appends, so the file stays over its limit and
    /// every subsequent write tries to rotate again — which without this latch
    /// is one warning per line on the sink that still works, i.e. exactly the
    /// log flood <see cref="LogThrottle"/> exists to prevent, arriving through
    /// the component meant to be the safe one. Cleared by a rotation that
    /// succeeds, so a transient failure still reports the next real one.
    /// </remarks>
    private bool _rotationFailed;

    /// <param name="inner">The sink that keeps working when the disk does not.</param>
    /// <param name="path">The live log file. Its directory is created if missing.</param>
    /// <param name="maxBytes">Rotate once the live file passes this.</param>
    /// <param name="keep">
    /// How many rotated files to keep. <c>path.1</c> is the most recent;
    /// <c>path.{keep}</c> is the oldest and is deleted on the next rotation.
    /// </param>
    public FileLog(IServerLog inner, string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        _inner = inner;
        _path = Path.GetFullPath(path);
        _maxBytes = maxBytes;
        _keep = keep;

        Open();
    }

    /// <summary>Whether writing has failed and been given up on.</summary>
    /// <remarks>
    /// For the panel and for tests. A server whose file log died is still a
    /// working server, so this is information rather than an error state.
    /// </remarks>
    public bool IsBroken
    {
        get
        {
            lock (_gate)
                return _broken;
        }
    }

    public void Info(string message)
    {
        _inner.Info(message);
        Write("info ", message, null);
    }

    public void Warn(string message)
    {
        _inner.Warn(message);
        Write("warn ", message, null);
    }

    public void Error(string message, Exception? error = null)
    {
        _inner.Error(message, error);
        Write("error", message, error);
    }

    private void Write(string level, string message, Exception? error)
    {
        lock (_gate)
        {
            if (_broken || _disposed || _writer is null)
                return;

            try
            {
                // The same shape the console uses, so a file and a journal can
                // be read side by side without a second format in anybody's
                // head. ISO-8601 with the offset sorts lexicographically and
                // survives a timezone change.
                var line = $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} {level} {message}";

                _writer.WriteLine(line);
                _written += line.Length + Environment.NewLine.Length;

                // ⚠️ The trace goes to the FILE and not to the ring or the
                // panel. This is the sink with room for it, and it is the one
                // an operator still has tomorrow.
                if (error is not null)
                {
                    var trace = error.ToString();
                    _writer.WriteLine(trace);
                    _written += trace.Length + Environment.NewLine.Length;
                }

                if (_written >= _maxBytes)
                    Rotate();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                Break(e);
            }
        }
    }

    /// <summary>
    /// Closes the live file, shuffles the kept ones along, and opens a new one.
    /// </summary>
    /// <remarks>
    /// ⚠️ <b>Renaming rather than copying, and oldest first.</b> Copy-and-
    /// truncate loses whatever is written between the two, and shuffling from
    /// the newest end would overwrite <c>.2</c> with <c>.1</c> before <c>.2</c>
    /// had been moved to <c>.3</c>.
    /// <para>
    /// A <c>keep</c> of zero means "rotate by deleting", which is the right
    /// behaviour for somebody who wants a bounded file and no history at all.
    /// </para>
    /// </remarks>
    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;

        try
        {
            var oldest = $"{_path}.{_keep}";

            if (_keep > 0 && File.Exists(oldest))
                File.Delete(oldest);

            for (var i = _keep - 1; i >= 1; i--)
            {
                var from = $"{_path}.{i}";

                if (File.Exists(from))
                    File.Move(from, $"{_path}.{i + 1}", overwrite: true);
            }

            if (_keep > 0)
                File.Move(_path, $"{_path}.1", overwrite: true);
            else
                File.Delete(_path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Rotation failed but writing might not have. Reopening appends to
            // whatever is there, which grows past the limit rather than losing
            // the log — the less bad of the two.
            //
            // ⚠️ Reported ONCE. See _rotationFailed: the file stays over its
            // limit after a failed rotation, so every later write tries again,
            // and an unlatched warning here is one line per line of log.
            if (!_rotationFailed)
            {
                _rotationFailed = true;
                _inner.Warn($"could not rotate {_path}, and it will grow past its limit: {e.Message}");
            }

            Open();
            return;
        }

        _rotationFailed = false;
        Open();
    }

    private void Open()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Append, never truncate: a restart must not delete the log of why
            // the previous process stopped, which is the log most worth having.
            var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read);

            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                // ⚠️ Flushed per write. A buffered log loses the last few
                // seconds on a crash, and the last few seconds before a crash
                // are the whole reason to read it.
                AutoFlush = true,
            };

            _written = stream.Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Break(e);
        }
    }

    private void Break(Exception e)
    {
        if (_broken)
            return;

        _broken = true;
        _writer?.Dispose();
        _writer = null;

        // Once, not per line: a broken disk must not become a second flood on
        // the sink that still works.
        _inner.Warn($"file logging to {_path} stopped: {e.GetType().Name}: {e.Message}");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
