using System.Globalization;

namespace GG2.Parity;

internal static partial class Program
{
    private static int Diff(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("diff needs two files");
            return 2;
        }

        var left = File.ReadAllBytes(args[0]);
        var right = File.ReadAllBytes(args[1]);

        Console.WriteLine($"{args[0]}: {left.Length} bytes");
        Console.WriteLine($"{args[1]}: {right.Length} bytes");

        var shared = Math.Min(left.Length, right.Length);
        var at = 0;
        while (at < shared && left[at] == right[at])
            at++;

        if (at == shared && left.Length == right.Length)
        {
            Console.WriteLine("identical");
            return 0;
        }

        Console.WriteLine($"first difference at byte {at}");
        Console.WriteLine();
        Console.WriteLine(Window(left, at, args[0]));
        Console.WriteLine(Window(right, at, args[1]));
        return 1;
    }

    /// <summary>32 bytes either side of the divergence, with it marked.</summary>
    private static string Window(byte[] bytes, int at, string label)
    {
        var from = Math.Max(0, at - 32);
        var to = Math.Min(bytes.Length, at + 32);

        var hex = new System.Text.StringBuilder();
        hex.Append(label).Append('\n').Append("  ");

        for (var i = from; i < to; i++)
        {
            hex.Append(i == at ? '[' : ' ');
            hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            if (i == at)
                hex.Append(']');
        }

        return hex.ToString();
    }
}
