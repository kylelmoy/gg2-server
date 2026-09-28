#!/usr/bin/env node
//
// Regenerates src/Gg2.Server/Web/Assets/world.json — the geometry the admin
// panel's analytics map draws.
//
//   node tools/gen-geo-assets.js [--in <dir>] [--out <path>] [--check]
//
// The output is CHECKED IN. This script exists so the asset can be rebuilt and
// diffed rather than hand-edited, the same arrangement gg2-samplers/gen-map-fixture.js
// has with tests/Gg2.Tests/Fixtures/maps.json. Running it with no changes to
// the sources must produce an empty diff.
//
// Sources, all public domain (Natural Earth's own terms: "no permission needed"):
//   world-atlas land-110m             coastlines, no internal borders
//   ne_50m_admin_0_countries          country polygons, ISO 3166-1 alpha-2
//   ne_110m_admin_1_states_provinces  US states, ISO 3166-2
//
// The coastlines come from 110m and the country markers from 50m, which is not
// an inconsistency: 110m drops every country too small to draw — Singapore,
// Malta, Luxembourg, Hong Kong, Puerto Rico — and those are places players
// connect from. The outline is what a reader sees; the marker table is what a
// country code is looked up in, and it needs the small ones.
//
// They are downloaded to a cache directory on first run; --in points at one
// that already has them so the script works with no network.
//
// WHY THE OUTPUT IS ALREADY PROJECTED. Every coordinate in world.json is an
// integer in a 0..10000 display box, not a longitude and a latitude. The panel
// therefore contains no projection code at all: it scales a box to a canvas and
// draws. That is also what makes the Alaska and Hawaii insets free — they are
// ordinary geometry that was moved here, offline, once, instead of two special
// cases in the drawing loop.

const fs = require('fs');
const path = require('path');

const SOURCES = {
  'land-110m.json':
    'https://cdn.jsdelivr.net/npm/world-atlas@2/land-110m.json',
  'ne_50m_admin_0_countries.geojson':
    'https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_50m_admin_0_countries.geojson',
  'ne_110m_admin_1_states_provinces.geojson':
    'https://raw.githubusercontent.com/nvkelso/natural-earth-vector/master/geojson/ne_110m_admin_1_states_provinces.geojson',
};

/** The display box every emitted coordinate lives in. */
const GRID = 10000;

/**
 * Natural Earth leaves ISO_A2 as "-99" for a handful of countries — mostly
 * where sovereignty is disputed, plus France and Norway, whose codes live on
 * their sovereign entries rather than their mapped ones. Without this the two
 * largest of those are simply missing from the map.
 */
const ISO_FIXUPS = {
  France: 'FR',
  Norway: 'NO',
  'Northern Cyprus': 'CY',
  Somaliland: 'SO',
  Kosovo: 'XK',
  'Republic of Serbia': 'RS',
  'Turkish Republic of Northern Cyprus': 'CY',
};

function arg(name, fallback) {
  const i = process.argv.indexOf(name);
  return i >= 0 && i + 1 < process.argv.length ? process.argv[i + 1] : fallback;
}

async function load(dir, name) {
  const file = path.join(dir, name);

  if (!fs.existsSync(file)) {
    const url = SOURCES[name];
    process.stderr.write(`fetching ${name} …\n`);
    const response = await fetch(url);

    if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);

    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(file, Buffer.from(await response.arrayBuffer()));
  }

  return JSON.parse(fs.readFileSync(file, 'utf8'));
}

/* ---------- TopoJSON ------------------------------------------------------- */

/** Decodes one topology's arcs into absolute [lon, lat] point lists. */
function decodeArcs(topology) {
  const { scale, translate } = topology.transform;

  return topology.arcs.map((arc) => {
    let x = 0;
    let y = 0;

    return arc.map(([dx, dy]) => {
      x += dx;
      y += dy;
      return [x * scale[0] + translate[0], y * scale[1] + translate[1]];
    });
  });
}

/** Stitches an arc index list (negative means "that arc, reversed") into a ring. */
function stitch(arcs, indices) {
  const ring = [];

  for (const index of indices) {
    const arc = index < 0 ? arcs[~index].slice().reverse() : arcs[index];

    // The shared endpoint between two arcs is present in both.
    for (let i = ring.length === 0 ? 0 : 1; i < arc.length; i++) ring.push(arc[i]);
  }

  return ring;
}

/** Every ring in a TopoJSON object, as absolute lon/lat point lists. */
function topoRings(topology, objectName) {
  const arcs = decodeArcs(topology);
  const object = topology.objects[objectName];
  const rings = [];

  const walk = (geometry) => {
    if (geometry.type === 'GeometryCollection') {
      geometry.geometries.forEach(walk);
    } else if (geometry.type === 'Polygon') {
      geometry.arcs.forEach((r) => rings.push(stitch(arcs, r)));
    } else if (geometry.type === 'MultiPolygon') {
      geometry.arcs.forEach((p) => p.forEach((r) => rings.push(stitch(arcs, r))));
    }
  };

  walk(object);
  return rings;
}

/* ---------- GeoJSON -------------------------------------------------------- */

/** Every ring of a GeoJSON geometry, as lon/lat point lists. */
function geoRings(geometry) {
  if (!geometry) return [];
  if (geometry.type === 'Polygon') return geometry.coordinates;
  if (geometry.type === 'MultiPolygon') return geometry.coordinates.flat();
  return [];
}

/** Twice the signed area of a ring, in square degrees. Sign gives the winding. */
function signedArea(ring) {
  let sum = 0;

  for (let i = 0, j = ring.length - 1; i < ring.length; j = i++)
    sum += ring[j][0] * ring[i][1] - ring[i][0] * ring[j][1];

  return sum;
}

/**
 * A representative point for a feature: the area centroid of its LARGEST ring.
 *
 * Not the centroid of every ring together, which for a country with distant
 * territories (France, the United States, Norway) lands in the ocean between
 * them. The largest ring is the part a reader is looking at.
 */
function representativePoint(rings) {
  let best = null;
  let bestArea = 0;

  for (const ring of rings) {
    const area = Math.abs(signedArea(ring));

    if (area > bestArea) {
      bestArea = area;
      best = ring;
    }
  }

  if (!best) return null;

  const twice = signedArea(best);

  // A degenerate ring (three collinear points) has no centroid; fall back to
  // the mean of its vertices rather than dividing by zero.
  if (Math.abs(twice) < 1e-9) {
    const mean = best.reduce((a, p) => [a[0] + p[0], a[1] + p[1]], [0, 0]);
    return [mean[0] / best.length, mean[1] / best.length];
  }

  let x = 0;
  let y = 0;

  for (let i = 0, j = best.length - 1; i < best.length; j = i++) {
    const cross = best[j][0] * best[i][1] - best[i][0] * best[j][1];
    x += (best[j][0] + best[i][0]) * cross;
    y += (best[j][1] + best[i][1]) * cross;
  }

  return [x / (3 * twice), y / (3 * twice)];
}

/* ---------- projection ----------------------------------------------------- */

/**
 * Equirectangular with a standard parallel: x is stretched by cos(parallel) so
 * a country is not twice as wide as it looks on a globe. `parallel` 0 is the
 * plain plate carrée the world map wants; 38° is what makes the contiguous
 * United States the shape an American expects.
 */
function projector(bounds, parallel) {
  const k = Math.cos((parallel * Math.PI) / 180);
  const [west, south, east, north] = bounds;
  const width = (east - west) * k;
  const height = north - south;

  return {
    aspect: width / height,
    project: ([lon, lat]) => [
      ((lon - west) * k) / width,
      (north - lat) / height,
    ],
  };
}

/* ---------- emitting ------------------------------------------------------- */

/**
 * Quantises a projected ring onto the display grid and drops the points that
 * land on top of their predecessor.
 *
 * ⚠️ The grid is what makes the file small — the sources carry seven decimal
 * places of a coastline nobody will render wider than a thousand pixels — and
 * it is applied BEFORE the duplicate test, so the duplicates it creates are the
 * ones removed.
 */
function quantise(ring, project) {
  const parts = [];
  let out = [];
  let lastX = NaN;
  let lastY = NaN;

  for (const point of ring) {
    const [u, v] = project(point);
    const x = Math.round(u * GRID);
    const y = Math.round(v * GRID);

    // ⚠️ THE ANTIMERIDIAN, and it is not a rounding concern. Natural Earth cuts
    // a landmass that crosses 180° into points at both -180 and +180, and in an
    // equirectangular projection those are the two opposite edges of the map —
    // so the segment between them is drawn as a line straight across the world.
    // Measured, not reasoned about: Antarctica, Chukotka, Wrangel Island and
    // one Fijian island each put a full-width horizontal line on the panel's
    // map. A jump of more than half the world is that crossing and nothing
    // else, so the ring is CUT there and each piece closes against its own
    // edge, which is where its points already are.
    if (out.length !== 0 && Math.abs(x - lastX) > GRID / 2) {
      parts.push(out);
      out = [];
      lastX = NaN;
      lastY = NaN;
    }

    if (x === lastX && y === lastY) continue;

    out.push(x, y);
    lastX = x;
    lastY = y;
  }

  if (out.length !== 0) parts.push(out);

  return parts;
}

/**
 * Drops rings too small to read.
 *
 * At the panel's size one grid unit is well under a pixel, so a ring whose
 * whole bounding box is a few units is a dot of noise that costs bytes. The
 * threshold is a diagonal, not an area, so a long thin island survives.
 */
function bigEnough(flat, minimum) {
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;

  for (let i = 0; i < flat.length; i += 2) {
    minX = Math.min(minX, flat[i]);
    maxX = Math.max(maxX, flat[i]);
    minY = Math.min(minY, flat[i + 1]);
    maxY = Math.max(maxY, flat[i + 1]);
  }

  return flat.length >= 6 && Math.hypot(maxX - minX, maxY - minY) >= minimum;
}

/* ---------- the two views -------------------------------------------------- */

function buildWorld(land, countries) {
  const { aspect, project } = projector([-180, -90, 180, 90], 0);

  const rings = topoRings(land, 'land')
    .flatMap((ring) => quantise(ring, project))
    .filter((flat) => bigEnough(flat, 18));

  const points = {};

  for (const feature of countries.features) {
    const p = feature.properties;
    const name = p.NAME_EN || p.NAME || p.ADMIN;
    let code = p.ISO_A2_EH && p.ISO_A2_EH !== '-99' ? p.ISO_A2_EH : p.ISO_A2;

    if (!code || code === '-99') code = ISO_FIXUPS[p.ADMIN] || ISO_FIXUPS[name];
    if (!code || code === '-99' || points[code]) continue;

    const point = representativePoint(geoRings(feature.geometry));

    if (!point) continue;

    const [u, v] = project(point);
    points[code] = [Math.round(u * GRID), Math.round(v * GRID), name];
  }

  return { aspect: +aspect.toFixed(4), rings, points };
}

/**
 * The contiguous states in one projection, with Alaska and Hawaii moved into
 * the lower left at a smaller scale — the arrangement every printed US map
 * uses, done here so the panel never has to know about it.
 */
const INSETS = {
  // `scale` is the fraction of the main view's WIDTH the inset occupies, and
  // its height follows from its own projection — an inset given a height of its
  // own would be the one place in this file where a shape is stretched.
  'US-AK': { bounds: [-180, 51, -129.5, 72], scale: 0.22, at: [0.02, 0.62] },
  'US-HI': { bounds: [-160.6, 18.6, -154.6, 22.4], scale: 0.09, at: [0.28, 0.86] },
};

function buildUnitedStates(states) {
  // The lower 48 plus DC. Alaska crosses the antimeridian and Hawaii is 3,000
  // miles out; both are placed by INSETS instead.
  const contiguous = [-125, 24.4, -66.9, 49.5];
  const { aspect, project } = projector(contiguous, 38);

  const rings = [];
  const points = {};
  const boxes = [];

  for (const feature of states.features) {
    const p = feature.properties;
    const code = p.iso_3166_2;

    if (!code || !code.startsWith('US-')) continue;

    const inset = INSETS[code];
    const geometry = geoRings(feature.geometry);

    // An inset is its own little projection, then squeezed into a slot in the
    // main view's unit box. Same operation for its outline and its bubble, so
    // the two cannot come apart.
    const place = inset
      ? (() => {
          const sub = projector(inset.bounds, (inset.bounds[1] + inset.bounds[3]) / 2);
          const w = inset.scale;
          const h = (inset.scale / sub.aspect) * aspect;

          return ([lon, lat]) => {
            const [u, v] = sub.project([lon, lat]);
            return [inset.at[0] + u * w, inset.at[1] + v * h];
          };
        })()
      : project;

    if (inset) {
      const sub = projector(inset.bounds, (inset.bounds[1] + inset.bounds[3]) / 2);
      const w = inset.scale;
      const h = (inset.scale / sub.aspect) * aspect;

      boxes.push([
        Math.round(inset.at[0] * GRID),
        Math.round(inset.at[1] * GRID),
        Math.round(w * GRID),
        Math.round(h * GRID),
        p.postal,
      ]);
    }

    // ⚠️ An inset is a window, not a transform: Alaska's Aleutians cross the
    // antimeridian into positive longitude, and projecting those through
    // bounds that stop at -180 puts a chain of specks across the whole map.
    // Rings that are not wholly inside the window are dropped instead.
    const inside = inset
      ? geometry.filter((ring) => ring.every(([lon, lat]) =>
          lon >= inset.bounds[0] && lon <= inset.bounds[2]
          && lat >= inset.bounds[1] && lat <= inset.bounds[3]))
      : geometry;

    for (const ring of inside) {
      // A lower threshold than the world's: at this zoom the Florida keys and
      // the Chesapeake islands are shapes a reader recognises.
      for (const flat of quantise(ring, place))
        if (bigEnough(flat, 8)) rings.push(flat);
    }

    const point = representativePoint(inside.length !== 0 ? inside : geometry);

    if (point) {
      const [u, v] = place(point);
      points[p.postal] = [Math.round(u * GRID), Math.round(v * GRID), p.name];
    }
  }

  return { aspect: +aspect.toFixed(4), rings, points, boxes };
}

/* ---------- main ----------------------------------------------------------- */

(async () => {
  const cache = arg('--in', path.join(require('os').tmpdir(), 'gg2-geo-sources'));
  const out = arg('--out', path.join(__dirname, '..', 'src', 'Gg2.Server', 'Web', 'Assets', 'world.json'));

  const [land, countries, states] = await Promise.all([
    load(cache, 'land-110m.json'),
    load(cache, 'ne_50m_admin_0_countries.geojson'),
    load(cache, 'ne_110m_admin_1_states_provinces.geojson'),
  ]);

  const document = {
    source: 'Natural Earth 1:110m, public domain. Rebuild with tools/gen-geo-assets.js.',
    grid: GRID,
    views: {
      world: buildWorld(land, countries),
      us: buildUnitedStates(states),
    },
  };

  // One view per line rather than pretty-printed: the rings are the file, and
  // a diff of "did the geometry change" is unreadable at one number per line.
  const text =
    '{\n'
    + `"source":${JSON.stringify(document.source)},\n`
    + `"grid":${document.grid},\n`
    + '"views":{\n'
    + Object.entries(document.views)
      .map(([name, view]) =>
        `${JSON.stringify(name)}:{`
        + `"aspect":${view.aspect},`
        + `"rings":[\n${view.rings.map((r) => JSON.stringify(r)).join(',\n')}\n],`
        + `"points":{\n${Object.entries(view.points).map(([k, v]) => `${JSON.stringify(k)}:${JSON.stringify(v)}`).join(',\n')}\n}`
        + (view.boxes ? `,"boxes":${JSON.stringify(view.boxes)}` : '')
        + '}')
      .join(',\n')
    + '\n}\n}\n';

  if (process.argv.includes('--check')) {
    const current = fs.existsSync(out) ? fs.readFileSync(out, 'utf8') : '';
    const same = current === text;
    process.stderr.write(same ? 'world.json is up to date\n' : 'world.json is STALE\n');
    process.exit(same ? 0 : 1);
  }

  fs.writeFileSync(out, text);

  const kb = (Buffer.byteLength(text) / 1024).toFixed(1);
  process.stderr.write(
    `wrote ${out}: ${kb} KB, `
    + `${document.views.world.rings.length} world rings / ${Object.keys(document.views.world.points).length} countries, `
    + `${document.views.us.rings.length} US rings / ${Object.keys(document.views.us.points).length} states\n`);
})();
