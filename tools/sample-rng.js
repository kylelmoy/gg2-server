#!/usr/bin/env node
//=============================================================================
// sample-rng.js - ask the running game what its random number generator does,
// and write it down.
//
// Phase 5 needs this and phases 0-4 did not, because weapon fire is the one
// place GG2's randomness is *shared with the client*. A weapon's User Event 1
// draws a seed, puts it on the wire (sendEventFireWeapon.gml), and then both
// sides run random_set_seed(seed) and re-derive the shot spread themselves
// (doEventFireWeapon.gml). The server broadcasts the seed, not the result - so
// which pellets hit is decided by GM8's generator on both machines, and the
// port has to be the same generator.
//
// That makes docs/SIMULATION.md's "server-side RNG is free" too broad. It is
// free for spawn points and chat bubbles; it is contract for weapon fire.
//
// What this measures, all of it against the live engine:
//
//   1. the seed chain     - random_set_seed(s) then repeated advances
//   2. random(x)          - to the last bit, via the mantissa encoder
//   3. irandom(n)         - which integers come out
//   4. choose(...)        - which index it picks, and that it costs one advance
//
// Every measurement re-seeds immediately before drawing, so the game's own
// frames between evals cannot perturb it.
//
// Requires a running dedicated server with the agent bridge. No bots needed.
//
//   node tools/sample-rng.js [--out <file>]
//=============================================================================

const fs = require('fs');
const path = require('path');

// The exact-double encoder lives in tools/exact-double.js, which is one
// copy of what used to be four - and three of those four were wrong. Run
// `node tools/exact-double.js` to self-test it; it needs no game.
const exact = require(path.join(__dirname, 'exact-double.js'));


const AGENT = process.env.GG2_AGENT || 'D:/code/gg2-agent';
const mcp = require(path.join(AGENT, 'tools', 'gg2-mcp-server.js'));

function arg(name, fallback) {
  const i = process.argv.indexOf('--' + name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

const OUT = arg('out', 'tests/Gg2.Tests/Fixtures/rng-samples.json');

async function evalx(expr) {
  const r = await mcp.callTool('gg2_evalx', { expr });
  const text = typeof r === 'string' ? r : (r?.content?.[0]?.text ?? '');
  return String(text).replace(/^"|"$/g, '');
}

const ev = (code) => mcp.callTool('gg2_eval', { code });

// Runs a GML block that appends to global.rngOut, then reads it back.
// Kept small per batch: the bridge hands values back as one string.
async function batch(body) {
  await ev('global.rngOut = ""; ' + body);
  return (await evalx('global.rngOut')).trim();
}



// GM8 hands a 32-bit seed back through GML's one numeric type, signed.
const toUnsigned = (n) => (n < 0 ? n + 4294967296 : n);

//---------------------------------------------------------------------------
// What to ask
//---------------------------------------------------------------------------

// Seeds worth asking about: the edges, GG2's own irandom(65535) range, and a
// spread that is not a multiple of anything.
const SEEDS = [
  0, 1, 2, 65535, 65536, 12345, 1000003, 2147483647, 4294967295,
  987654321, 424242, 7, 99999, 31337, 2000000011,
];

const CHAIN_LENGTH = 8;

// Every argument GG2 actually passes to random(), plus two fractions to catch a
// rule that only holds for whole numbers.
//
// Both fractions are dyadic on purpose. 1.75 was in this list and had to come
// out: all fifteen seeds disagreed with the generator by exactly one ULP, and
// the cause was not the generator at all - GM8 parses the *literal* 1.75 one
// ULP high, the same bug docs/SIMULATION.md records for 0.6 and 0.85. A
// non-dyadic argument cannot separate the two questions, and GG2 never passes
// one, so this asks only the question it can answer.
const RANDOM_ARGS = [1, 4, 5, 7, 8, 9, 11, 14, 15, 30, 40, 80, 151, 0.25, 0.5];

// irandom's ceiling is inclusive, so 0 and 1 are the interesting small cases.
const IRANDOM_ARGS = [0, 1, 2, 3, 7, 100, 255, 65535];

// choose() is measured by its index rather than its value, so the count is all
// that matters. Shot's Create uses choose(0,0,1); three is the live case.
const CHOOSE_COUNTS = [2, 3, 4, 6];

//---------------------------------------------------------------------------

async function seedChains() {
  const out = [];
  for (const seed of SEEDS) {
    const text = await batch(`
      random_set_seed(${seed});
      var i;
      for(i = 0; i < ${CHAIN_LENGTH}; i += 1)
      {
          random(1);
          global.rngOut += string(random_get_seed()) + " ";
      }`);
    out.push({ seed, chain: text.split(/\s+/).map(Number).map(toUnsigned) });
    process.stdout.write('.');
  }
  return out;
}

async function randomValues() {
  const out = [];
  for (const seed of SEEDS) {
    for (const x of RANDOM_ARGS) {
      const text = await batch(`
        random_set_seed(${seed});
        var v;
        v = random(${x});
        ${exact.encodeInto('global.rngOut', 'v')}
        global.rngOut += " | " + string(random_get_seed());`);
      const [bits, advanced] = text.split('|');
      out.push({
        seed,
        arg: x,
        value: exact.decode(bits),
        bits: bits.trim(),
        advanced: toUnsigned(Number(advanced.trim())),
      });
      process.stdout.write('.');
    }
  }
  return out;
}

async function integerDraws() {
  const out = [];
  for (const seed of SEEDS) {
    const irandoms = await batch(
      IRANDOM_ARGS.map((n) => `
      random_set_seed(${seed});
      global.rngOut += string(irandom(${n})) + " ";`).join(''),
    );

    const chooses = await batch(
      CHOOSE_COUNTS.map((n) => `
      random_set_seed(${seed});
      global.rngOut += string(choose(${[...Array(n).keys()].join(',')})) + " ";
      global.rngOut += string(random_get_seed()) + " ";`).join(''),
    );

    const drawn = irandoms.split(/\s+/).map(Number);
    const chosen = chooses.split(/\s+/).map(Number);

    out.push({
      seed,
      irandom: Object.fromEntries(IRANDOM_ARGS.map((n, i) => [n, drawn[i]])),
      choose: Object.fromEntries(
        CHOOSE_COUNTS.map((n, i) => [
          n,
          { index: chosen[i * 2], advanced: toUnsigned(chosen[i * 2 + 1]) },
        ]),
      ),
    });
    process.stdout.write('.');
  }
  return out;
}

async function main() {
  process.stdout.write('seed chains: ');
  const chains = await seedChains();
  process.stdout.write('\nrandom(x):   ');
  const values = await randomValues();
  process.stdout.write('\nintegers:    ');
  const integers = await integerDraws();
  process.stdout.write('\n');

  const out = {
    generated: new Date().toISOString().slice(0, 10),
    chainLength: CHAIN_LENGTH,
    chains,
    values,
    integers,
  };

  fs.mkdirSync(path.dirname(OUT), { recursive: true });
  fs.writeFileSync(OUT, JSON.stringify(out, null, 1) + '\n');
  console.log(
    `wrote ${OUT}: ${chains.length} chains, ${values.length} random(x), ${integers.length} integer draws`,
  );
}

main().then(() => process.exit(0), (e) => { console.error(e); process.exit(1); });
