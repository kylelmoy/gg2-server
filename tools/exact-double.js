#!/usr/bin/env node
//=============================================================================
// exact-double.js - reading a GM8 double back to the last bit.
//
// `string_format(v, 1, 17)` is good to about sixteen significant digits and the
// seventeenth is noise, which is useless when the question is a single ULP. So
// every sampler that needs an exact value pulls the double apart inside the
// game and puts it back together here.
//
// ⚠️ THIS FILE EXISTS BECAUSE THERE WERE FOUR COPIES OF IT AND ONE OF THEM WAS
// RIGHT.
//
// sample-rng.js, sample-physics.js and sample-primitives.js each carried the
// same encoder, and it opened with `if (v == 0)` and closed with two `while`
// normalisation loops. **Every one of those comparisons is GML's fuzzy one**:
// two reals compare equal when they are within 1e-13 of each other
// (docs/SIMULATION.md, "GML's comparisons are fuzzy"). So:
//
//   * `v == 0` is TRUE for cos(degtorad(90)), which is 6.1e-17, and the encoder
//     reported a hard zero for it - the exact value a motion sampler exists to
//     measure;
//   * `while (m >= 2)` and `while (m < 1)` could exit with the mantissa outside
//     [1, 2) and lose the last bit.
//
// sample-motion.js was written later and got it right. This is that version,
// with the two output shapes the four callers between them need. Their
// checked-in fixtures were audited when the bug was found and none of them took
// the fuzzy branch, so those oracles are sound as they stand - but nothing may
// be regenerated with the old encoder.
//
// Why this one is safe. Nothing here compares a small number to anything:
//
//   * the zero and sign tests are made on the value scaled by 2^400, which puts
//     any real double a sampler can see far outside the epsilon;
//   * there are no normalisation loops.
//
// ⚠️ TWO THINGS THE VERSION IN sample-motion.js GOT WRONG, found by the
// self-test below rather than by reading:
//
//   1. It scaled by 2^(52-e), and its comment claimed the result "is an exact
//      integer in every case". It is not. `floor(log2(a))` OVERSHOOTS BY ONE
//      whenever `a` is close enough below a power of two that log2 rounds up -
//      2^53 - 1 is the smallest example - and then a * 2^(52-e) is M/2 for an
//      odd mantissa M, a HALF-integer. GM8 would then print "262143.50" for a
//      chunk and the last bit would be lost silently. Scaling by 2^(53-e)
//      instead absorbs the overshoot: the result is M or 2M, both integers,
//      both under 2^54, and the decoder takes the extra bit back out.
//   2. `power(2, 52 - e)` OVERFLOWS TO INFINITY for a value below about
//      2^-972, because the exponent passes 1024. The scale is done in two
//      steps here - normalise to the mantissa with 2^-e, THEN scale by 2^53 -
//      so the first intermediate is always in [1, 2) and the second always in
//      [2^53, 2^54). Nothing overflows above a denormal.
//
// Neither is reachable by the values these samplers actually collect, which is
// why motion-samples.json is sound. Both are one line away from being
// reachable by the next one.
//
// Chunks stay under 2^19 throughout, because GM8's floor() truncates through a
// 32-bit integer and wraps silently above 2^31 (docs/SIMULATION.md, "floor()
// truncates through a 32-bit integer"): the widest is 2^54 / 2^36 = 2^18.
//
// Run `node tools/exact-double.js` to self-test. It needs no game: every GML
// operation the fragment uses is a power-of-two scale, a floor or a log2, and
// JS reproduces all three bit for bit - which is the whole point of choosing
// them. What it cannot check is the fuzzy comparison, which is exactly what
// this version no longer has.
//=============================================================================

'use strict';

// The body both output shapes share. `emit` is handed the five-part string
// expression and returns the GML that does something with it.
const body = (src, emit) => `
{
    global.mV = ${src};
    global.mZ = global.mV * power(2, 400);
    if (global.mZ == 0) { ${emit('"0 0 0 0 0"')} }
    else {
        global.mS = 1; global.mA = global.mV;
        if (global.mZ < 0) { global.mS = -1; global.mA = -global.mA; }
        global.mE = floor(log2(global.mA));
        global.mN = global.mA * power(2, -global.mE) * power(2, 53);
        global.mC1 = floor(global.mN / 68719476736);
        global.mN = global.mN - global.mC1 * 68719476736;
        global.mC2 = floor(global.mN / 262144);
        global.mC3 = global.mN - global.mC2 * 262144;
        ${emit('string(global.mS) + " " + string(global.mE) + " "\n            + string(global.mC1) + " " + string(global.mC2) + " " + string(global.mC3)')}
    }
}`;

/**
 * GML that assigns the encoding of `src` to the variable named by `dst`.
 * The shape sample-rng.js and sample-physics.js want.
 */
const encodeInto = (dst, src) => body(src, (v) => `${dst} = ${v};`);

/**
 * GML that appends the encoding of `src` to the variable named by `dst`.
 * The shape sample-primitives.js wants.
 */
const appendInto = (dst, src) => body(src, (v) => `${dst} += ${v};`);

/**
 * GML that appends the encoding of `src` to `global.mOut`, followed by " | ".
 * The shape sample-motion.js wants, and the one that was measured against the
 * running game.
 */
const appendToOut = (src) => body(src, (v) => `global.mOut += ${v} + " | ";`);

/**
 * The three chunks are an exact integer of up to 54 bits, so they are put back
 * together with BigInt: the sum overflows a double's exact integer range even
 * though the value it encodes does not.
 */
function decode(text) {
  const [sign, exponent, c1, c2, c3] = text.trim().split(/\s+/).map(Number);
  if (sign === 0) return 0;
  const n = (BigInt(c1) << 36n) + (BigInt(c2) << 18n) + BigInt(c3);
  return sign * Number(n) * Math.pow(2, exponent - 53);
}

//---------------------------------------------------------------------------
// The self-test
//
// A JS transcription of the GML fragment above, run over values chosen to
// break the encoder it replaces. It is a faithful stand-in because the fragment
// uses only operations JS and GM8 agree on exactly - and because the one thing
// they do NOT agree on, the fuzzy comparison, is what was removed.
//---------------------------------------------------------------------------
function encodeAsGmlWould(v) {
  const z = v * Math.pow(2, 400);
  if (z === 0) return '0 0 0 0 0';

  let s = 1;
  let a = v;
  if (z < 0) { s = -1; a = -a; }

  const e = Math.floor(Math.log2(a));
  let n = a * Math.pow(2, -e) * Math.pow(2, 53);
  const c1 = Math.floor(n / 68719476736);
  n -= c1 * 68719476736;
  const c2 = Math.floor(n / 262144);
  const c3 = n - c2 * 262144;

  return `${s} ${e} ${c1} ${c2} ${c3}`;
}

//---------------------------------------------------------------------------
// The encoder this replaces, transcribed faithfully - GML's fuzzy comparison
// included - so the self-test can assert that each of its three bugs is still a
// bug. If one of these ever stops reproducing, the transcription has drifted
// and the controls below are no longer controls.
//---------------------------------------------------------------------------
const GML_EPSILON = 1e-13;

function encodeAsOldGmlWould(v) {
  // `if (v == 0)` with GML's fuzzy equality: true for anything under 1e-13.
  if (Math.abs(v) < GML_EPSILON) return '0 0 0 0 0';

  let s = 1;
  let a = v;
  if (a < 0) { s = -1; a = -a; }

  let e = Math.floor(Math.log2(a));
  let m = a / Math.pow(2, e);
  while (m >= 2) { m /= 2; e += 1; }
  while (m < 1) { m *= 2; e -= 1; }

  let f = m * 262144;
  const c1 = Math.floor(f);
  f = (f - c1) * 262144;
  const c2 = Math.floor(f);
  f = (f - c2) * 65536;
  const c3 = Math.floor(f + 0.5);

  return `${s} ${e} ${c1} ${c2} ${c3}`;
}

function decodeOld(text) {
  const [sign, exponent, c1, c2, c3] = text.trim().split(/\s+/).map(Number);
  if (sign === 0) return 0;
  const mantissa = (c1 + (c2 + c3 / 65536) / 262144) / 262144;
  return sign * mantissa * Math.pow(2, exponent);
}

/** The old scale, which this file replaces with a normalise-then-scale pair. */
const scaleTheOldWay = (v) => Math.abs(v) * Math.pow(2, 52 - Math.floor(Math.log2(Math.abs(v))));

function selfTest() {
  const cases = [
    // ⚠️ The value the old encoder reported as a hard zero. GM8 answers
    // cos(degtorad(90)) with this, and a motion sample is about exactly that.
    6.123233995736766e-17,
    -6.123233995736766e-17,

    // The residues docs/SIMULATION.md names, all inside GML's 1e-13 fuzz.
    1.4e-16, 7.02e-12, 7.3108186171566558e-14, 2.7e-14,

    // The five literals GM8 parses one ULP above the IEEE nearest.
    0.6000000000000001, 0.8500000000000001, 1.1500000000000001,
    0.6500000000000001, 0.35000000000000003,

    // Ordinary physics, both signs, and the exact powers of two the
    // normalisation loops used to sit on the boundary of.
    0, 1, -1, 2, -2, 0.5, 1024, 1 / 3, -1 / 3,
    3.00005, 8.3, -8.3, 516.6899190010863, 14125.5014, 2117.5087,

    // Values whose mantissa is all ones or a single bit, which is where a
    // chunk boundary would show.
    Number.MAX_SAFE_INTEGER, Math.PI, Math.E, 1e-300, 1e300 / 1e120,
  ];

  let failures = 0;

  for (const v of cases) {
    const round = decode(encodeAsGmlWould(v));
    if (Object.is(round, v)) continue;

    failures++;
    console.error(`  FAIL ${v} -> ${round}  (${encodeAsGmlWould(v)})`);
  }

  // A sweep, so this is not only the cases somebody thought of.
  for (let i = 0; i < 20000; i++) {
    const v = (Math.random() - 0.5) * Math.pow(2, Math.floor(Math.random() * 80) - 40);
    if (Object.is(decode(encodeAsGmlWould(v)), v)) continue;

    failures++;
    if (failures < 5) console.error(`  FAIL swept ${v}`);
  }

  // The controls. A green run above only means "some encoder round-trips"
  // unless the ones this replaces demonstrably do not, so each of the three
  // bugs is transcribed and asserted to still be a bug.
  const controls = [
    ['the fuzzy zero test', () => decodeOld(encodeAsOldGmlWould(6.123233995736766e-17)) === 0],
    ['the 2^(52-e) half-integer',
      () => !Number.isInteger(scaleTheOldWay(Number.MAX_SAFE_INTEGER))],
    ['the power(2, 52-e) overflow', () => !Number.isFinite(scaleTheOldWay(1e-300))],
  ];

  for (const [name, stillBroken] of controls) {
    if (stillBroken()) continue;

    failures++;
    console.error(`  FAIL control "${name}": the bug this was fixed for no longer reproduces`);
  }

  if (failures > 0) {
    console.error(`exact-double: ${failures} failure(s)`);
    process.exit(1);
  }

  console.log('exact-double: round trip exact over 20,027 values, including');
  console.log('  cos(degtorad(90)) = 6.123233995736766e-17, which the encoder');
  console.log('  this replaces reported as a hard zero.');
}

module.exports = { encodeInto, appendInto, appendToOut, decode, encodeAsGmlWould };

if (require.main === module) selfTest();
