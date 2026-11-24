namespace GG2.Server;

/// <summary>
/// Applies a configuration pushed by <c>scripts/deploy.sh --config</c> the first
/// time nobody is connected.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the server does this and the deploy script does not.</b> Every
/// subsystem reads its options once at start, so a pushed file is inert until a
/// restart, and a restart drops everyone connected. Waiting until the server is
/// empty needs to know who is connected, which only the server knows — the
/// control port and the panel are loopback-bound and neither is on by default.
/// So the push writes the file and the marker, and the server restarts itself
/// (the panel's in-process restart, which re-reads the command line and the
/// file) at the first moment it would cost nobody anything.
/// </para>
/// <para>
/// ⛔ <b>Only a PUSHED file triggers it</b>: the file's hash has to equal the
/// marker <c>deploy.sh</c> wrote beside it. A save from the admin panel, or a
/// hand edit, changes the file and not the marker, so it is never applied behind
/// the operator's back — the panel's own "Save &amp; restart" is still what
/// applies those. A server started without a marker never restarts on its own.
/// </para>
/// <para>
/// ⚠️ <b>A file that would not start is not applied.</b> The push validated it
/// already, but a restart that fails here is fatal to the process (see
/// <c>Program.Main</c>'s restart loop), so it is re-checked and refused with one
/// warning per pushed version.
/// </para>
/// <para>
/// ⚠️ <b>File I/O on the tick thread</b>: one read of a 64-byte marker every
/// <see cref="CheckInterval"/>, and the config file itself only when the marker
/// has changed. That is the same bargain <c>config-set</c> makes, and it must not
/// become per tick.
/// </para>
/// </remarks>
internal sealed class PushedConfigWatcher
{
    /// <summary>How often the marker is looked at.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

    private readonly string _path;
    private readonly IServerLog _log;
    private long _nextCheck;
    private string _announced = string.Empty;
    private bool _usable;

    /// <summary>
    /// Captures the hash of the file as this server started from it, and records
    /// it beside a managed file as <c>gg2.json.applied</c>.
    /// </summary>
    public PushedConfigWatcher(string path, IServerLog log, long nowMilliseconds)
    {
        _path = path;
        _log = log;
        _nextCheck = nowMilliseconds + (long)CheckInterval.TotalMilliseconds;

        StartedFrom = TryHashFile(path);

        if (StartedFrom.Length != 0 && File.Exists(path + FleetConfig.PushedSuffix))
        {
            try
            {
                File.WriteAllText(path + FleetConfig.AppliedSuffix, StartedFrom + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Only deploy.sh --status reads it. A server that cannot write it
                // is still a working server, and says so once.
                _log.Warn($"could not record the applied config hash beside {path}: {e.Message}");
            }
        }
    }

    /// <summary>The SHA-256 of the config file when this server started, or empty.</summary>
    public string StartedFrom { get; }

    /// <summary>What the marker and the file say right now — for the <c>config</c> verb.</summary>
    public (bool Managed, bool EditedSincePush, bool WaitingToApply) Describe()
    {
        var marker = TryReadMarker();

        if (marker.Length == 0)
            return (false, false, false);

        var current = TryHashFile(_path);
        var edited = current != marker;
        return (true, edited, !edited && current != StartedFrom);
    }

    /// <summary>
    /// Whether the server should restart now to apply a pushed configuration.
    /// </summary>
    /// <param name="nowMilliseconds"><see cref="Environment.TickCount64"/>.</param>
    /// <param name="anybodyConnected">
    /// A player, a spectator, or a connection still joining. Anybody at all
    /// defers it: a spectator loses as much from a restart as a player does.
    /// </param>
    public bool ShouldRestart(long nowMilliseconds, bool anybodyConnected)
    {
        if (nowMilliseconds < _nextCheck)
            return false;

        _nextCheck = nowMilliseconds + (long)CheckInterval.TotalMilliseconds;

        var marker = TryReadMarker();

        if (marker.Length == 0 || marker == StartedFrom)
            return false;

        // Edited on the host after the push. Not ours to apply.
        var current = TryHashFile(_path);

        if (current != marker)
            return false;

        // Validated once per pushed version, not every interval while players
        // keep the server busy: a config load resolves directories off disk.
        if (_announced != marker)
        {
            _announced = marker;
            _usable = true;

            try
            {
                var candidate = ServerOptions.CreateDefault();
                ServerConfigFile.Apply(candidate, _path);
                candidate.Validate();
            }
            catch (Exception e) when (e is IOException or FormatException or ArgumentException or InvalidOperationException)
            {
                _usable = false;
                _log.Warn($"a pushed configuration was NOT applied, because this server could not start on it: {e.Message}");
            }

            if (_usable && anybodyConnected)
                _log.Info("a pushed configuration is waiting: it is applied the next time nobody is connected");
        }

        if (!_usable || anybodyConnected)
            return false;

        _log.Info("nobody is connected: restarting to apply the pushed configuration");
        return true;
    }

    private string TryReadMarker()
    {
        try
        {
            var path = _path + FleetConfig.PushedSuffix;
            return File.Exists(path) ? File.ReadAllText(path).Trim() : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string TryHashFile(string path)
    {
        try
        {
            return File.Exists(path) ? FleetConfig.Hash(File.ReadAllBytes(path)) : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
