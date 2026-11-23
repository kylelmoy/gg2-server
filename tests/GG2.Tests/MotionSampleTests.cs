using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using GG2.Protocol;
using GG2.Simulation;

namespace GG2.Tests;

/// <summary>
/// <see cref="Motion"/> against GM8's own four motion variables.
/// </summary>
/// <remarks>
/// <see cref="MotionTests"/> pins the model as it was transcribed from OpenGMK.
/// This pins it against the <em>engine</em>, which is the check phase 5 could
/// not do without: nothing in phase 4 writes <c>direction</c> or <c>speed</c>,
/// so the whole polar half of the model reached this repo unmeasured, and a
/// projectile writes it three times before it has moved a pixel
/// (<c>Objects/Weapons/Scattergun.events/User Event 3.xml</c>).
/// <para>
/// <b>This is the first fixture in the project that is not bit-exact, and the
/// reason is the engine rather than the port.</b> GM8 is a Delphi program and
/// its motion conversions run on the x87 stack at 80-bit extended precision,
/// with Intel's <c>FSIN</c>/<c>FCOS</c> and their 66-bit argument reduction. No
/// IEEE-double formula reproduces that — eight candidate spellings of
/// degrees-to-radians were measured against 486 components and the best of them,
/// the one <see cref="Motion"/> already used, was wrong on 23% of them. So the
/// conversions are asserted to a <b>measured bound</b> instead, and everything
/// that does <em>not</em> go through the trig is still asserted exactly. See
/// docs/SIMULATION.md, "Settled: the four motion variables".
/// </para>
/// <para>
/// Every sample is a list of writes replayed in order, so a row states its own
/// inputs and cannot be invalidated by anything the running game did between
/// calls — docs/TRAPS-SIMULATION.md, "Every fixture row should carry its own inputs".
/// Regenerate with a live-game sampler.
/// </para>
/// </remarks>
public sealed class MotionSampleTests
{
    /// <summary>
    /// How far a component or a speed may sit from the engine's, per unit of
    /// speed.
    /// </summary>
    /// <remarks>
    /// The error scales with the magnitude, so the bound is relative: the worst
    /// of 1,197 sampled values is <b>1.46e-15</b> per unit of speed, about seven
    /// machine epsilons, and this leaves a little room above it.
    /// <para>
    /// <b>Why that is small enough to build on.</b> GML's own comparison
    /// operators are fuzzy to 1e-13 (docs/SIMULATION.md), so the reference
    /// cannot itself distinguish a difference of this size; a projectile lives
    /// at most 40 ticks, so the accumulated position error is under 1e-12 of a
    /// pixel; positions reach the wire quantised to a fifth of a pixel; and
    /// collision runs on integer bounding boxes. The one thing that <em>is</em>
    /// discrete — the 1e-4 snap — is asserted exactly, on rows chosen so that no
    /// trig can perturb them.
    /// </para>
    /// </remarks>
    private const double ComponentTolerance = 2e-15;

    /// <summary>How far a recomputed <c>direction</c> may sit from the engine's, in degrees.</summary>
    /// <remarks>Worst of 394 sampled directions: <b>1.14e-13</b> degrees.</remarks>
    private const double DirectionTolerance = 2e-13;

    /// <summary>
    /// A component this small is the residue of a cancellation, and its
    /// direction is noise on both sides.
    /// </summary>
    /// <remarks>
    /// <c>motion_add</c> of an equal and opposite push leaves about 1e-16 in a
    /// component rather than a true zero (that is the whole point of the
    /// <c>motion_add</c> family), and <c>arctan2</c> of two such residues is
    /// whatever the last bits happen to say — the engine and the port disagree
    /// by tens of degrees there, and both answers are equally meaningless. The
    /// <em>components</em> of those rows are still checked; only the direction
    /// is skipped.
    /// </remarks>
    private const double NoiseFloor = 1e-10;

    /// <summary>A written value, as the numerator and denominator that were sent.</summary>
    /// <remarks>
    /// Ratios rather than decimals because GM8 parses some decimal literals one
    /// ULP above the IEEE nearest (docs/SIMULATION.md); <c>n/d</c> is one
    /// correctly rounded division on both sides and cannot drift.
    /// </remarks>
    private static double Value(double[] ratio) => ratio[0] / ratio[1];

    private sealed record Operation
    {
        [JsonPropertyName("op")] public required string Op { get; init; }

        [JsonPropertyName("a")] public required double[] A { get; init; }

        /// <summary>Only <c>motion_add</c> has a second argument.</summary>
        [JsonPropertyName("b")] public double[]? B { get; init; }
    }

    private sealed record Measured
    {
        /// <summary>The engine's value, decoded from the mantissa encoder.</summary>
        [JsonPropertyName("value")] public required double Value { get; init; }
    }

    private sealed record Sample
    {
        [JsonPropertyName("family")] public required string Family { get; init; }

        [JsonPropertyName("label")] public required string Label { get; init; }

        [JsonPropertyName("ops")] public required Operation[] Ops { get; init; }

        [JsonPropertyName("hspeed")] public required Measured HSpeed { get; init; }

        [JsonPropertyName("vspeed")] public required Measured VSpeed { get; init; }

        [JsonPropertyName("direction")] public required Measured Direction { get; init; }

        [JsonPropertyName("speed")] public required Measured Speed { get; init; }

        /// <summary>Both components have cancelled to the noise floor.</summary>
        public bool IsResidue =>
            Math.Abs(HSpeed.Value) < NoiseFloor
            && Math.Abs(VSpeed.Value) < NoiseFloor
            && (HSpeed.Value != 0 || VSpeed.Value != 0);
    }

    private sealed record TrigRow
    {
        [JsonPropertyName("direction")] public required double[] Direction { get; init; }

        [JsonPropertyName("lengthdirX")] public required Measured LengthdirX { get; init; }

        [JsonPropertyName("lengthdirY")] public required Measured LengthdirY { get; init; }

        [JsonPropertyName("cos")] public required Measured Cos { get; init; }

        [JsonPropertyName("sin")] public required Measured Sin { get; init; }

        [JsonPropertyName("degtorad")] public required Measured DegToRad { get; init; }
    }

    private sealed record Document
    {
        [JsonPropertyName("samples")] public required Sample[] Samples { get; init; }

        [JsonPropertyName("trig")] public required TrigRow[] Trig { get; init; }
    }

    private static Document Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "motion-samples.json");
        Assert.True(File.Exists(path), $"missing {path}");
        return JsonSerializer.Deserialize<Document>(File.ReadAllBytes(path))!;
    }

    private static Motion Replay(Operation[] ops)
    {
        var motion = default(Motion);

        foreach (var op in ops)
        {
            var a = Value(op.A);

            switch (op.Op)
            {
                case "direction=": motion.Direction = a; break;
                case "direction+=": motion.Direction += a; break;
                case "speed=": motion.Speed = a; break;
                case "speed+=": motion.Speed += a; break;
                case "hspeed=": motion.HSpeed = a; break;
                case "hspeed+=": motion.HSpeed += a; break;
                case "vspeed=": motion.VSpeed = a; break;
                case "vspeed+=": motion.VSpeed += a; break;
                case "motion_add": motion.MotionAdd(a, Value(op.B!)); break;
                default: throw new InvalidOperationException($"unknown op {op.Op}");
            }
        }

        return motion;
    }

    /// <summary>
    /// Every sampled write sequence leaves <c>hspeed</c>, <c>vspeed</c> and
    /// <c>speed</c> within <see cref="ComponentTolerance"/> of the engine, and
    /// the great majority of them exactly on it.
    /// </summary>
    /// <remarks>
    /// The exact-match floor is asserted as well as the bound. The bound alone
    /// would not notice a change that quietly moved a thousand values by one
    /// ULP each, and this is the one primitive in phase 5 that every projectile
    /// trajectory is built out of.
    /// </remarks>
    [Fact]
    public void ComponentsAndSpeedStayWithinTheMeasuredBound()
    {
        var samples = Load().Samples;
        Assert.NotEmpty(samples);

        var checkedValues = 0;
        var exact = 0;
        var worst = 0.0;
        var worstLabel = "";

        foreach (var sample in samples)
        {
            var motion = Replay(sample.Ops);

            // Relative to the speed, because that is what the error scales with:
            // the same direction at speed 100 is off by five times as much as at
            // speed 20.
            var scale = Math.Max(1, Math.Max(Math.Abs(sample.Speed.Value), Math.Abs(motion.Speed)));

            foreach (var (mine, engine) in new[]
                     {
                         (motion.HSpeed, sample.HSpeed.Value),
                         (motion.VSpeed, sample.VSpeed.Value),
                         (motion.Speed, sample.Speed.Value),
                     })
            {
                checkedValues++;

                if (mine.Equals(engine))
                {
                    exact++;
                    continue;
                }

                var error = Math.Abs(mine - engine) / scale;
                if (error > worst)
                {
                    worst = error;
                    worstLabel = sample.Label;
                }
            }
        }

        Assert.True(
            worst <= ComponentTolerance,
            $"worst component error {worst:E4} per unit speed exceeds {ComponentTolerance:E4} at: {worstLabel}");

        // Measured at 1045 of 1197 when this was settled. A floor rather than an
        // equality, so that a genuine improvement is not a failure.
        Assert.True(
            exact * 100 >= checkedValues * 85,
            $"only {exact} of {checkedValues} components are bit-exact; that was 87% when this was measured");
    }

    /// <summary>
    /// A recomputed <c>direction</c> stays within
    /// <see cref="DirectionTolerance"/> of the engine's.
    /// </summary>
    /// <remarks>
    /// Rows whose components have cancelled to the noise floor are skipped —
    /// see <see cref="NoiseFloor"/>. Their components are still checked by
    /// <see cref="ComponentsAndSpeedStayWithinTheMeasuredBound"/>.
    /// </remarks>
    [Fact]
    public void DirectionStaysWithinTheMeasuredBound()
    {
        var checkedRows = 0;
        var exact = 0;
        var worst = 0.0;
        var worstLabel = "";

        foreach (var sample in Load().Samples)
        {
            if (sample.IsResidue)
                continue;

            checkedRows++;
            var motion = Replay(sample.Ops);

            if (motion.Direction.Equals(sample.Direction.Value))
            {
                exact++;
                continue;
            }

            var error = Math.Abs(motion.Direction - sample.Direction.Value);
            if (error > worst)
            {
                worst = error;
                worstLabel = sample.Label;
            }
        }

        Assert.True(checkedRows > 0, "no non-degenerate samples in the fixture");
        Assert.True(
            worst <= DirectionTolerance,
            $"worst direction error {worst:E4} degrees exceeds {DirectionTolerance:E4} at: {worstLabel}");

        // Measured at 373 of 394.
        Assert.True(
            exact * 100 >= checkedRows * 92,
            $"only {exact} of {checkedRows} directions are bit-exact; that was 95% when this was measured");
    }

    /// <summary>
    /// Bit-for-bit on Windows, and within one ULP anywhere else. See the
    /// remarks on <see cref="TheSnapIsExactAndItsThresholdIsOneTenThousandth"/>
    /// for why the platform is part of the claim.
    /// </summary>
    private static void Same(string label, string field, double engine, double ported)
    {
        if (engine.Equals(ported))
            return;

        Assert.False(
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            $"{label}: {field} {ported:R} != engine {engine:R}");

        var ulps = Math.Abs(
            BitConverter.DoubleToInt64Bits(engine) - BitConverter.DoubleToInt64Bits(ported));

        Assert.True(
            ulps <= 1,
            $"{label}: {field} {ported:R} is {ulps} ULP from engine {engine:R}, and off "
            + "Windows only the platform math library's last bit is allowed to differ");
    }

    /// <summary>
    /// The snap is exact, and it fires <b>below 1e-4 and not at it</b>.
    /// </summary>
    /// <remarks>
    /// This is the one rule in <see cref="Motion"/> that changes a value by
    /// something a human could see — up to 1e-4, a billion times GML's
    /// comparison fuzz — so it is asserted bit-for-bit rather than to a bound.
    /// Most of the <c>snap</c> family is built so that it can be: a row aimed
    /// along an axis has <c>cos</c> exactly 1 and <c>sin</c> exactly 0, or
    /// writes a component directly, so nothing in
    /// <see cref="ComponentsAndSpeedStayWithinTheMeasuredBound"/>'s bound can
    /// hide a wrong threshold.
    /// <para>
    /// ⚠️ <b>Some rows DO run trig</b> — <c>direction 45.001 then speed 5</c> is
    /// one — and for those the bit-exact comparison is against the platform
    /// math library as much as against the snap. Windows agrees with the
    /// recorded engine, which is also a Windows program; Linux returns a
    /// different last bit for <c>cos</c> on about 3% of inputs, so the exact
    /// assertion is a Windows one and every other platform is held to one ULP
    /// instead. That is still a gate — a real regression moves a trajectory by
    /// far more — and it is the only claim the evidence supports.
    /// docs/SIMULATION.md, "The same build on two machines".
    /// </para>
    /// <para>
    /// The sweep steps the offset from an integer in units of 2^-30, which
    /// straddles 1e-4 at 107374.1824 of them — so the rows at 107374 and 107375
    /// bracket the threshold and pin it as strictly less-than.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSnapIsExactAndItsThresholdIsOneTenThousandth()
    {
        var rows = Load().Samples.Where(s => s.Family is "snap" or "noop").ToArray();
        Assert.NotEmpty(rows);

        foreach (var sample in rows)
        {
            var motion = Replay(sample.Ops);

            Same(sample.Label, "hspeed", sample.HSpeed.Value, motion.HSpeed);
            Same(sample.Label, "vspeed", sample.VSpeed.Value, motion.VSpeed);
            Same(sample.Label, "speed", sample.Speed.Value, motion.Speed);
        }

        // And the bracket itself, named rather than left implicit: the fixture
        // is regenerated by hand and a row that quietly stopped being generated
        // would otherwise take the threshold with it.
        var justInside = rows.Single(s => s.Label == "speed 3 + 107374/2^30 at dir 0");
        var justOutside = rows.Single(s => s.Label == "speed 3 + 107375/2^30 at dir 0");

        Assert.Equal(3.0, justInside.HSpeed.Value);
        Assert.NotEqual(3.0, justOutside.HSpeed.Value);
    }

    /// <summary>
    /// A direct write stores what it is given; only a <em>recomputed</em>
    /// variable snaps.
    /// </summary>
    /// <remarks>
    /// <b>This is what found the bug.</b> The model started life snapping
    /// <c>direction</c> and not <c>speed</c>, on the strength of the OpenGMK
    /// transcription. Writing <c>hspeed = 3.00005</c> on a stopped instance
    /// leaves <c>hspeed</c> at 3.00005 — a direct write — and reports
    /// <c>speed</c> as exactly <b>3</b>, because <c>update_speed_direction</c>
    /// snapped it. The same rows show the other half: <c>speed = 3.00005</c>
    /// keeps its 3.00005 while <c>hspeed</c> becomes exactly 3.
    /// </remarks>
    [Fact]
    public void ADirectWriteIsStoredAndOnlyTheRecomputedSideSnaps()
    {
        var rows = Load().Samples.Where(s => s.Family == "snap").ToArray();

        var speedWrite = rows.Single(s => s.Label == "speed 3 + 107374/2^30 at dir 0");
        Assert.Equal(3.0000999998301268, speedWrite.Speed.Value);
        Assert.Equal(3.0, speedWrite.HSpeed.Value);

        var componentWrite = rows.Single(s => s.Label == "hspeed 3.00005");
        Assert.Equal(3.00005, componentWrite.HSpeed.Value);
        Assert.Equal(3.0, componentWrite.Speed.Value);

        var directionWrite = rows.Single(s => s.Label == "direction 45.00005 then speed 5");
        Assert.Equal(45.00005, directionWrite.Direction.Value);

        // And a speed built out of two components snaps the same way, so it is
        // the recompute and not the write that does it.
        var fromComponents = rows.Single(s => s.Label == "h 3.000001 v -4 (speed 5.0000006)");
        Assert.Equal(5.0, fromComponents.Speed.Value);
    }

    /// <summary>
    /// Writing a component the value it already holds does not recompute
    /// <c>direction</c>.
    /// </summary>
    /// <remarks>
    /// An instance whose speed is zero has both components zero, so without the
    /// no-op rule every <c>hspeed = 0</c> would collapse its direction to 0 or
    /// 180 and it would forget where it was aimed. The <c>noop</c> family sets a
    /// direction, zeroes the speed and then writes a zero component; the
    /// direction has to survive, and it does — exactly, on every row.
    /// </remarks>
    [Fact]
    public void WritingAComponentItsOwnValueLeavesDirectionAlone()
    {
        var rows = Load().Samples.Where(s => s.Family == "noop").ToArray();
        Assert.NotEmpty(rows);

        foreach (var sample in rows)
        {
            // The direction the sample asked for is the last "direction=" in its
            // op list, normalised the way GM8 normalises it.
            var asked = sample.Ops.Last(o => o.Op == "direction=");
            var expected = Value(asked.A) % 360;
            if (expected < 0)
                expected += 360;

            Assert.Equal(expected, sample.Direction.Value);
            Assert.Equal(expected, Replay(sample.Ops).Direction);
        }
    }

    /// <summary>
    /// <c>motion_add</c> does <b>not</b> snap its components.
    /// </summary>
    /// <remarks>
    /// It adds to <c>hspeed</c> and <c>vspeed</c> and lets the polar pair be
    /// recomputed, so it takes the branch that has no snap in it. The fixture's
    /// cancelling pairs are what make that visible — a shot pushed one way at 7
    /// and back the other way at 7 keeps a residue of about 1e-16 rather than
    /// coming to rest. It is also the one place a <c>Shot</c> reaches:
    /// its collision with a character does
    /// <c>motion_add(other.direction, other.speed*0.03)</c> on the
    /// <em>victim</em>.
    /// <para>
    /// Note the asymmetry the same rows show: the residue survives in the
    /// components and is snapped away in <c>speed</c>, because <c>speed</c> is
    /// recomputed and the components are not.
    /// </para>
    /// </remarks>
    [Fact]
    public void MotionAddLeavesAResidueInTheComponents()
    {
        var rows = Load().Samples.Where(s => s.Family == "motion_add").ToArray();
        Assert.NotEmpty(rows);

        var sawResidue = false;

        foreach (var sample in rows)
        {
            if (!sample.IsResidue)
                continue;

            sawResidue = true;

            // The components kept something; the speed recomputed from them did
            // not, because 1e-16 is inside the 1e-4 window.
            Assert.True(
                sample.HSpeed.Value != 0 || sample.VSpeed.Value != 0,
                $"{sample.Label}: both components came back a true zero");
            Assert.Equal(0.0, sample.Speed.Value);

            var motion = Replay(sample.Ops);
            Assert.True(
                motion.HSpeed != 0 || motion.VSpeed != 0,
                $"{sample.Label}: the port snapped a residue the engine kept");
            Assert.Equal(0.0, motion.Speed);
        }

        Assert.True(sawResidue, "no cancelling motion_add in the fixture — it cannot show the missing snap");
    }

    /// <summary>
    /// <c>lengthdir_x</c>, <c>lengthdir_y</c>, <c>cos</c>, <c>sin</c> and
    /// <c>degtorad</c> against the engine — and the shape of where they differ.
    /// </summary>
    /// <remarks>
    /// Not motion variables, but the same event: every scattergun pellet is
    /// nudged fifteen pixels along its own direction with <c>lengthdir</c>
    /// before it is allowed to fly, and its lifetime alarm is computed from
    /// <c>cos(degtorad(aimDirection))</c>.
    /// <para>
    /// These are the GML-level functions rather than the engine-internal
    /// conversion, and they are much closer to <see cref="Math"/>: the results
    /// agree <b>exactly</b> everywhere except within a hair of a zero of
    /// <c>cos</c> or <c>sin</c>, where x87's 66-bit argument reduction shows
    /// through as an absolute difference of about 2e-20 per quadrant. That is
    /// asserted as an absolute bound rather than a relative one, because a
    /// relative bound near a zero is meaningless — and 2e-20 is sixteen orders
    /// below the snap that immediately swallows it.
    /// </para>
    /// </remarks>
    [Fact]
    public void LengthdirAndDegToRadTrackTheEngine()
    {
        var rows = Load().Trig;
        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var degrees = Value(row.Direction);
            var radians = degrees * (Math.PI / 180.0);

            // degtorad is a single multiply and lands on the engine's double for
            // all but a couple of angles, where GM8's extended-precision pi wins
            // by one ULP.
            Assert.True(
                Math.Abs(radians - row.DegToRad.Value) <= Math.Abs(radians) * 4 * double.Epsilon + 1e-15,
                $"degtorad({degrees}): {radians:R} vs engine {row.DegToRad.Value:R}");

            // Absolute, not relative: cos(90 degrees) is 6.1e-17 and the two
            // implementations disagree in its fourth digit while agreeing to
            // 2e-20 in absolute terms, which is the number that matters.
            Assert.True(
                Math.Abs(Math.Cos(radians) - row.Cos.Value) < 1e-15,
                $"cos({degrees}): {Math.Cos(radians):R} vs engine {row.Cos.Value:R}");
            Assert.True(
                Math.Abs(Math.Sin(radians) - row.Sin.Value) < 1e-15,
                $"sin({degrees}): {Math.Sin(radians):R} vs engine {row.Sin.Value:R}");
            // Through Gml, because lengthdir applies the 1e-4 snap and a raw
            // `15 * Math.Cos` does not: the engine answers lengthdir_y(15,
            // 89.999) with exactly -15, and the unsnapped product is
            // -14.999999998. That is a difference of 2e-9 — seven orders above
            // anything the tolerance below covers, so this line is what would
            // catch the snap going missing from lengthdir.
            //
            // The length multiplies the residual trig difference through, so
            // the bound does too: ComponentTolerance at a magnitude of 15.
            Assert.True(
                Math.Abs(Gml.LengthDirX(15, degrees) - row.LengthdirX.Value) < 15 * ComponentTolerance,
                $"lengthdir_x(15, {degrees}): {Gml.LengthDirX(15, degrees):R} vs engine {row.LengthdirX.Value:R}");
            Assert.True(
                Math.Abs(Gml.LengthDirY(15, degrees) - row.LengthdirY.Value) < 15 * ComponentTolerance,
                $"lengthdir_y(15, {degrees}): {Gml.LengthDirY(15, degrees):R} vs engine {row.LengthdirY.Value:R}");
        }
    }
}
