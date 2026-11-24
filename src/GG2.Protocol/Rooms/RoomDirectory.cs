using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GG2.Protocol.Rooms;

/// <summary>One room host in the directory: the code prefix it owns, a region to show, and its API's base URL.</summary>
public sealed record RoomDirectoryHost(char Prefix, string Region, Uri Url);

/// <summary>
/// The signed list of room hosts a client trusts (<c>docs/ROOMS.md</c>, "The
/// directory"). Liveness is never in it: a client reads each host's
/// <c>/status</c> itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>On the wire</b> it is an envelope, <c>{"payload": base64, "signature":
/// base64}</c>, and the signature is over the payload's bytes exactly as they
/// are. Nothing is re-serialised before checking, so the signer and a client
/// never have to agree on how JSON is written.
/// </para>
/// <para>
/// <b>ECDSA P-256 with SHA-256</b>, the 64-byte IEEE P1363 form (r then s).
/// Every .NET platform the native client runs on, iOS included, has it in the
/// base library; Ed25519 has no standalone .NET API. The public key is the
/// base64 of its SubjectPublicKeyInfo.
/// </para>
/// <para>
/// <b>A client keeps the newest copy it has verified</b>, by <see
/// cref="Issued"/>, so a stale signed copy served later cannot roll a client
/// back to a host list the operator has since replaced.
/// </para>
/// </remarks>
public sealed record RoomDirectory(string Operator, DateTime Issued, IReadOnlyList<RoomDirectoryHost> Hosts)
{
    /// <summary>The payload's <c>version</c>. A client refuses a directory with any other.</summary>
    public const int Version = 1;

    // "\n" on every OS and no escaping of an apostrophe, so the same source
    // signs to the same bytes wherever the tool runs and the payload reads as
    // written. The payload is never put in HTML, which is what the default
    // escaping guards.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Checks the hosts: at least one; each prefix a room-code letter
    /// (<see cref="RoomCodes.Alphabet"/>) owned by one host; each URL absolute
    /// https with no query; each region named. Throws <see cref="FormatException"/>.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Operator))
            throw new FormatException("directory: operator is empty");
        if (Hosts.Count == 0)
            throw new FormatException("directory: no hosts");

        var seen = new HashSet<char>();
        foreach (var host in Hosts)
        {
            if (RoomCodes.Alphabet.IndexOf(host.Prefix) < 0)
                throw new FormatException($"directory: prefix '{host.Prefix}' is not one of {RoomCodes.Alphabet}");
            if (!seen.Add(host.Prefix))
                throw new FormatException($"directory: prefix '{host.Prefix}' names two hosts");
            if (string.IsNullOrWhiteSpace(host.Region))
                throw new FormatException($"directory: host {host.Prefix} has no region");
            if (!host.Url.IsAbsoluteUri || host.Url.Scheme != Uri.UriSchemeHttps || host.Url.Query.Length > 0 || host.Url.Fragment.Length > 0)
                throw new FormatException($"directory: host {host.Prefix}: '{host.Url}' is not an https base URL");
        }
    }

    /// <summary>The payload's bytes: what is signed.</summary>
    public byte[] ToPayload()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("version", Version);
            json.WriteString("operator", Operator);
            json.WriteString("issued", Issued.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            json.WriteStartArray("hosts");
            foreach (var host in Hosts)
            {
                json.WriteStartObject();
                json.WriteString("prefix", host.Prefix.ToString());
                json.WriteString("region", host.Region);
                json.WriteString("url", host.Url.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>Validates, signs with <paramref name="key"/> and returns the envelope's text.</summary>
    public string Sign(ECDsa key)
    {
        Validate();
        var payload = ToPayload();
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("payload", Convert.ToBase64String(payload));
            json.WriteString("signature", Convert.ToBase64String(signature));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }

    /// <summary>
    /// The directory in <paramref name="envelope"/> if its signature checks
    /// against <paramref name="publicKey"/> (base64 SubjectPublicKeyInfo) and
    /// its payload is a valid version-1 directory. Throws <see
    /// cref="FormatException"/> saying why not.
    /// </summary>
    public static RoomDirectory Verify(string envelope, string publicKey)
    {
        byte[] payload, signature;
        try
        {
            using var outer = JsonDocument.Parse(envelope);
            var root = outer.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("signature", out var s) || s.ValueKind != JsonValueKind.String)
                throw new FormatException("directory: not a {payload, signature} envelope");
            payload = Convert.FromBase64String(p.GetString()!);
            signature = Convert.FromBase64String(s.GetString()!);
        }
        catch (JsonException e)
        {
            throw new FormatException($"directory: not JSON: {e.Message}", e);
        }

        using (var key = ECDsa.Create())
        {
            try
            {
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            }
            catch (CryptographicException e)
            {
                throw new FormatException("directory: the public key is not a P-256 SubjectPublicKeyInfo", e);
            }

            if (key.KeySize != 256 || !key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new FormatException("directory: the signature does not match");
        }

        var directory = ParsePayload(payload);
        directory.Validate();
        return directory;
    }

    /// <summary>The base64 SubjectPublicKeyInfo of <paramref name="key"/>: what a client ships.</summary>
    public static string PublicKeyOf(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Reads the unsigned source a directory is signed from: <c>{"operator",
    /// "hosts": [{"prefix", "region", "url"}]}</c>. <c>issued</c> is not in it;
    /// signing stamps <paramref name="issued"/>.
    /// </summary>
    public static RoomDirectory ParseSource(string text, DateTime issued)
    {
        var parsed = Parse(Encoding.UTF8.GetBytes(text), requireEnvelopeFields: false);
        return parsed with { Issued = issued };
    }

    private static RoomDirectory ParsePayload(byte[] payload) => Parse(payload, requireEnvelopeFields: true);

    private static RoomDirectory Parse(byte[] bytes, bool requireEnvelopeFields)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new FormatException("directory: not an object");

            var issued = DateTime.MinValue;
            if (requireEnvelopeFields)
            {
                if (!root.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version)
                    throw new FormatException($"directory: version is not {Version}");
                if (Text(root, "issued") is not { } stamp
                    || !DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out issued))
                    throw new FormatException("directory: issued is missing or not a time");
            }

            if (!root.TryGetProperty("hosts", out var list) || list.ValueKind != JsonValueKind.Array)
                throw new FormatException("directory: hosts is not a list");

            var hosts = new List<RoomDirectoryHost>();
            foreach (var entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || Text(entry, "prefix") is not { Length: 1 } prefix
                    || Text(entry, "url") is not { } url
                    || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    throw new FormatException($"directory: a host needs a one-letter prefix and an absolute url: {entry.GetRawText()}");
                hosts.Add(new RoomDirectoryHost(prefix[0], Text(entry, "region") ?? "", uri));
            }

            return new RoomDirectory(Text(root, "operator") ?? "", issued, hosts);
        }
        catch (JsonException e)
        {
            throw new FormatException($"directory: not JSON: {e.Message}", e);
        }
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
