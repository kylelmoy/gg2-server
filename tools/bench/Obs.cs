using System.Runtime.InteropServices;
using Gg2.Bots;
using Gg2.Protocol;
using Gg2.Simulation;

namespace Bench;

/// <summary>
/// Dumps real observations so the numpy policy and the C# one can be compared on
/// the same inputs.
/// </summary>
/// <remarks>
/// <b>Real states, not random floats.</b> A forward pass agrees on noise
/// whatever its head offsets are; what has to agree is the ranking over the
/// actions in states the bot will actually meet, which is where a wrong offset
/// shows up as a plausible answer to the wrong question.
/// </remarks>
public static class ObsDump
{
    public static void Run(string maps, string? nav, string map, string outPath, int count)
    {
        var world = ScenarioWorld.Load(map, 1, maps, nav);
        var red = world.Place(PlayerClass.Soldier, Team.Red, 600, 850);
        var blue = world.Place(PlayerClass.Scout, Team.Blue, 760, 850);
        var random = new Random(20260901);
        var buffer = new float[ObservationWriter.Length];
        var all = new List<float>();

        // Spread the samples over states the bot would meet: settle, then move
        // both characters around and re-read.
        for (var i = 0; i < 30; i++)
            world.Step();

        for (var sample = 0; sample < count; sample++)
        {
            world.State.Characters[red].X = 200 + (random.NextDouble() * 1000);
            world.State.Characters[red].Y = 700 + (random.NextDouble() * 200);
            world.State.Characters[blue].X = 200 + (random.NextDouble() * 1000);
            world.State.Characters[blue].Y = 700 + (random.NextDouble() * 200);
            world.State.Characters[red].NetAimDirection = (ushort)random.Next(65536);
            world.State.Characters[blue].NetAimDirection = (ushort)random.Next(65536);
            world.State.Characters[red].Hp = 1 + (random.NextDouble() * 199);

            world.Step();

            var view = new WorldView(in world.State, world.Collision);

            ObservationWriter.Write(in view, sample % 2 == 0 ? red : blue, buffer);
            all.AddRange(buffer);
        }

        using var file = File.Create(outPath);

        Span<byte> header = stackalloc byte[8];
        BitConverter.TryWriteBytes(header, count);
        BitConverter.TryWriteBytes(header[4..], ObservationWriter.Length);
        file.Write(header);
        file.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(all)));

        Console.WriteLine($"wrote {count} x {ObservationWriter.Length} observations to {outPath}");
    }
}
