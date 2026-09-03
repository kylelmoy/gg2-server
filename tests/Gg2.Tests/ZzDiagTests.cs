using Gg2.Maps;
using Gg2.Protocol;
using Xunit.Abstractions;

namespace Gg2.Tests;

public sealed class ZzDiagTests(ITestOutputHelper output)
{
    [Fact]
    public void AreaShapes()
    {
        foreach (var name in new[] { "cp_dirtbowl", "cp_egypt", "ctf_truefort" })
        {
            var whole = LevelMap.FromPng(name, ReferenceTree.ReadMap(name));
            var areas = GameModes.TotalAreas(whole);
            output.WriteLine($"{name}: TotalAreas={areas}");

            for (var a = 1; a <= areas; a++)
            {
                var m = whole.ForArea(a);
                var shape = GameModes.ShapeOf(m);
                output.WriteLine(
                    $"  area {a}: mode={shape.GameMode} cps={shape.ControlPointCount} gens={shape.GeneratorCount} "
                    + $"size={m.Walkmask.Width}x{m.Walkmask.Height} entities={m.Entities.Count}");
            }
        }
    }
}
