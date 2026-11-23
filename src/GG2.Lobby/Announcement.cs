using System.Buffers;
using System.Text.Json;

namespace GG2.Lobby;

/// <summary>
/// One announce body, parsed and already re-serialised as the list entry it
/// becomes.
/// </summary>
/// <remarks>
/// <b>The lobby never echoes the bytes it was sent.</b> Every field is read,
/// bounded and written back out through <see cref="Utf8JsonWriter"/>, so an
/// announce cannot smuggle anything into the list that is not one of these
/// fields with a sane length. Unknown properties are skipped, which is the
/// forward-compatibility rule: a newer server can send more than this lobby
/// knows about and still be listed.
/// <para>
/// The entry is serialised ONCE, here, and the list response is those
/// fragments joined. A GET does no JSON work at all.
/// </para>
/// </remarks>
internal static class Announcement
{
    public const int MaxNameLength = 128;
    public const int MaxShortText = 64;
    public const int MaxUrlLength = 256;
    public const int MaxProtocols = 8;
    public const int MaxInfoEntries = 16;
    public const int MaxInfoKey = 32;
    public const int MaxInfoValue = 256;

    /// <summary>
    /// Parses <paramref name="body"/> and builds the list entry for a server at
    /// <paramref name="ip"/>.
    /// </summary>
    /// <param name="ip">The address the announce CAME FROM, never one it claims.</param>
    public static bool TryBuild(
        ReadOnlySpan<byte> body, string ip, out ushort port, out byte[] fragment, out string name, out string error)
    {
        port = 0;
        fragment = [];
        name = string.Empty;

        string? map = null, game = null, version = null, url = null;
        ushort slots = 0, players = 0, bots = 0;
        var passworded = false;
        List<string>? protocols = null;
        List<KeyValuePair<string, string>>? info = null;

        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 4 });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return Fail("body is not a JSON object", out error);

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("port"u8))
                {
                    if (!ReadUShort(ref reader, out port) || port == 0)
                        return Fail("port must be 1-65535", out error);
                }
                else if (reader.ValueTextEquals("name"u8))
                {
                    if (!ReadText(ref reader, MaxNameLength, out var n) || n.Length == 0)
                        return Fail($"name must be 1-{MaxNameLength} characters", out error);
                    name = n;
                }
                else if (reader.ValueTextEquals("map"u8))
                {
                    if (!ReadText(ref reader, MaxShortText, out map))
                        return Fail($"map is at most {MaxShortText} characters", out error);
                }
                else if (reader.ValueTextEquals("game"u8))
                {
                    if (!ReadText(ref reader, MaxShortText, out game))
                        return Fail($"game is at most {MaxShortText} characters", out error);
                }
                else if (reader.ValueTextEquals("version"u8))
                {
                    if (!ReadText(ref reader, MaxShortText, out version))
                        return Fail($"version is at most {MaxShortText} characters", out error);
                }
                else if (reader.ValueTextEquals("url"u8))
                {
                    if (!ReadText(ref reader, MaxUrlLength, out url))
                        return Fail($"url is at most {MaxUrlLength} characters", out error);
                }
                else if (reader.ValueTextEquals("slots"u8))
                {
                    if (!ReadUShort(ref reader, out slots))
                        return Fail("slots must be 0-65535", out error);
                }
                else if (reader.ValueTextEquals("players"u8))
                {
                    if (!ReadUShort(ref reader, out players))
                        return Fail("players must be 0-65535", out error);
                }
                else if (reader.ValueTextEquals("bots"u8))
                {
                    if (!ReadUShort(ref reader, out bots))
                        return Fail("bots must be 0-65535", out error);
                }
                else if (reader.ValueTextEquals("passworded"u8))
                {
                    reader.Read();
                    if (reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                        return Fail("passworded must be a boolean", out error);
                    passworded = reader.TokenType == JsonTokenType.True;
                }
                else if (reader.ValueTextEquals("protocols"u8))
                {
                    if (!ReadProtocols(ref reader, out protocols))
                        return Fail($"protocols is an array of at most {MaxProtocols} strings of at most {MaxShortText} characters", out error);
                }
                else if (reader.ValueTextEquals("info"u8))
                {
                    if (!ReadInfo(ref reader, out info))
                        return Fail($"info is an object of at most {MaxInfoEntries} string values", out error);
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                return Fail("body is not a JSON object", out error);
        }
        catch (JsonException)
        {
            return Fail("body is not valid JSON", out error);
        }

        if (port == 0)
            return Fail("port is required", out error);

        if (name.Length == 0)
            return Fail("name is required", out error);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("ip"u8, ip);
            writer.WriteNumber("port"u8, port);
            writer.WriteString("name"u8, name);
            if (map is not null) writer.WriteString("map"u8, map);
            writer.WriteNumber("slots"u8, slots);
            writer.WriteNumber("players"u8, players);
            writer.WriteNumber("bots"u8, bots);
            writer.WriteBoolean("passworded"u8, passworded);
            if (game is not null) writer.WriteString("game"u8, game);
            if (version is not null) writer.WriteString("version"u8, version);
            if (url is not null) writer.WriteString("url"u8, url);

            writer.WriteStartArray("protocols"u8);
            if (protocols is not null)
                foreach (var p in protocols)
                    writer.WriteStringValue(p);
            writer.WriteEndArray();

            if (info is { Count: > 0 })
            {
                writer.WriteStartObject("info"u8);
                foreach (var (key, value) in info)
                    writer.WriteString(key, value);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        fragment = buffer.WrittenSpan.ToArray();
        error = string.Empty;
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool ReadUShort(ref Utf8JsonReader reader, out ushort value)
    {
        value = 0;
        return reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetUInt16(out value);
    }

    private static bool ReadText(ref Utf8JsonReader reader, int max, out string value)
    {
        value = string.Empty;
        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            return false;

        // ValueSpan is the ESCAPED length, which is never shorter than the
        // decoded one, so this rejects an oversized string before allocating it.
        if (!reader.ValueIsEscaped && reader.ValueSpan.Length > max * 4)
            return false;

        value = reader.GetString()!;
        return value.Length <= max;
    }

    private static bool ReadProtocols(ref Utf8JsonReader reader, out List<string>? protocols)
    {
        protocols = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            return false;

        protocols = [];
        while (true)
        {
            if (!reader.Read())
                return false;
            if (reader.TokenType == JsonTokenType.EndArray)
                return true;
            if (reader.TokenType != JsonTokenType.String || protocols.Count == MaxProtocols)
                return false;

            var p = reader.GetString()!;
            if (p.Length > MaxShortText)
                return false;
            protocols.Add(p);
        }
    }

    private static bool ReadInfo(ref Utf8JsonReader reader, out List<KeyValuePair<string, string>>? info)
    {
        info = null;
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return false;

        info = [];
        while (true)
        {
            if (!reader.Read())
                return false;
            if (reader.TokenType == JsonTokenType.EndObject)
                return true;
            if (info.Count == MaxInfoEntries)
                return false;

            var key = reader.GetString()!;
            if (key.Length is 0 or > MaxInfoKey)
                return false;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                return false;

            var value = reader.GetString()!;
            if (value.Length > MaxInfoValue)
                return false;

            // A key the object has already set is dropped rather than written
            // twice: duplicate keys parse differently in different readers.
            if (!info.Exists(kv => kv.Key == key))
                info.Add(new(key, value));
        }
    }
}
