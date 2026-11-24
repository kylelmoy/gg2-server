using System.Security.Cryptography;
using GG2.Protocol.Rooms;

namespace GG2.Host;

/// <summary>
/// <c>gg2-server directory ...</c>: makes the signing key, signs the room-host
/// directory and checks a signed copy (<c>docs/ROOMS.md</c>, "The directory").
/// </summary>
/// <remarks>
/// Runs on the operator's machine, never on a room host: the private key is
/// the authority over which hosts every client trusts, so it does not belong
/// on a box that serves the internet. A subcommand for the reason
/// <c>config</c> is one: it serves no game.
/// </remarks>
internal static class DirectoryTool
{
    public static void PrintUsage()
    {
        Console.WriteLine(
            """
            usage:
              gg2-server directory keygen --key <file>
              gg2-server directory sign   --key <file> --source <hosts.json> --out <rooms.json>
              gg2-server directory verify --public-key <base64> <rooms.json | https://...>

              keygen   write a new P-256 private key (PKCS#8 PEM) and print its
                       public key, the base64 a client ships. Refuses to overwrite.
              sign     sign the source, {"operator", "hosts": [{"prefix", "region",
                       "url"}]}, stamped with the current time, and check the result
                       against the key's public half before writing it.
              verify   check a signed directory, from a file or a URL, and print it.
            """);
    }

    public static int Run(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("directory: say keygen, sign or verify");

        string? key = null, source = null, output = null, publicKey = null, target = null;
        for (var i = 1; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--key": key = Value(); break;
                case "--source": source = Value(); break;
                case "--out": output = Value(); break;
                case "--public-key": publicKey = Value(); break;
                default:
                    if (args[i].StartsWith('-') || target is not null)
                        throw new ArgumentException($"directory: unexpected '{args[i]}'");
                    target = args[i];
                    break;
            }
        }

        switch (args[0])
        {
            case "keygen":
                return Keygen(key ?? throw new ArgumentException("keygen: --key is required"));
            case "sign":
                return Sign(key ?? throw new ArgumentException("sign: --key is required"),
                    source ?? throw new ArgumentException("sign: --source is required"),
                    output ?? throw new ArgumentException("sign: --out is required"));
            case "verify":
                return Verify(publicKey ?? throw new ArgumentException("verify: --public-key is required"),
                    target ?? throw new ArgumentException("verify: name a file or URL"));
            default:
                throw new ArgumentException($"directory: unknown verb '{args[0]}'");
        }
    }

    private static int Keygen(string path)
    {
        if (File.Exists(path))
        {
            Console.Error.WriteLine($"{path} exists; a new key would orphan every client that ships the old one");
            return 1;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null)
            Directory.CreateDirectory(directory);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file))
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        Console.WriteLine($"wrote {path}; keep a copy somewhere safe, it cannot be recovered");
        Console.WriteLine($"public key: {RoomDirectory.PublicKeyOf(key)}");
        return 0;
    }

    private static int Sign(string keyPath, string sourcePath, string outputPath)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        var publicKey = RoomDirectory.PublicKeyOf(key);

        var issued = DateTime.UtcNow;
        issued = new DateTime(issued.Ticks - issued.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var envelope = RoomDirectory.ParseSource(File.ReadAllText(sourcePath), issued).Sign(key);

        // Check what is about to be published the way a client will, so a
        // broken file never leaves this machine.
        var check = RoomDirectory.Verify(envelope, publicKey);
        File.WriteAllText(outputPath, envelope);
        Console.WriteLine($"wrote {outputPath}: {check.Hosts.Count} hosts, issued {check.Issued:yyyy-MM-ddTHH:mm:ssZ}");
        Console.WriteLine($"public key: {publicKey}");
        return 0;
    }

    private static int Verify(string publicKey, string target)
    {
        string envelope;
        if (target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            envelope = http.GetStringAsync(new Uri(target)).GetAwaiter().GetResult();
        }
        else
        {
            envelope = File.ReadAllText(target);
        }

        RoomDirectory directory;
        try
        {
            directory = RoomDirectory.Verify(envelope, publicKey);
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine($"REFUSED: {e.Message}");
            return 1;
        }

        Console.WriteLine($"verified: {directory.Operator}, issued {directory.Issued:yyyy-MM-ddTHH:mm:ssZ}");
        foreach (var host in directory.Hosts)
            Console.WriteLine($"  {host.Prefix}  {host.Region,-12} {host.Url}");
        return 0;
    }
}
