using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace GG2.Auth;

internal static class Program
{
    private const string Usage = """
        gg2-auth: the Haxxy reward auth server for gg2-server. docs/AUTH.md.

          gg2-auth [--db <path>] <command>

        The user table is --db, else $GG2_AUTH_DB, else gg2-auth.tsv in the
        current directory. It holds every player's secret key.

          serve [options]           answer game servers (the default command)
            --port <n>                listen port (default 45022)
            --bind <address>          listen address (default [::], dual-stack)
            --public-address <ip>     also accept this address for a game server
                                      that connects from loopback or a private
                                      network; repeatable
            --require-ip              refuse ipCheck = 0 (host self-auth)
            --quiet                   do not log each request
          add [--note <text>] [reward ...]
                                    issue a new id and key, printed as the
                                    player's [Haxxy] section
          list                      every user, without keys
          show <id>                 one user, with the key
          grant <id> <reward> ...   add rewards
          revoke <id> <reward> ...  remove rewards
          rekey <id>                issue a new key; the old one stops working
          remove <id>               delete the user

        A running server picks up every change on its next request.
        """;

    public static int Main(string[] args)
    {
        try
        {
            var rest = new List<string>();
            string? db = null;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--db")
                    db = i + 1 < args.Length ? args[++i] : throw new FormatException("--db needs a value");
                else if (args[i] is "-h" or "--help")
                {
                    Console.WriteLine(Usage);
                    return 0;
                }
                else
                    rest.Add(args[i]);
            }

            db ??= Environment.GetEnvironmentVariable("GG2_AUTH_DB") is { Length: > 0 } env ? env : "gg2-auth.tsv";
            db = Path.GetFullPath(db);

            // No command, or options first, means serve.
            var named = rest.Count > 0 && !rest[0].StartsWith("--", StringComparison.Ordinal);
            var command = named ? rest[0] : "serve";
            var operands = named ? rest[1..] : rest;

            return command switch
            {
                "serve" => Serve(db, operands),
                "add" => Add(db, operands),
                "list" => List(db, operands),
                "show" => Show(db, operands),
                "grant" => Change(db, operands, grant: true),
                "revoke" => Change(db, operands, grant: false),
                "rekey" => Rekey(db, operands),
                "remove" => Remove(db, operands),
                _ => throw new FormatException($"unknown command {command}"),
            };
        }
        catch (FormatException e)
        {
            Console.Error.WriteLine(e.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Usage);
            return 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
    }

    private static int Serve(string db, List<string> args)
    {
        var options = new AuthOptions();
        var publicAddresses = new List<string>();
        var quiet = false;

        for (var i = 0; i < args.Count; i++)
        {
            string Next() => i + 1 < args.Count ? args[++i] : throw new FormatException($"{args[i]} needs a value");

            switch (args[i])
            {
                case "--port": options = options with { Port = int.Parse(Next(), CultureInfo.InvariantCulture) }; break;
                case "--bind": options = options with { Bind = IPAddress.Parse(Next()) }; break;
                case "--public-address":
                    // Normalised, because the comparison is against the text the
                    // client appended, and GM8 writes dotted IPv4 with no padding.
                    var address = IPAddress.Parse(Next());
                    if (address.AddressFamily != AddressFamily.InterNetwork)
                        throw new FormatException("--public-address takes an IPv4 address: the stock client has no IPv6");
                    publicAddresses.Add(address.ToString());
                    break;
                case "--require-ip": options = options with { AllowWithoutIp = false }; break;
                case "--quiet": quiet = true; break;
                default: throw new FormatException($"unknown argument {args[i]}");
            }
        }

        options = options with { PublicAddresses = publicAddresses };

        Action<string> log = static line => Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {line}");
        var users = new UserStore(db, log);
        if (!File.Exists(db))
            log($"{db} does not exist yet; every request fails until `gg2-auth add` creates it");

        using var server = new AuthServer(options, users, quiet ? static _ => { } : log);
        Console.WriteLine(
            $"gg2-auth listening on {options.Bind}:{server.Port}, {users.Count} user(s)"
            + (publicAddresses.Count > 0 ? $", local peers may be {string.Join(", ", publicAddresses)}" : string.Empty)
            + (options.AllowWithoutIp ? string.Empty : ", ipCheck required"));

        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Set();
        });

        stop.Wait();
        return 0;
    }

    private static int Add(string db, List<string> args)
    {
        var note = string.Empty;
        var rewards = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--note")
                note = i + 1 < args.Count ? UserFile.CleanNote(args[++i]) : throw new FormatException("--note needs a value");
            else
                rewards.Add(CheckReward(args[i]));
        }

        var users = UserFile.Load(db);
        var user = new User(UserFile.NextId(users), UserFile.NewKey(), string.Join(':', rewards.Distinct(StringComparer.Ordinal)), note);
        users.Add(user);
        UserFile.Save(db, users);

        Console.Error.WriteLine($"added user {user.Id} to {db}. Give the player this, for their gg2.ini:");
        PrintIni(user);
        return 0;
    }

    private static int List(string db, List<string> args)
    {
        if (args.Count != 0)
            throw new FormatException("list takes no arguments");

        var users = UserFile.Load(db);
        Console.Error.WriteLine($"{users.Count} user(s) in {db}");
        foreach (var user in users)
            Console.WriteLine($"{user.Id}\t{(user.Rewards.Length > 0 ? user.Rewards : "-")}\t{user.Note}");
        return 0;
    }

    private static int Show(string db, List<string> args)
    {
        var (_, user) = Find(db, args, exactly: 1);
        if (user.Note.Length > 0)
            Console.Error.WriteLine($"note: {user.Note}");
        Console.Error.WriteLine($"rewards: {(user.Rewards.Length > 0 ? user.Rewards : "(none)")}");
        PrintIni(user);
        return 0;
    }

    private static int Change(string db, List<string> args, bool grant)
    {
        if (args.Count < 2)
            throw new FormatException($"{(grant ? "grant" : "revoke")} takes an id and at least one reward");

        var (users, user) = Find(db, args[..1], exactly: 1);
        var names = user.Rewards.Length > 0 ? user.Rewards.Split(':').ToList() : [];
        foreach (var reward in args.Skip(1).Select(CheckReward))
        {
            if (grant && !names.Contains(reward, StringComparer.Ordinal))
                names.Add(reward);
            else if (!grant && !names.Remove(reward))
                Console.Error.WriteLine($"user {user.Id} did not have {reward}");
        }

        var updated = user with { Rewards = string.Join(':', names) };
        users[users.IndexOf(user)] = updated;
        UserFile.Save(db, users);
        Console.WriteLine($"{updated.Id}\t{(updated.Rewards.Length > 0 ? updated.Rewards : "-")}");
        return 0;
    }

    private static int Rekey(string db, List<string> args)
    {
        var (users, user) = Find(db, args, exactly: 1);
        var updated = user with { Key = UserFile.NewKey() };
        users[users.IndexOf(user)] = updated;
        UserFile.Save(db, users);

        Console.Error.WriteLine($"user {user.Id} has a new key; the old one no longer works:");
        PrintIni(updated);
        return 0;
    }

    private static int Remove(string db, List<string> args)
    {
        var (users, user) = Find(db, args, exactly: 1);
        users.Remove(user);
        UserFile.Save(db, users);
        Console.Error.WriteLine($"removed user {user.Id}");
        return 0;
    }

    private static (List<User> Users, User User) Find(string db, List<string> args, int exactly)
    {
        if (args.Count != exactly)
            throw new FormatException("expected a user id");

        if (!File.Exists(db))
            throw new IOException($"{db} does not exist; `gg2-auth add` creates it");

        var users = UserFile.Load(db);
        var user = users.Find(u => u.Id == args[0]) ?? throw new IOException($"no user {args[0]} in {db}");
        return (users, user);
    }

    private static string CheckReward(string name) =>
        UserFile.ValidateReward(name) is { } error ? throw new FormatException(error) : name;

    /// <summary>
    /// The player's half, in the form <c>game_init.gml:92-93</c> reads it.
    /// Stdout only, so it can be redirected away from the messages around it.
    /// </summary>
    private static void PrintIni(User user)
    {
        Console.WriteLine("[Haxxy]");
        Console.WriteLine($"RewardId={user.Id}");
        Console.WriteLine($"RewardKey={user.KeyHex}");
    }
}
