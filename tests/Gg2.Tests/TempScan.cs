using System.Text.Json;
using Gg2.Maps;

namespace Gg2.Tests;

public sealed class TempScan(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void DiffBubble()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "sprite-mask-samples.json")));
        var bytes = File.ReadAllBytes(Path.Combine(dir, "sprite-bubble1.gif"));

        foreach (var s in json.RootElement.GetProperty("samples").EnumerateArray())
        {
            if (s.GetProperty("file").GetString() != "sprite-bubble1.gif" || s.GetProperty("scale").GetInt32() != 1)
                continue;

            var rows = s.GetProperty("rows").EnumerateArray().Select(r => r.GetString()!).ToArray();

            foreach (var removeBack in new[] { true, false })
            {
                var mask = SpriteMask.Decode(bytes, removeBack)!;
                int diff = 0, portOnly = 0, engineOnly = 0;

                for (var y = 0; y < 40; y++)
                for (var x = 0; x < 40; x++)
                {
                    var engine = rows[y][x] == '#';
                    var port = mask.IsSolid(x, y);
                    if (engine == port) continue;
                    diff++;
                    if (port) portOnly++; else engineOnly++;
                    if (diff <= 12) output.WriteLine($"RESULT   removeback={removeBack} ({x},{y}) engine={engine} port={port}");
                }

                output.WriteLine($"RESULT removeback={removeBack}: solid={mask.SolidCount} engine=586 diff={diff} portOnly={portOnly} engineOnly={engineOnly}");
            }
        }
    }
}
