using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GG2.Protocol.Rooms;
using Xunit;

namespace GG2.Tests;

/// <summary>The signed room-host directory (docs/ROOMS.md, "The directory").</summary>
public sealed class RoomDirectoryTests
{
    private const string Source =
        """
        {
          "operator": "Test operator",
          "hosts": [
            {"prefix": "W", "region": "US West", "url": "https://w.example"},
            {"prefix": "E", "region": "US East", "url": "https://e.example/"}
          ]
        }
        """;

    private static readonly DateTime Issued = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ASignedDirectoryVerifiesWithItsPublicKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = RoomDirectory.ParseSource(Source, Issued).Sign(key);

        var directory = RoomDirectory.Verify(envelope, RoomDirectory.PublicKeyOf(key));

        Assert.Equal("Test operator", directory.Operator);
        Assert.Equal(Issued, directory.Issued);
        Assert.Equal(['W', 'E'], directory.Hosts.Select(h => h.Prefix));
        Assert.Equal("https://w.example/", directory.Hosts[0].Url.ToString());
        Assert.Equal("US East", directory.Hosts[1].Region);
    }

    [Fact]
    public void AnotherKeyIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = RoomDirectory.ParseSource(Source, Issued).Sign(key);

        var e = Assert.Throws<FormatException>(() => RoomDirectory.Verify(envelope, RoomDirectory.PublicKeyOf(other)));
        Assert.Contains("signature", e.Message);
    }

    [Fact]
    public void AnEditedPayloadIsRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = RoomDirectory.ParseSource(Source, Issued).Sign(key);
        var (payload, signature) = Open(envelope);

        var edited = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("https://w.example/", "https://evil.example/", StringComparison.Ordinal));
        Assert.NotEqual(payload, edited);

        Assert.Throws<FormatException>(() => RoomDirectory.Verify(Close(edited, signature), RoomDirectory.PublicKeyOf(key)));
    }

    /// <summary>The signature covers the payload's bytes, so the envelope can be re-indented by anything in between.</summary>
    [Fact]
    public void TheEnvelopesOwnFormattingDoesNotMatter()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = RoomDirectory.ParseSource(Source, Issued).Sign(key);
        var (payload, signature) = Open(envelope);

        var compact = Close(payload, signature).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);

        Assert.Equal(2, RoomDirectory.Verify(compact, RoomDirectory.PublicKeyOf(key)).Hosts.Count);
    }

    [Fact]
    public void AnotherVersionIsRefusedEvenWhenSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = RoomDirectory.ParseSource(Source, Issued).ToPayload();
        var future = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload).Replace("\"version\": 1", "\"version\": 2", StringComparison.Ordinal));
        Assert.NotEqual(payload, future);
        var signature = key.SignData(future, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var e = Assert.Throws<FormatException>(() => RoomDirectory.Verify(Close(future, signature), RoomDirectory.PublicKeyOf(key)));
        Assert.Contains("version", e.Message);
    }

    [Theory]
    [InlineData("""{"operator": "x", "hosts": [{"prefix": "W", "region": "a", "url": "https://a.example"}, {"prefix": "W", "region": "b", "url": "https://b.example"}]}""", "two hosts")]
    [InlineData("""{"operator": "x", "hosts": [{"prefix": "I", "region": "a", "url": "https://a.example"}]}""", "not one of")]
    [InlineData("""{"operator": "x", "hosts": [{"prefix": "W", "region": "a", "url": "http://a.example"}]}""", "https")]
    [InlineData("""{"operator": "x", "hosts": [{"prefix": "W", "region": "", "url": "https://a.example"}]}""", "region")]
    [InlineData("""{"operator": "x", "hosts": []}""", "no hosts")]
    [InlineData("""{"operator": "", "hosts": [{"prefix": "W", "region": "a", "url": "https://a.example"}]}""", "operator")]
    public void ABadSourceIsNotSigned(string source, string reason)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var e = Assert.Throws<FormatException>(() => RoomDirectory.ParseSource(source, Issued).Sign(key));
        Assert.Contains(reason, e.Message);
    }

    private static (byte[] Payload, byte[] Signature) Open(string envelope)
    {
        using var json = JsonDocument.Parse(envelope);
        return (Convert.FromBase64String(json.RootElement.GetProperty("payload").GetString()!),
            Convert.FromBase64String(json.RootElement.GetProperty("signature").GetString()!));
    }

    private static string Close(byte[] payload, byte[] signature) =>
        $$"""
        {
          "payload": "{{Convert.ToBase64String(payload)}}",
          "signature": "{{Convert.ToBase64String(signature)}}"
        }
        """;
}

/// <summary>Reading a whole code a player typed to join a room.</summary>
public sealed class RoomCodeParseTests
{
    [Theory]
    [InlineData("WABCDE", "WABCDE")]
    [InlineData("  w4k 9pq ", "W4K9PQ")]
    [InlineData("w4k-9pq", "W4K9PQ")]
    [InlineData("WABCD", null)]
    [InlineData("WABCDEF", null)]
    [InlineData("WABCD0", null)]
    [InlineData("WIBCDE", null)]
    [InlineData("", null)]
    public void ParsesAWholeCode(string typed, string? expected) => Assert.Equal(expected, RoomCodes.ParseWhole(typed));
}
