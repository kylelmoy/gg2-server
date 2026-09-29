/*
 * The analytics view: who has played, for how long, and roughly from where.
 *
 * Drawn by two pages. The admin panel (app.js) fetches one server's
 * `analytics` reply; the fleet page (scripts/fleet-analytics.sh, fleet.js)
 * hands it several servers' replies summed into the same shape, with a `fleet`
 * member saying so. Neither page's loading lives here, so this file draws an
 * object and never fetches one.
 *
 * Four things this had to be told:
 *
 *   1. ⚠️ Days arrive as arrays of bare values against a `dayFields` list, and
 *      the page indexes THROUGH that list rather than by position. Eight
 *      hundred days of six named fields is mostly repeated key names — and a
 *      column added on the server and not here would otherwise shift every
 *      series after it onto the wrong axis, silently.
 *
 *   2. ⚠️ A day with nobody on it is ABSENT from the reply, not zero. The chart
 *      fills the gaps itself, or a quiet week would draw as a continuous line
 *      and a busy server would look like it never stopped.
 *
 *   3. ⛔ The map places a marker on a country's or a state's own centroid, out
 *      of world.json. It never uses a coordinate from the geo database, which
 *      can place an address far more precisely than this panel should.
 *
 *   4. ⛔ A fleet total of "players" is a SUM of per-server counts. Each server
 *      hashes addresses with its own salt, so nothing can tell that the player
 *      on one server is the player on another; the figures say "summed" where
 *      they are drawn.
 */
'use strict';

let analytics = null;
let geometry = null;
let anMetric = 'unique';
let anWindow = 30;
let anView = 'world';

/** Bubbles currently on the map, for hit-testing. Rebuilt by drawGeoMap. */
let anBubbles = [];

/** The day bars currently drawn, likewise. */
let anBars = [];

/**
 * Turns the wire's rows-and-fields pair into objects.
 *
 * The one place a positional row becomes a named one, so the field list is
 * consulted exactly once and a server that adds a column cannot quietly move
 * an existing one.
 */
function named(rows, fields) {
  return (rows || []).map((row) => {
    const object = {};
    fields.forEach((field, i) => { object[field] = row[i]; });
    return object;
  });
}

const METRICS = {
  unique: { label: 'unique players', format: (v) => v.toLocaleString() },
  new: { label: 'first-time players', format: (v) => v.toLocaleString() },
  sessions: { label: 'joins', format: (v) => v.toLocaleString() },
  playtimeSeconds: { label: 'playtime', format: (v) => duration(v) },
  peak: { label: 'peak concurrent', format: (v) => v.toLocaleString() },
};

/**
 * Draws one `analytics` object into the sheet.
 *
 * @param data     a server's `analytics` reply, or fleet.js's sum of several.
 * @param world    the parsed world.json, or null — which costs the map and
 *                 nothing else.
 */
function showAnalytics(data, world) {
  analytics = data;
  geometry = world;

  $('an-off').hidden = true;

  // Not an error state. Nothing is counted and nothing is written until an
  // operator asks for it, so the job here is to name the setting that asks.
  if (!analytics.enabled) {
    $('an-loading').hidden = true;
    $('an-content').hidden = true;
    $('an-off').hidden = false;
    // ⚠️ The flag is named by the SERVER, in `hint`, and not repeated here.
    // Two copies of a flag's spelling in two languages is one copy that goes
    // stale, and the one that goes stale is the one the operator reads.
    $('an-off').textContent =
      'Analytics are off: nothing is counted and nothing is written. To turn them on, '
      + `${analytics.hint || 'set an analytics file'}, then restart.`;
    $('analytics-sub').textContent = 'not collecting';
    return;
  }

  analytics.dayRows = named(analytics.days, analytics.dayFields);
  analytics.regionRows = named(analytics.regions, analytics.regionFields);

  $('an-loading').hidden = true;
  $('an-content').hidden = false;
  renderAnalytics();
}

/** Redraws both canvases at their boxes' current size. For a resized window. */
function redrawAnalytics() {
  if (!analytics || !analytics.enabled) return;

  drawDays();
  drawGeoMap();
}

function renderAnalytics() {
  const t = analytics.totals;
  const fleet = analytics.fleet;

  $('analytics-sub').textContent = analytics.file;

  const today = analytics.dayRows.length !== 0
    ? analytics.dayRows[analytics.dayRows.length - 1]
    : null;

  const isToday = today && today.date === analytics.now.slice(0, 10);
  const peakAt = t.peakUtc ? t.peakUtc.replace('T', ' ').replace('Z', ' UTC') : '—';

  anKpis([
    ['players all time', t.uniquePlayers.toLocaleString(),
      fleet ? `summed over ${fleet.hosts.length} servers` : `since ${analytics.since.slice(0, 10)}`],
    ['joins', t.sessions.toLocaleString(), `${(t.sessions / Math.max(1, t.uniquePlayers)).toFixed(1)} per player`],
    ['time played', duration(t.playtimeSeconds), 'connected time, everybody'],
    // ⚠️ The busiest ONE server's peak. Per-server peaks happened at different
    // moments, so their sum is a crowd that never existed.
    ['peak at once', String(t.peakConcurrent), fleet && t.peakHost ? `${t.peakHost}, ${peakAt}` : peakAt],
    ['today', isToday ? today.unique.toLocaleString() : '0', isToday ? `${today.new} new` : 'nobody yet'],
    ['connected now', String(t.live), t.live === 1 ? 'player' : 'players'],
  ]);

  drawDays();
  drawGeoMap();
  places();
  topPlayers();
  foot();
}

/**
 * The six figures across the top.
 *
 * ⚠️ NOT called `kpis`. The performance card already owns a function by that
 * name, and a second top-level declaration of it silently replaced the first —
 * which broke the graphs' footer, on a page with no build step to catch it.
 */
function anKpis(items) {
  const host = $('an-kpis');
  host.innerHTML = '';

  for (const [key, value, note] of items) {
    const cell = document.createElement('div');
    cell.className = 'an-kpi';
    cell.appendChild(Object.assign(document.createElement('i'), { textContent: key }));
    cell.appendChild(Object.assign(document.createElement('b'), { textContent: value }));
    cell.appendChild(Object.assign(document.createElement('span'), { textContent: note }));
    host.appendChild(cell);
  }
}

/* ---------- the daily chart -------------------------------------------------
 *
 * ⚠️ Bars, not a line. The series is a count per calendar day with real gaps in
 * it; a line drawn through a day nobody played invents traffic that did not
 * happen, and a line is what every "activity over time" chart reaches for.
 */

/** Every day in the window, including the ones with nothing in them. */
function densify(rows, days) {
  if (rows.length === 0) return [];

  const byDate = new Map(rows.map((row) => [row.date, row]));
  const end = new Date(`${analytics.now.slice(0, 10)}T00:00:00Z`);
  const first = new Date(`${rows[0].date}T00:00:00Z`);

  const start = days > 0
    ? new Date(Math.max(first.getTime(), end.getTime() - (days - 1) * 86400000))
    : first;

  const dense = [];

  for (let at = new Date(start); at <= end; at = new Date(at.getTime() + 86400000)) {
    const date = at.toISOString().slice(0, 10);

    dense.push(byDate.get(date)
      || { date, sessions: 0, unique: 0, new: 0, playtimeSeconds: 0, peak: 0 });
  }

  return dense;
}

function drawDays() {
  const canvas = $('an-days');
  const data = densify(analytics.dayRows, anWindow);

  const pixels = Math.min(2, window.devicePixelRatio || 1);
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;

  if (width === 0) return;

  canvas.width = Math.round(width * pixels);
  canvas.height = Math.round(height * pixels);

  const g = canvas.getContext('2d');
  g.setTransform(pixels, 0, 0, pixels, 0, 0);
  g.clearRect(0, 0, width, height);

  anBars = [];

  $('an-days-from').textContent = data.length !== 0 ? data[0].date : '';
  $('an-days-to').textContent = data.length !== 0 ? data[data.length - 1].date : '';

  if (data.length === 0) {
    g.fillStyle = INK_3();
    g.font = '11px ui-monospace, Consolas, monospace';
    g.fillText('no days recorded yet', 12, height / 2);
    return;
  }

  const pad = { top: 16, right: 10, bottom: 6, left: 10 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;

  let peak = 0;

  for (const day of data) peak = Math.max(peak, day[anMetric] || 0);

  const top = niceCeil(Math.max(peak, 1));

  g.strokeStyle = GRID();
  g.lineWidth = 1;

  for (const fraction of [0, 0.5, 1]) {
    const line = Math.round(pad.top + plotH * fraction) + 0.5;
    g.beginPath();
    g.moveTo(pad.left, line);
    g.lineTo(pad.left + plotW, line);
    g.stroke();
  }

  g.fillStyle = INK_3();
  g.font = '10px ui-monospace, Consolas, monospace';
  g.fillText(METRICS[anMetric].format(top), pad.left + 1, pad.top - 4);

  const step = plotW / data.length;

  // A one-pixel gap between bars, and never below one pixel of bar: at "all"
  // over two years a proportional gap eats the whole series.
  const barWidth = Math.max(1, step - (step > 4 ? 2 : 0.5));

  for (let i = 0; i < data.length; i++) {
    const value = data[i][anMetric] || 0;
    const barHeight = Math.max(value > 0 ? 1 : 0, (value / top) * plotH);
    const x = pad.left + i * step;
    const y = pad.top + plotH - barHeight;

    anBars.push({ x, width: step, day: data[i] });

    if (barHeight === 0) continue;

    g.fillStyle = SERIES.aqua;
    g.fillRect(x, y, barWidth, barHeight);
  }
}

function onDaysHover(event) {
  const canvas = $('an-days');
  const box = canvas.getBoundingClientRect();
  const x = event.clientX - box.left;
  const tip = $('an-days-tip');

  const bar = anBars.find((b) => x >= b.x && x < b.x + b.width);

  if (!bar) { tip.hidden = true; return; }

  const day = bar.day;

  tip.hidden = false;
  tip.textContent =
    `${day.date} · ${day.unique} player${day.unique === 1 ? '' : 's'} `
    + `· ${day.sessions} join${day.sessions === 1 ? '' : 's'} `
    + `· ${duration(day.playtimeSeconds)} · peak ${day.peak}`;

  tip.style.left = `${Math.min(Math.max(bar.x + bar.width / 2, 90), box.width - 90)}px`;
  tip.style.top = `${box.height - 8}px`;
}

/* ---------- the map ---------------------------------------------------------
 *
 * A proportional-symbol map: one circle per place, its AREA proportional to the
 * player count. Area rather than radius, because a radius scale exaggerates a
 * large value by the square — the classic way a map like this lies.
 */

/** What the current view plots: place code → { players, sessions, seconds, name }. */
function mapTotals() {
  const totals = new Map();

  // ⚠️ Counted, not skipped. Three groups of players never get a marker, and
  // the map's caption has to be able to account for all three separately —
  // "296 of 300 placed" with no explanation is the kind of quiet arithmetic
  // that makes a reader distrust the whole panel.
  let unresolved = 0;   // the database had nothing for the address
  let outOfView = 0;    // resolved, but not somewhere this view can show

  for (const region of analytics.regionRows) {
    if (region.players === 0) continue;

    if (region.key === '??') {
      unresolved += region.players;
      continue;
    }

    // In the world view a US state is folded back into the United States, so
    // the two views always agree on the country's total.
    if (anView === 'world') {
      add(region.country, region.countryName, region);
      continue;
    }

    // The US view shows states. ⚠️ An American address the database placed to
    // a country but not to a state arrives here with the bare key "US", and it
    // is NEITHER out of view nor unplaceable — the fix for it is a city-level
    // database. `add` with an empty code puts it in `unresolved`, which is the
    // bucket whose caption says exactly that.
    if (region.country === 'US') add(region.region, region.regionName, region);
    else outOfView += region.players;
  }

  function add(code, name, region) {
    if (!code) { unresolved += region.players; return; }

    const at = totals.get(code) || { players: 0, sessions: 0, seconds: 0, name: '' };

    at.players += region.players;
    at.sessions += region.sessions;
    at.seconds += region.playtimeSeconds;
    at.name = name || at.name;

    totals.set(code, at);
  }

  return { totals, unresolved, outOfView };
}

function drawGeoMap() {
  const canvas = $('an-map');
  const note = $('an-map-note');

  anBubbles = [];

  const pixels = Math.min(2, window.devicePixelRatio || 1);
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;

  if (width === 0) return;

  canvas.width = Math.round(width * pixels);
  canvas.height = Math.round(height * pixels);

  const g = canvas.getContext('2d');
  g.setTransform(pixels, 0, 0, pixels, 0, 0);
  g.clearRect(0, 0, width, height);

  if (!geometry) {
    g.fillStyle = INK_3();
    g.font = '11px ui-monospace, Consolas, monospace';
    g.fillText('the map geometry could not be loaded', 12, height / 2);
    return;
  }

  const view = geometry.views[anView];
  const grid = geometry.grid;

  // Fit the view's own aspect into the box, centred. The box is a fixed height
  // so the sheet does not jump when the toggle changes the aspect.
  const scale = Math.min(width / view.aspect, height);
  const drawW = scale * view.aspect;
  const drawH = scale;
  const originX = (width - drawW) / 2;
  const originY = (height - drawH) / 2;

  const px = (x) => originX + (x / grid) * drawW;
  const py = (y) => originY + (y / grid) * drawH;

  // Land: filled one step off the card's own surface, with a hairline edge.
  // Two flat greys rather than a colour, so every bubble on top of it is the
  // only saturated thing on the map.
  g.fillStyle = token('--default', '#242327');
  g.strokeStyle = GRID();
  g.lineWidth = 0.5;
  g.beginPath();

  for (const ring of view.rings) {
    g.moveTo(px(ring[0]), py(ring[1]));

    for (let i = 2; i < ring.length; i += 2)
      g.lineTo(px(ring[i]), py(ring[i + 1]));

    g.closePath();
  }

  g.fill();
  g.stroke();

  // The Alaska and Hawaii insets, labelled — an unlabelled box in the corner of
  // a US map is the one thing a reader has to guess at.
  if (view.boxes) {
    g.strokeStyle = token('--separator', '#222123');
    g.fillStyle = INK_3();
    g.font = '9px ui-monospace, Consolas, monospace';

    for (const [x, y, w, h, label] of view.boxes) {
      g.strokeRect(px(x), py(y), (w / grid) * drawW, (h / grid) * drawH);
      g.fillText(label, px(x) + 3, py(y) + 10);
    }
  }

  const { totals, unresolved, outOfView } = mapTotals();
  let peak = 0;
  let placed = 0;

  // Resolved to a place world.json has no marker for. Gibraltar is the only
  // one at 50m, but a geo database naming a country this generator's sources
  // do not would land here too — so it is reported rather than absorbed.
  let noMarker = 0;

  for (const value of totals.values()) peak = Math.max(peak, value.players);

  // Sorted big-first so a large bubble never hides a small one it overlaps.
  const entries = [...totals.entries()].sort((a, b) => b[1].players - a[1].players);

  for (const [code, value] of entries) {
    const point = view.points[code];

    if (!point) {
      noMarker += value.players;
      continue;
    }

    placed += value.players;

    // Area ∝ players, with a floor so a single player is still a visible dot.
    const radius = Math.max(3, Math.sqrt(value.players / peak) * (anView === 'world' ? 26 : 30));
    const x = px(point[0]);
    const y = py(point[1]);

    anBubbles.push({ x, y, radius, code, name: point[2] || value.name, value });

    g.beginPath();
    g.arc(x, y, radius, 0, Math.PI * 2);

    // ⚠️ globalAlpha rather than a translucent colour string. The series
    // palette is shared with the charts, where it is opaque; deriving a
    // translucent variant per call would either duplicate the hex or depend on
    // color-mix() being accepted by a canvas, which is a narrower guarantee
    // than the stylesheet's.
    g.globalAlpha = 0.42;
    g.fillStyle = SERIES.aqua;
    g.fill();
    g.globalAlpha = 1;

    g.lineWidth = 1.25;
    g.strokeStyle = SERIES.aqua;
    g.stroke();
  }

  // ⛔ Every player is accounted for, in one of four buckets. A caption that
  // said "296 placed" out of 300 and stopped there is the panel quietly losing
  // four people, and a reader who notices has no way to find out where.
  const parts = [anView === 'world'
    ? `${placed.toLocaleString()} player${placed === 1 ? '' : 's'} on the map`
    : `${placed.toLocaleString()} placed by state`];

  if (outOfView > 0) parts.push(`${outOfView.toLocaleString()} outside the United States`);

  if (noMarker > 0) parts.push(`${noMarker.toLocaleString()} in a place this map has no marker for`);

  if (unresolved > 0) {
    parts.push(anView === 'us' && analytics.geoip && !analytics.geoip.regions
      ? `${unresolved.toLocaleString()} with no state — ${analytics.geoip.type} is a country-level database`
      : `${unresolved.toLocaleString()} the database could not place`);
  }

  if (!analytics.geoip) {
    parts.length = 0;
    parts.push('No geo database is loaded, so nobody can be placed. Pass --geoip <path.mmdb>');
  }

  note.textContent = parts.join(' · ') + '.';
}

function onMapHover(event) {
  const canvas = $('an-map');
  const box = canvas.getBoundingClientRect();
  const x = event.clientX - box.left;
  const y = event.clientY - box.top;
  const tip = $('an-map-tip');

  let best = null;

  for (const bubble of anBubbles) {
    const distance = Math.hypot(bubble.x - x, bubble.y - y);

    // Smallest match wins, so a small bubble sitting inside a large one is
    // still reachable.
    if (distance <= bubble.radius && (!best || bubble.radius < best.radius))
      best = bubble;
  }

  if (!best) { tip.hidden = true; return; }

  tip.hidden = false;
  tip.textContent =
    `${best.name || best.code} · ${best.value.players} player${best.value.players === 1 ? '' : 's'} `
    + `· ${duration(best.value.seconds)}`;

  tip.style.left = `${Math.min(Math.max(best.x, 90), box.width - 90)}px`;
  tip.style.top = `${best.y - best.radius}px`;
}

/* ---------- the two tables --------------------------------------------------- */

function places() {
  const body = $('an-places');
  body.innerHTML = '';

  const rows = analytics.regionRows.filter((r) => r.players > 0);

  $('an-places-empty').hidden = rows.length !== 0;

  for (const region of rows) {
    const tr = document.createElement('tr');

    // ⚠️ NOT `cell`. There is a shared cell() helper three rows down, and a
    // local const by that name shadows it for the whole function — which throws
    // at the first use rather than at the declaration, so the table simply did
    // not render and nothing said why.
    const first = document.createElement('td');
    const box = document.createElement('div');
    box.className = 'c-place';

    const code = document.createElement('span');
    code.className = 'c-code';
    code.textContent = region.key === '??' ? '—' : region.key;

    const name = document.createElement('span');
    name.className = 'who-name';

    // A US state is named with its country, because "California" and "Germany"
    // in one column with no other cue reads as two countries.
    name.textContent = region.key === '??'
      ? 'not resolved'
      : (region.key.startsWith('US-')
        ? `${region.regionName || region.region}, United States`
        : (region.countryName || region.country));

    box.appendChild(code);
    box.appendChild(name);
    first.appendChild(box);
    tr.appendChild(first);

    tr.appendChild(cell(region.players.toLocaleString(), 'c-num'));
    tr.appendChild(cell(region.sessions.toLocaleString(), 'c-num'));
    tr.appendChild(cell(duration(region.playtimeSeconds), 'c-num'));

    body.appendChild(tr);
  }
}

function topPlayers() {
  const body = $('an-top');
  body.innerHTML = '';

  $('an-top-empty').hidden = analytics.top.length !== 0;

  for (const player of analytics.top) {
    const tr = document.createElement('tr');
    const first = document.createElement('td');
    const box = document.createElement('div');
    box.className = 'c-place';

    const name = document.createElement('span');
    name.className = 'who-name';

    // ⛔ textContent, never innerHTML. This is a name a player typed into their
    // own client, and the CSP is the second net rather than the first.
    name.textContent = player.name || '(no name)';
    box.appendChild(name);

    if (player.country) {
      const code = document.createElement('span');
      code.className = 'c-code';
      code.textContent = player.country === 'US' && player.region
        ? `US-${player.region}`
        : player.country;
      box.appendChild(code);
    }

    // Only in a fleet sum. The same person on two servers is two rows here,
    // and the server is what tells a reader why.
    if (player.host) {
      const host = document.createElement('span');
      host.className = 'c-code';
      host.textContent = player.host;
      box.appendChild(host);
    }

    if (player.live) box.appendChild(tag('on now', 'good'));

    first.appendChild(box);
    tr.appendChild(first);

    tr.appendChild(cell(player.sessions.toLocaleString(), 'c-num'));
    tr.appendChild(cell(duration(player.playtimeSeconds), 'c-num'));

    const last = document.createElement('td');
    last.className = 'c-last';
    last.textContent = player.live ? 'now' : (player.lastSeen || '').slice(0, 10);
    tr.appendChild(last);

    body.appendChild(tr);
  }
}

/**
 * The caveats, on the page rather than only in the documentation.
 *
 * ⚠️ "Unique players" is unique ADDRESSES. It is wrong in both directions — a
 * household is one player and a phone is a new one every day — and an operator
 * reading a number off this sheet has to be told that where they read it, not
 * in a file they have not opened.
 */
function foot() {
  const t = analytics.totals;
  const parts = [
    'A "player" here is an address, hashed with a per-file salt — the protocol has no '
    + 'account, so everybody behind one address counts once and one person on a mobile '
    + 'network counts more than once.',
  ];

  if (analytics.fleet) {
    parts.push(
      `These are ${analytics.fleet.hosts.length} servers' figures added together. Each salts its `
      + 'hashes differently, so somebody who played on two of them is two players here, and a '
      + "day's peak is the busiest single server's.");
  }

  if (analytics.geoip) {
    parts.push(
      `Geography from ${analytics.geoip.type}, built ${analytics.geoip.built}`
      + `${analytics.geoip.regions ? ' (with subdivisions)' : ' (countries only)'}.`);
  } else {
    parts.push('No geo database is loaded.');
  }

  if (t.trimmed > 0) {
    parts.push(
      `${t.trimmed.toLocaleString()} of the oldest player records have been dropped to stay `
      + `under the ${t.cap.toLocaleString()} kept, so the all-time total counts a returning `
      + 'player from before that twice.');
  } else {
    parts.push(`${t.tracked.toLocaleString()} of ${t.cap.toLocaleString()} player records kept.`);
  }

  $('an-foot').textContent = parts.join(' ');
}

$('an-metric').addEventListener('click', (event) => {
  const button = event.target.closest('.seg-btn');
  if (!button) return;

  // ⚠️ Scoped to this group. Three segmented controls on this page share the
  // class name and each owns its own selection — see the perf card's note.
  for (const other of $('an-metric').querySelectorAll('.seg-btn')) other.classList.remove('is-on');

  button.classList.add('is-on');
  anMetric = button.dataset.metric;
  drawDays();
});

$('an-range').addEventListener('click', (event) => {
  const button = event.target.closest('.seg-btn');
  if (!button) return;

  for (const other of $('an-range').querySelectorAll('.seg-btn')) other.classList.remove('is-on');

  button.classList.add('is-on');
  anWindow = Number(button.dataset.days);
  drawDays();
});

$('an-view').addEventListener('click', (event) => {
  const button = event.target.closest('.seg-btn');
  if (!button) return;

  for (const other of $('an-view').querySelectorAll('.seg-btn')) other.classList.remove('is-on');

  button.classList.add('is-on');
  anView = button.dataset.view;
  $('an-map-tip').hidden = true;
  drawGeoMap();
});

$('an-days').addEventListener('mousemove', onDaysHover);
$('an-days').addEventListener('mouseleave', () => { $('an-days-tip').hidden = true; });
$('an-map').addEventListener('mousemove', onMapHover);
$('an-map').addEventListener('mouseleave', () => { $('an-map-tip').hidden = true; });
