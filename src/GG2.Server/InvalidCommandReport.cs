using System.Text;
using GG2.Protocol;

namespace GG2.Server;

/// <summary>
/// Turns a <see cref="CommandStream"/>'s record of unrecognised bytes into one
/// log line.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>This exists because a count on its own cannot be acted on.</b>
/// <c>ConnectionLimits.MaxInvalidCommandBytes</c> drops a connection that skids
/// past a threshold, and the operator is then holding a number with no way to
/// tell a hostile sender from a client this port frames wrongly. The two need
/// opposite responses — one is the guard working, the other is the guard
/// causing the outage — and the runs are what separate them:
/// </para>
/// <list type="bullet">
/// <item>One long run and nothing after it: alignment was lost and never came
/// back. The bytes are a real message this framer mis-sized, and the command it
/// followed names the suspect.</item>
/// <item>Many short runs of the same length after the same command: the sender
/// is emitting something this table does not have.</item>
/// <item>Many runs of varied length after varied commands: the stream is
/// garbage, which is what the guard is for.</item>
/// </list>
/// <para>
/// The text carries a player-controlled byte count and nothing else a peer
/// chooses, so it is safe in a log line as it stands — but it is still
/// client-driven, so every caller puts it behind a <see cref="LogThrottle"/>.
/// </para>
/// </remarks>
internal static class InvalidCommandReport
{
    /// <summary>
    /// One line describing what <paramref name="stream"/> has skipped. Empty
    /// when it has skipped nothing.
    /// </summary>
    public static string Describe(CommandStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (stream.InvalidBytesSkipped == 0)
            return string.Empty;

        var text = new StringBuilder();
        text.Append(stream.InvalidBytesSkipped)
            .Append(" unrecognised command bytes in ")
            .Append(stream.InvalidRunsSeen)
            .Append(stream.InvalidRunsSeen == 1 ? " run, after " : " runs, after ")
            .Append(stream.CommandsFramed)
            .Append(" framed commands");

        var runs = stream.InvalidRuns;
        if (runs.Length == 0)
            return text.ToString();

        text.Append("; first ").Append(runs.Length).Append(runs.Length == 1 ? " run: " : " runs: ");

        for (var i = 0; i < runs.Length; i++)
        {
            if (i > 0)
                text.Append(" | ");

            ref readonly var run = ref runs[i];

            text.Append('@').Append(run.Offset).Append(" after ");

            if (run.AfterCommand < 0)
                text.Append("nothing");
            else
                text.Append(Name((byte)run.AfterCommand))
                    .Append('+')
                    .Append(run.AfterPayloadLength)
                    .Append(" (#")
                    .Append(run.AfterCommandCount)
                    .Append(')');

            text.Append(": ").Append(run.Length).Append(run.Length == 1 ? " byte " : " bytes ");

            for (var b = 0; b < run.SampleCount; b++)
            {
                if (b > 0)
                    text.Append(' ');
                text.Append(run.Sample[b].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }

            if (run.Length > run.SampleCount)
                text.Append(" ...");
        }

        return text.ToString();
    }

    /// <summary>
    /// The command's name, or its value when the enum does not name it — an
    /// unnamed one is the interesting case, so it must not read as a blank.
    /// </summary>
    private static string Name(byte command) =>
        Enum.IsDefined(typeof(MessageType), command)
            ? ((MessageType)command).ToString()
            : "0x" + command.ToString("x2", System.Globalization.CultureInfo.InvariantCulture);
}
