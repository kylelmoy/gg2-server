/*
 * gg2-server web admin.
 *
 * No framework and no build step — see WebAssets.cs for why. The page is small
 * enough that a hundred lines of DOM plumbing beats a dependency that has to be
 * vendored, pinned and served from the same assembly.
 *
 * Two rules hold the whole file together:
 *
 *   1. State comes in over ONE server-sent-events stream and is applied by
 *      render(). Nothing else writes to the DOM from a timer, so the panel can
 *      never show two halves of two different ticks.
 *
 *   2. Everything that CHANGES the server goes through send(), which posts one
 *      command to /api/command and shows what came back. The command names and
 *      their arguments are the control port's, unchanged — the browser is not a
 *      second control surface, it is a client of the first one.
 */
'use strict';


/* ---------- transport ----------------------------------------------------- */

/*
 * Which server this page is showing. On a fleet hub, `?host=<member>` points
 * every call about the SERVER at the hub's pass-through, /h/<member>/api/…, and
 * the member answers it. Calls about the BROWSER — session, sign-in, sign-out —
 * always go to the hub, because the hub is what the browser is signed in to.
 * Switching member is a page load rather than an in-place swap: every graph,
 * log and chat buffer here is one server's, and a reload is the one reset that
 * cannot miss one.
 */
const member = new URLSearchParams(location.search).get('host') || '';
const apiBase = member ? `/h/${encodeURIComponent(member)}` : '';

/** A server-scoped API path, on this server or through the hub to a member. */
function api(path) { return apiBase + path; }

let csrf = '';
let stream = null;
let retry = 1000;
let state = null;
let selected = -1;
let logSeen = -1;
let chatSeen = -1;

/** One command to the server. Returns the parsed reply; never throws. */
async function send(body, quiet) {
  try {
    const reply = await fetch(api('/api/command'), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
      body: JSON.stringify(body),
    });

    if (reply.status === 401) { showLogin(); return { ok: false, error: 'signed out' }; }

    const json = await reply.json();

    // A refusal is the interesting case and the server always explains itself,
    // so it is shown whether or not the caller asked to be quiet.
    if (!json.ok) toast(json.error || 'refused', 'bad');
    else if (!quiet) toast(describe(body), 'good');

    return json;
  } catch (e) {
    toast('the server did not answer', 'bad');
    return { ok: false, error: String(e) };
  }
}

function describe(body) {
  switch (body.cmd) {
    case 'bots': return `Bot fill set to ${body.fill}`;
    case 'map': return `Changing map to ${body.name}`;
    case 'kick': return 'Kicked';
    case 'ban': return 'Banned';
    case 'offline': return body.enabled ? 'Server offline' : 'Server back online';
    case 'end-round': return 'Round ended';
    case 'restart-round': return 'Restarting the round';
    case 'autobalance': return `Autobalance ${body.enabled ? 'on' : 'off'}`;
    case 'player-limit': return `Player limit ${body.value}`;
    case 'password': return body.value ? 'Join password set' : 'Join password removed';
    case 'rotation': return 'Rotation updated';
    case 'team': return `Moved to ${body.team}`;
    case 'bot-limit': return `Bot limit ${body.max}`;
    case 'bot-brain': return `Brain ${body.mode}; bots replaced`;
    case 'teleport': return 'Teleported';
    case 'config-set': return 'Settings saved';
    case 'restart': return 'Restarting';
    default: return 'Done';
  }
}

/**
 * Opens the event stream, and keeps it open.
 *
 * EventSource reconnects itself, but only while the server is answering; a
 * server that has stopped needs the backoff below, and a session that has
 * expired needs the page to go back to the login card rather than retry for
 * ever.
 */
function connect() {
  if (stream) stream.close();

  live('connecting');
  stream = new EventSource(api('/api/events'));

  stream.addEventListener('open', () => { retry = 1000; live('live'); });

  stream.addEventListener('state', (event) => {
    live('live');
    render(JSON.parse(event.data));
  });

  stream.addEventListener('log', (event) => appendLog(JSON.parse(event.data)));
  stream.addEventListener('chat', (event) => appendChat(JSON.parse(event.data)));
  stream.addEventListener('perf', (event) => appendPerf(JSON.parse(event.data)));

  stream.addEventListener('error', () => {
    live('lost');
    stream.close();

    // ⚠️ Check the session before reconnecting. Without this a panel whose
    // session expired hammers /api/events for ever and never tells the operator
    // why nothing is updating.
    fetch('/api/session')
      .then((r) => r.json())
      .then((s) => { if (!s.authenticated) showLogin(); else schedule(); })
      .catch(schedule);
  });
}

function schedule() {
  setTimeout(connect, retry);
  retry = Math.min(retry * 2, 15000);
}

function live(kind) {
  $('live').dataset.state = kind;
  $('live-text').textContent = kind === 'live' ? 'live' : kind === 'lost' ? 'reconnecting' : 'connecting';
}

/* ---------- rendering ------------------------------------------------------ */

let lastRed = null;
let lastBlue = null;
let mapImage = null;
let mapImageName = '';

function render(next) {
  // ⚠️ The panel must never white-screen on an unexpected payload. The server
  // will not send a state event before it has a snapshot, and this is the second
  // net for the same failure — a thrown exception here stops every later event
  // from being applied and leaves a page that looks merely stale.
  if (!next || !next.server) return;

  const first = state === null;
  const mapChanged = !state || state.map !== next.map;
  state = next;

  $('server-name').textContent = next.server.name;
  $('server-port').textContent = `:${next.server.port}`;
  chip('chip-map', next.map || '—');
  chip('chip-mode', spaced(next.match.mode));

  banner(next);
  scores(next);
  tiles(next);
  kpis();
  brains(next);
  controls(next);
  rotation(next);
  roster(next);
  objective(next);

  if (mapChanged) loadMapImage(next.map);
  drawMap();

  if (first) { loadMaps(); loadSystem(); }
}

/**
 * The pending map change.
 *
 * ⚠️ A map change is a ten-second countdown rather than an event — the server
 * broadcasts MAP_END and loads the map afterwards — so the panel counts it down
 * instead of showing a toast and then appearing to have done nothing.
 */
function banner(s) {
  const node = $('banner');

  if (!s.match.nextMap) { node.hidden = true; return; }

  node.hidden = false;
  node.replaceChildren('Changing map to ');

  const name = document.createElement('b');
  name.textContent = s.match.nextMap;
  node.append(name, ` in ${Math.max(0, Math.ceil(s.match.changeIn / 30))}s`);
}

function chip(id, text) { $(id).querySelector('b').textContent = text; }

function spaced(text) { return String(text).replace(/([a-z])([A-Z])/g, '$1 $2'); }

function scores(s) {
  const red = $('score-red');
  const blue = $('score-blue');

  red.textContent = s.match.redCaps;
  blue.textContent = s.match.blueCaps;

  if (lastRed !== null && s.match.redCaps > lastRed) bump(red);
  if (lastBlue !== null && s.match.blueCaps > lastBlue) bump(blue);

  lastRed = s.match.redCaps;
  lastBlue = s.match.blueCaps;

  const clock = $('clock');
  clock.textContent = mmss(s.round.timer);
  clock.classList.toggle('low', s.round.timer > 0 && s.round.timer < 30 * 60);

  $('clock-sub').textContent = s.round.decided
    ? (s.round.winner === 'Spectator' ? 'stalemate' : `${s.round.winner.toUpperCase()} wins`)
    : `first to ${s.match.capLimit}`;

  $('setup').hidden = s.match.setupTimer <= 0;
  $('setup-time').textContent = mmss(s.match.setupTimer);
}

function bump(node) {
  node.classList.remove('bump');
  void node.offsetWidth;
  node.classList.add('bump');
}

function tiles(s) {
  const budget = s.server.tickBudgetMs || 33.3;
  const share = Math.min(100, (s.server.tickMs / budget) * 100);

  $('tile-tick').textContent = s.server.tickMs.toFixed(2);

  const bar = $('tile-tick-bar');
  bar.style.width = `${share}%`;
  bar.className = share > 80 ? 'high' : share > 45 ? 'mid' : '';
  $('tile-tick-note').textContent = `${share.toFixed(1)}% of ${budget.toFixed(1)}ms`;

  const bots = s.players.filter((p) => p.bot).length;
  const humans = s.players.filter((p) => p.connected).length;

  $('tile-players').textContent = humans + bots;
  $('tile-slots').textContent = `/ ${s.server.slots}`;
  $('tile-bots').textContent = `${humans} human${humans === 1 ? '' : 's'}, ${bots} bot${bots === 1 ? '' : 's'}`;

  $('tile-uptime').textContent = duration(s.server.uptimeSeconds);
  $('tile-state').textContent = s.server.offline ? 'offline'
    : s.frozen ? 'frozen' : s.server.idle ? 'idle (2 Hz, nobody on)' : 'running';
  $('tile-state').classList.toggle('warn', s.frozen || s.server.offline);

  $('tile-joins').textContent = s.server.joins.toLocaleString();

  // Where the chat box's lines go, which a player sees very differently: chat
  // through a plugin, or an on-screen notice that replaces the last one.
  $('chat-hint').textContent = s.server.chatPlugin
    ? `Sent as ${s.players[0] ? s.players[0].name : 'the server'} through ${s.server.chatPlugin}.`
    : 'No chat plugin is loaded, so this shows as an on-screen notice to every player.';

  const fill = $('bots');
  if (document.activeElement !== fill) fill.value = s.server.botFill;

  // ⚠️ Below --bots-min-humans the population manager's target is zero rather
  // than the fill, so setting a bot count on an empty server can look like a
  // button that does nothing. Say which it is.
  //
  // ⚠️ And a SECOND reason, which an operator cannot otherwise see at all: a
  // player typed /bots off and the population is held at zero until the window
  // ends. It is checked first because it overrides the fill outright, and it is
  // the one with an action attached — "bots-suspend" with minutes 0 ends it, and
  // so does saving a bot count.
  const suspended = s.server.botSuspendSeconds > 0;
  const blocked = s.server.botFill > 0 && humans < s.server.botMinHumans;
  const warn = $('bots-warn');

  warn.hidden = !suspended && !blocked;
  warn.textContent = suspended
    ? `A player turned the bots off with /bots for another `
      + `${Math.ceil(s.server.botSuspendSeconds / 60)} minute`
      + `${Math.ceil(s.server.botSuspendSeconds / 60) === 1 ? '' : 's'}. `
      + 'Setting a bot count here ends it.'
    : blocked
    ? `No bots will spawn until ${s.server.botMinHumans} human`
      + `${s.server.botMinHumans === 1 ? '' : 's'} are connected `
      + '(--bots-min-humans). There are ' + humans + '.'
    : '';
}

/**
 * Which brain the bots run, and whether it is actually driving.
 *
 * ⚠️ Three different questions, and the card answers all three because no one of
 * them is enough. `bots.brain` is what was CONFIGURED. A tier's class list is
 * which bots it is allowed to drive. The SHARE beside it is ticks a controller
 * drove over ticks it was polled on — and that is the only one that says the
 * controller is being consulted at all, because a tier can be loaded, named here
 * and never admitted by the arbiter.
 *
 * ⚠️ A controller only holds the wheel inside the engagement window, so "driving
 * right now" is zero most of the time on a perfectly healthy server. It is shown
 * as a highlight on the count rather than as a number, so nobody reads a quiet
 * moment as a broken brain.
 */
function brains(s) {
  const all = s.players.filter((p) => p.bot);

  $('brain-name').textContent = s.bots.brain;

  const rows = document.createDocumentFragment();

  for (const tier of s.bots.tiers) rows.appendChild(tierRow(tier, all, true));

  // The bots no tier drives: a class every controller refuses, or a server whose
  // mode is `scripted` and has no tiers at all. Shown only when there are some —
  // "0 scripted" on a server whose tiers cover every class is noise, and on a
  // scripted server this is the only row there is.
  if (all.some((p) => p.brain === 'scripted')) {
    rows.appendChild(tierRow({ name: 'scripted' }, all, false));
  }

  $('brain-tiers').replaceChildren(rows);

  const note = $('brain-note');
  const parts = [];

  if (s.bots.arbiterMicroseconds !== undefined) {
    // ⚠️ The budget IN FORCE, not the one configured: MicroBudgetController moves
    // it while the server runs, so a number here that is not the one in the
    // config file is the calibration working rather than a bug.
    parts.push(`Arbiter ${s.bots.arbiterMicroseconds.toLocaleString()} µs a tick, `
      + `${s.bots.arbiterSpentMicroseconds.toLocaleString()} spent on the last one.`);
  }

  if (all.length === 0 && s.bots.tiers.length > 0) {
    parts.push('No bots are on the server, so nothing is driving.');
  } else if (s.bots.engage === 'humans') {
    // ⛔ The gate every tier is behind, and the reason a correctly configured
    // brain can drive far less than an operator expects — a live server hands a
    // controller the wheel for a HUMAN enemy only.
    //
    // ⚠️ Stated as the CONFIGURATION, never as a prediction that they will not
    // drive. The share beside each tier above is the measurement and it is the
    // one to believe — the gate rests on PlayerState.IsBot surviving a respawn,
    // which is a simulation property this page cannot check.
    parts.push('Controllers take the wheel against a human enemy only '
      + '(--bot-engages-bots opens them to bot fights).');
  }

  note.textContent = parts.join(' ');
  note.hidden = parts.length === 0;
}

/**
 * One brain, with what it is allowed to drive and what it has actually done.
 *
 * `share` is off for the scripted row: those bots' counters, when they have any,
 * belong to whatever tier drove them under a class they have since changed away
 * from, and a percentage on a row that by definition never drives would be read
 * as one.
 */
function tierRow(tier, all, share) {
  const mine = all.filter((p) => p.brain === tier.name);
  const row = document.createElement('div');
  row.className = 'tier';

  const name = document.createElement('span');
  name.className = 'tier-name';
  name.textContent = tier.name;
  row.appendChild(name);

  const drives = document.createElement('span');
  drives.className = 'tier-classes';
  // Absent when the server has no bots on it: the class set is read off a live
  // controller rather than re-derived, so with nothing built there is nothing to
  // ask. See ControlCommands.AppendBots.
  drives.textContent = tier.classes ? tier.classes.join(' · ') : '';
  // The row truncates: nine class names is a paragraph, and the row's job is the
  // count on its right.
  if (tier.classes) drives.title = `Drives ${tier.classes.join(', ')}.`;
  row.appendChild(drives);

  const live = document.createElement('span');
  live.className = 'tier-live';

  const driving = mine.filter((p) => p.driving).length;
  const micro = mine.reduce((sum, p) => sum + (p.microTicks || 0), 0);
  const polls = micro + mine.reduce((sum, p) => sum + (p.scriptedTicks || 0), 0);

  live.textContent = mine.length === 0
    ? 'no bots'
    : `${mine.length} bot${mine.length === 1 ? '' : 's'}`
      + (share && polls > 0 ? ` · ${((micro / polls) * 100).toFixed(1)}%` : '');

  live.classList.toggle('is-driving', driving > 0);

  live.title = share
    ? `${driving} driving right now. ${micro.toLocaleString()} of `
      + `${polls.toLocaleString()} ticks driven since each bot spawned.`
    : 'The macro layer alone — every controller refuses these classes.';

  row.appendChild(live);
  return row;
}

/**
 * The roster table.
 *
 * ⚠️ Rebuilt wholesale five times a second, and that is fine: it is at most 48
 * rows and the alternative — diffing by slot — gets the roster's own compaction
 * wrong the moment somebody leaves, which is exactly the bug the server side of
 * this project has a rule about.
 */
function roster(s) {
  const filter = $('filter').value.trim().toLowerCase();
  const body = $('roster');
  const rows = document.createDocumentFragment();
  let shown = 0;

  for (const player of s.players) {
    // Slot 0 is the reserved server player: it is never on a team, never alive,
    // and showing it as a row of dashes only ever confuses.
    if (player.slot === 0) continue;

    if (filter && !player.name.toLowerCase().includes(filter)) continue;

    shown++;
    rows.appendChild(row(player));
  }

  body.replaceChildren(rows);
  $('roster-empty').hidden = shown > 0;
  $('roster-empty').textContent = filter ? 'No player matches that filter.' : 'Nobody is connected.';
}

function row(player) {
  const tr = document.createElement('tr');
  const team = player.team === 'Red' ? 'red' : player.team === 'Blue' ? 'blue' : 'spec';

  tr.className = (player.alive ? '' : 'dead ') + (player.slot === selected ? 'is-selected' : '');
  tr.dataset.slot = player.slot;

  tr.appendChild(cell(String(player.slot), 'c-slot'));

  const who = document.createElement('td');
  who.className = 'c-name';
  const wrap = document.createElement('div');
  wrap.className = 'who';

  const avatar = document.createElement('span');
  avatar.className = `avatar ${team}`;
  // ⚠️ The first LETTER OR DIGIT, not the first character: the bot names are
  // "[BOT] 3" and a straight slice(0, 1) puts a bracket in every avatar.
  avatar.textContent = ((player.name || '').match(/[a-z0-9]/i) || ['?'])[0].toUpperCase();
  wrap.appendChild(avatar);

  const name = document.createElement('span');
  name.className = 'who-name';
  // ⚠️ textContent, never innerHTML. A player's name is whatever they typed into
  // their own client, and it reaches this page unfiltered.
  name.textContent = player.name;

  if (player.bot) name.appendChild(tag('BOT', 'bot'));

  // ⚠️ The brain that WILL drive this slot, lit while it actually is. A
  // controller holds the wheel only inside the engagement window, so an unlit
  // badge is the ordinary case and not a brain that has stopped working — the
  // Bots card carries the share that answers that.
  if (player.bot && player.brain && player.brain !== 'scripted') {
    const badge = tag(player.brain, 'brain' + (player.driving ? ' is-driving' : ''));
    const polls = (player.microTicks || 0) + (player.scriptedTicks || 0);

    badge.title = polls > 0
      ? `${player.brain} has driven ${((player.microTicks / polls) * 100).toFixed(1)}% of `
        + `this bot's ticks (${player.microTicks.toLocaleString()} of ${polls.toLocaleString()})`
      : player.brain;

    name.appendChild(badge);
  }
  if (player.intel) name.appendChild(tag('INTEL', 'intel'));

  wrap.appendChild(name);
  who.appendChild(wrap);
  if (player.address) who.title = player.address;
  tr.appendChild(who);

  tr.appendChild(cell(player.alive || player.team !== 'Spectator' ? player.class : '—', 'c-class'));
  tr.appendChild(health(player));
  tr.appendChild(cell(String(player.kills), 'c-num'));
  tr.appendChild(cell(String(player.deaths), 'c-num'));
  tr.appendChild(cell(String(player.caps), 'c-num'));
  tr.appendChild(cell(String(player.points), 'c-num'));

  const actions = document.createElement('td');
  actions.className = 'c-act';

  const more = document.createElement('button');
  more.className = 'mini mini-plain more' + (menuFor && menuFor.slot === player.slot ? ' is-open' : '');
  more.type = 'button';
  more.textContent = '⋯';
  more.title = `Actions for ${player.name}`;
  more.setAttribute('aria-haspopup', 'menu');
  more.dataset.menu = player.slot;
  actions.appendChild(more);

  tr.appendChild(actions);
  return tr;
}


function health(player) {
  const td = document.createElement('td');
  td.className = 'c-hp';

  if (!player.alive) {
    td.textContent = '—';
    return td;
  }

  const hp = Math.max(0, Math.round(player.hp));
  const wrap = document.createElement('div');
  wrap.className = 'hp';

  const bar = document.createElement('div');
  bar.className = 'hp-bar';
  const fill = document.createElement('span');

  // The scale is the class's own maximum, which the state does not carry —
  // 200 is the largest in the game (heavy), so the bar reads as "how much of the
  // biggest health pool in GG2", which is the honest thing to show without
  // inventing a per-class table the server has not sent.
  fill.style.width = `${Math.min(100, (hp / 200) * 100)}%`;
  fill.className = hp < 40 ? 'low' : hp < 90 ? 'hurt' : '';
  bar.appendChild(fill);

  const number = document.createElement('span');
  number.className = 'hp-num';
  number.textContent = hp;

  wrap.append(bar, number);
  td.appendChild(wrap);
  return td;
}

function objective(s) {
  const foot = $('objective');
  const parts = [];

  for (const [name, intel] of [['RED', s.redIntel], ['BLU', s.blueIntel]]) {
    if (!intel.present) continue;

    const carrier = intel.carrier >= 0 ? s.players[intel.carrier] : null;
    const home = intel.x === intel.homeX && intel.y === intel.homeY;
    const status = carrier ? `taken by ${carrier.name}` : home ? 'at base' : 'dropped';

    parts.push(pill(`${name} intel`, status, name === 'RED' ? 'red' : 'blue'));
  }

  s.points.forEach((point, i) => {
    const owner = point.owner === 'None' ? 'neutral' : point.owner.toUpperCase();
    const node = pill(`point ${i + 1}`, owner, point.owner === 'Red' ? 'red' : point.owner === 'Blue' ? 'blue' : '');

    if (point.capping !== 'None') {
      const bar = document.createElement('span');
      bar.className = 'pbar';
      const fill = document.createElement('span');
      fill.style.width = `${Math.min(100, point.progress * 100)}%`;
      fill.style.background = point.capping === 'Red' ? 'var(--team-red)' : 'var(--team-blue)';
      bar.appendChild(fill);
      node.appendChild(bar);
    }

    parts.push(node);
  });

  foot.replaceChildren(...parts);
}

function pill(key, value, kind) {
  const span = document.createElement('span');
  span.className = `pill ${kind || ''}`;
  span.append(key + ' ');
  const b = document.createElement('b');
  b.textContent = value;
  span.appendChild(b);
  return span;
}

/* ---------- the live map --------------------------------------------------- */

function loadMapImage(name) {
  if (!name || name === mapImageName) return;

  mapImageName = name;
  mapImage = null;

  const image = new Image();
  image.onload = () => { if (mapImageName === name) { mapImage = image; drawMap(); } };
  image.onerror = () => { $('map-empty').textContent = `no image for ${name}`; };
  image.src = api(`/api/map.png?name=${encodeURIComponent(name)}`);
}

/**
 * Draws the map and everyone on it.
 *
 * ⚠️ World pixels are art pixels × 6 — GameConstants.CellSize, and the same
 * factor LevelMap.Width applies — so the map PNG scaled to the world's width IS
 * the coordinate system, with no fudge factor. If a dot is ever off by a
 * constant, that is the number to look at.
 */
function drawMap() {
  const canvas = $('map');
  const world = state && state.match;

  if (!world || !world.width || !world.height) return;

  const scale = Math.min(2, 1200 / world.width);
  const pixels = Math.min(2, window.devicePixelRatio || 1);

  canvas.width = Math.round(world.width * scale * pixels);
  canvas.height = Math.round(world.height * scale * pixels);
  canvas.style.aspectRatio = `${world.width} / ${world.height}`;

  // ⚠️ Bound the card by capping the WIDTH at whatever gives the wanted height,
  // never by capping the height: a canvas stretches its bitmap to fill the box
  // CSS gives it, so a max-height would distort the map and put every dot in the
  // wrong place. cp_dirtbowl is three areas stacked and is the case that needs
  // it.
  canvas.style.maxWidth = `min(100%, ${(62 * world.width / world.height).toFixed(2)}vh)`;

  const g = canvas.getContext('2d');
  g.setTransform(scale * pixels, 0, 0, scale * pixels, 0, 0);
  g.clearRect(0, 0, world.width, world.height);

  // ⚠️ Markers are sized in CSS pixels and converted back into world units, not
  // drawn at their world size. ctf_truefort is 5346 world pixels wide, so a
  // 9-unit dot on a canvas scaled to fit is ONE screen pixel — which is what
  // this looked like before, and reads as "the panel cannot see the players".
  const unit = 1 / scale;

  if (mapImage) {
    g.imageSmoothingEnabled = false;
    g.globalAlpha = 0.85;
    g.drawImage(mapImage, 0, 0, world.width, world.height);
    g.globalAlpha = 1;
    $('map-empty').hidden = true;
  } else {
    $('map-empty').hidden = false;
  }

  for (const [intel, colour] of [
    [state.redIntel, token('--team-red', '#e2564f')],
    [state.blueIntel, token('--team-blue', '#4f92dc')],
  ]) {
    if (!intel.present || intel.carrier >= 0) continue;
    const home = intel.x === intel.homeX && intel.y === intel.homeY;
    briefcase(g, intel.x, intel.y, colour, home, unit);
  }

  for (const player of state.players) {
    if (!player.alive || player.x === undefined) continue;
    dot(g, player, unit);
  }
}

function briefcase(g, x, y, colour, home, unit) {
  const w = 6 * unit;
  const h = 5 * unit;

  g.save();
  g.translate(x, y);
  g.fillStyle = colour;
  g.globalAlpha = home ? 0.55 : 1;
  g.fillRect(-w, -h, w * 2, h * 2);
  g.strokeStyle = EDGE();
  g.lineWidth = 1.5 * unit;
  g.strokeRect(-w, -h, w * 2, h * 2);
  g.restore();
}

function dot(g, player, unit) {
  const colour = player.team === 'Red' ? token('--team-red', '#e2564f')
    : player.team === 'Blue' ? token('--team-blue', '#4f92dc')
    : INK_3();

  g.save();
  g.translate(player.x, player.y);

  if (player.slot === selected) {
    g.beginPath();
    g.arc(0, 0, 11 * unit, 0, Math.PI * 2);
    g.strokeStyle = token('--accent', '#c49ff9');
    g.lineWidth = 2 * unit;
    g.stroke();
  }

  g.beginPath();
  g.arc(0, 0, 5.5 * unit, 0, Math.PI * 2);
  g.fillStyle = colour;
  g.fill();
  g.strokeStyle = EDGE();
  g.lineWidth = 1.75 * unit;
  g.stroke();

  if (player.bot) {
    g.beginPath();
    g.arc(0, 0, 2 * unit, 0, Math.PI * 2);
    g.fillStyle = EDGE();
    g.fill();
  }

  if (player.intel) {
    g.fillStyle = token('--warning', '#f7b750');
    g.fillRect(4 * unit, -10 * unit, 6 * unit, 5 * unit);
  }

  g.restore();
}

/** Canvas pixel → world pixel, for the tooltip and for click-to-teleport. */
function toWorld(event) {
  const canvas = $('map');
  const box = canvas.getBoundingClientRect();

  return {
    x: ((event.clientX - box.left) / box.width) * state.match.width,
    y: ((event.clientY - box.top) / box.height) * state.match.height,
  };
}

/* ---------- performance charts ---------------------------------------------
 *
 * Six small multiples on one time axis, fed by the `perf` event. The reasons
 * behind the shape of this section, in the order somebody would question them:
 *
 *   - **One axis per chart, never two.** Working set and managed heap share a
 *     chart because they share a unit; tick time and CPU do not and never will.
 *     A second y-scale invents a correlation that is not in the data.
 *   - **Small multiples rather than one crowded plot**, because the question is
 *     "which of these moved at the same instant" and a shared time axis with a
 *     shared crosshair answers it better than six series in one frame.
 *   - **The colors are the data-viz palette's dark steps, validated against this
 *     card's surface** (aqua/orange/violet: worst CVD ΔE 9.4, worst
 *     normal-vision ΔE 24.6, all ≥ 3:1). ⚠️ Deliberately NOT the team colors —
 *     red and blue mean RED and BLU everywhere else on this page.
 *   - **Late ticks wear the status warning color**, because that series means
 *     "something is wrong" rather than "series four". Its title carries the
 *     label, so the color is never alone.
 */


/**
 * One entry per chart: what it plots, in what unit, and how it is scaled.
 *
 * `floor` stops an idle server's noise being drawn as mountains — without it a
 * tick graph that never leaves 0.1ms fills the frame and looks alarming.
 */
const CHARTS = [
  {
    key: 'tick',
    title: 'Tick time',
    unit: 'ms',
    floor: 5,
    digits: 2,
    rule: (s) => s.tickBudget,
    ruleLabel: (ms) => `${ms.toFixed(1)}ms budget`,
    series: [
      { field: 'tickMean', label: 'mean', color: SERIES.aqua, fill: true },
      { field: 'tickPeak', label: 'worst', color: SERIES.orange },
    ],
  },
  {
    key: 'cpu',
    title: 'CPU',
    unit: '% of one core, 5s',
    floor: 10,
    digits: 1,
    series: [{ field: 'cpu', label: 'cpu', color: SERIES.aqua, fill: true }],
  },
  {
    key: 'mem',
    title: 'Memory',
    unit: 'MB',
    floor: 64,
    digits: 0,
    series: [
      { field: 'rss', label: 'resident', color: SERIES.aqua, fill: true },
      { field: 'heap', label: 'managed heap', color: SERIES.violet },
    ],
  },
  {
    key: 'late',
    title: 'Late ticks',
    unit: 'per second',
    floor: 2,
    digits: 0,
    series: [{ field: 'late', label: 'late', color: SERIES.warning, fill: true }],
    // (SERIES.warning is a function; resolve() below calls it.)
  },
  {
    key: 'net',
    title: 'Sent',
    unit: 'KB/s',
    floor: 8,
    digits: 1,
    series: [{ field: 'bytes', label: 'out', color: SERIES.aqua, fill: true, scale: 1 / 1024 }],
  },
  {
    key: 'load',
    title: 'Load',
    unit: 'count',
    floor: 4,
    digits: 0,
    series: [
      { field: 'characters', label: 'alive', color: SERIES.aqua, fill: true },
      { field: 'bots', label: 'bots', color: SERIES.orange },
      { field: 'humans', label: 'humans', color: SERIES.violet },
    ],
  },
];


let perf = [];
let perfSeen = -1;
let range = 300;
let hover = -1;

/** Builds the perf card once, then only redraws. */
function buildCharts() {
  const host = $('charts');

  if (host.childElementCount > 0) return;

  for (const chart of CHARTS) {
    const figure = document.createElement('figure');
    figure.className = 'chart';

    const caption = document.createElement('figcaption');
    const title = document.createElement('span');
    title.className = 'chart-title';
    title.textContent = `${chart.title} · ${chart.unit}`;

    const value = document.createElement('span');
    value.className = 'chart-value';
    value.id = `v-${chart.key}`;
    value.textContent = '—';

    caption.append(title, value);
    figure.appendChild(caption);

    // ⚠️ A legend only for two or more series. One series has one color, and the
    // caption already names it; a box with a single swatch restates the title.
    if (chart.series.length > 1) {
      const legend = document.createElement('div');
      legend.className = 'chart-legend';

      for (const series of chart.series) {
        const item = document.createElement('span');
        const key = document.createElement('i');
        key.style.background = typeof series.color === 'function' ? series.color() : series.color;
        item.append(key, series.label);
        legend.appendChild(item);
      }

      figure.appendChild(legend);
    }

    const canvas = document.createElement('canvas');
    canvas.className = 'chart-canvas';
    canvas.dataset.chart = chart.key;
    canvas.setAttribute('role', 'img');
    canvas.setAttribute('aria-label',
      `${chart.title} in ${chart.unit} over the last five minutes`);

    figure.appendChild(canvas);
    host.appendChild(figure);
  }

  host.addEventListener('mousemove', onChartHover);
  host.addEventListener('mouseleave', () => { hover = -1; drawCharts(); });

  for (const button of $('range-seg').querySelectorAll('.seg-btn')) {
    button.addEventListener('click', () => {
      range = Number(button.dataset.range);

      for (const other of $('range-seg').querySelectorAll('.seg-btn'))
        other.classList.toggle('is-on', other === button);

      $('axis-from').textContent = `${range / 60} minute${range === 60 ? '' : 's'} ago`;
      drawCharts();
    });
  }
}

/**
 * Folds a `perf` event's rows into the history.
 *
 * ⚠️ Keyed on `seq` and never on arrival order: the stream can re-send a sample
 * that landed while it was serialising the last batch, and the server says so
 * rather than taking a lock on the tick thread's writer.
 */
function appendPerf(payload) {
  const index = {};
  payload.fields.forEach((name, i) => { index[name] = i; });

  for (const row of payload.samples) {
    const seq = row[index.seq];

    if (seq <= perfSeen) continue;
    perfSeen = seq;

    const sample = {};
    for (const name of payload.fields) sample[name] = row[index[name]];
    perf.push(sample);
  }

  // The server keeps five minutes; so does this, so a tab left open for a day
  // does not grow without bound.
  if (perf.length > 300) perf = perf.slice(-300);

  drawCharts();
  kpis();
}

function windowed() {
  return perf.slice(-range);
}

function drawCharts() {
  const data = windowed();

  for (const chart of CHARTS)
    drawChart(chart, data);

  const at = $('chart-at');

  if (hover >= 0 && data[hover]) {
    at.hidden = false;
    at.textContent = new Date(data[hover].at).toLocaleTimeString();
  } else {
    at.hidden = true;
  }
}


function drawChart(chart, data) {
  const canvas = document.querySelector(`canvas[data-chart="${chart.key}"]`);
  if (!canvas) return;

  const pixels = Math.min(2, window.devicePixelRatio || 1);
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;

  if (width === 0) return;

  canvas.width = Math.round(width * pixels);
  canvas.height = Math.round(height * pixels);

  const g = canvas.getContext('2d');
  g.setTransform(pixels, 0, 0, pixels, 0, 0);
  g.clearRect(0, 0, width, height);

  const pad = { top: 10, right: 4, bottom: 4, left: 4 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;

  const value = (sample, series) => (sample[series.field] || 0) * (series.scale || 1);

  // A series colour may be a literal or a token lookup deferred until the
  // stylesheet has applied. One place unwraps it.
  const colour = (series) => (typeof series.color === 'function' ? series.color() : series.color);

  let peak = chart.floor;

  for (const sample of data) {
    for (const series of chart.series)
      peak = Math.max(peak, value(sample, series));
  }

  // ⚠️ The budget joins the SCALE only once the data is within a quarter of it,
  // and this is not cosmetic. Forcing 33.3ms into a chart whose real values are
  // 0.1-0.5ms flattens every one of them onto the baseline: the reference line
  // destroys the resolution of the thing it is a reference for. So a healthy
  // server's chart zooms into its own range, and the budget line appears — with
  // its label — exactly when a tick starts getting near it, which is when it is
  // worth looking at.
  const rule = chart.rule && data.length > 0 ? chart.rule(data[data.length - 1]) : 0;

  if (rule > 0 && peak > rule * 0.25) peak = Math.max(peak, rule);

  const top = niceCeil(peak);
  const x = (i) => pad.left + (data.length < 2 ? plotW : (i / (data.length - 1)) * plotW);
  const y = (v) => pad.top + plotH - Math.min(1, v / top) * plotH;

  // Grid: hairline, solid, one step off the surface, and recessive. Two lines —
  // the top and the middle — because the baseline is the plot's own floor.
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
  g.textBaseline = 'alphabetic';
  // ⚠️ The axis label rounds harder than the readout. `niceCeil` only ever
  // returns 1, 2 or 5 × 10ⁿ, so "50.00" was two decimal places of nothing.
  g.fillText(top >= 10 ? String(top) : top.toFixed(chart.digits), pad.left + 1, pad.top - 2);

  if (data.length === 0) {
    g.fillText('waiting for the first sample', pad.left + 1, pad.top + plotH / 2);
    return;
  }

  // The reference line — the tick budget — is solid and in text ink, not a
  // series color and not dashed: it is chrome that happens to be horizontal, and
  // a dashed rule reads as noise.
  if (rule > 0) {
    const line = Math.round(y(rule)) + 0.5;

    if (line > pad.top && line < pad.top + plotH) {
      g.strokeStyle = token('--segment', '#48454b');
      g.beginPath();
      g.moveTo(pad.left, line);
      g.lineTo(pad.left + plotW, line);
      g.stroke();

      const label = chart.ruleLabel(rule);
      g.fillStyle = INK_3();
      g.fillText(label, pad.left + plotW - g.measureText(label).width - 1, line - 3);
    }
  }

  for (const series of chart.series) {
    if (series.fill) {
      g.beginPath();
      g.moveTo(x(0), pad.top + plotH);

      data.forEach((sample, i) => g.lineTo(x(i), y(value(sample, series))));

      g.lineTo(x(data.length - 1), pad.top + plotH);
      g.closePath();
      g.fillStyle = colour(series);

      // A wash, never a saturated block: the area says "this is the one to read
      // first", the line carries the shape.
      g.globalAlpha = 0.12;
      g.fill();
      g.globalAlpha = 1;
    }

    g.beginPath();
    data.forEach((sample, i) => {
      const at = y(value(sample, series));
      if (i === 0) g.moveTo(x(i), at); else g.lineTo(x(i), at);
    });

    g.strokeStyle = colour(series);
    g.lineWidth = 2;
    g.lineJoin = 'round';
    g.lineCap = 'round';
    g.stroke();

    // The end marker, with a 2px ring in the surface color so it stays legible
    // where two series cross.
    const last = data.length - 1;
    g.beginPath();
    g.arc(x(last), y(value(data[last], series)), 3, 0, Math.PI * 2);
    g.fillStyle = colour(series);
    g.fill();
    g.strokeStyle = SURFACE();
    g.lineWidth = 2;
    g.stroke();
  }

  if (hover >= 0 && hover < data.length) {
    const line = Math.round(x(hover)) + 0.5;

    g.strokeStyle = token('--segment', '#48454b');
    g.lineWidth = 1;
    g.beginPath();
    g.moveTo(line, pad.top);
    g.lineTo(line, pad.top + plotH);
    g.stroke();

    for (const series of chart.series) {
      g.beginPath();
      g.arc(line, y(value(data[hover], series)), 3.5, 0, Math.PI * 2);
      g.fillStyle = colour(series);
      g.fill();
      g.strokeStyle = SURFACE();
      g.lineWidth = 2;
      g.stroke();
    }
  }

  // The headline number: the hovered sample if there is one, otherwise the
  // newest. Direct labels on a 300-point line would be chaos, so the caption is
  // where every value in this chart gets read.
  const shown = data[hover >= 0 && hover < data.length ? hover : data.length - 1];
  const readout = chart.series
    .map((series) => format(value(shown, series), chart.digits))
    .join(' / ');

  $(`v-${chart.key}`).textContent = readout;
}

function format(value, digits) {
  return value >= 1000
    ? Math.round(value).toLocaleString()
    : value.toFixed(digits);
}

function onChartHover(event) {
  const canvas = event.target.closest('.chart-canvas');
  if (!canvas) return;

  const data = windowed();
  if (data.length === 0) return;

  const box = canvas.getBoundingClientRect();
  const fraction = (event.clientX - box.left - 4) / Math.max(1, box.width - 8);

  hover = Math.max(0, Math.min(data.length - 1, Math.round(fraction * (data.length - 1))));
  drawCharts();
}

/** The running totals beside the graphs. */
function kpis() {
  const last = perf[perf.length - 1];
  if (!last || !state) return;

  $('kpi-rate').textContent = last.ticks;
  $('kpi-gc').textContent = last.gc;

  const late = $('kpi-late');
  late.textContent = state.server.lateTicks.toLocaleString();
  late.classList.toggle('warn', state.server.lateTicks > 0);

  $('kpi-worst').textContent = state.server.worstOverrunMs > 0
    ? `+${state.server.worstOverrunMs.toFixed(1)}ms`
    : 'none';

  $('kpi-sent').textContent = bytes(state.server.bytesSent);
}

function bytes(value) {
  if (value < 1024) return `${value} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

/* ---------- player menu ---------------------------------------------------- */

let menuFor = null;

/**
 * Opens the per-player menu under a roster button.
 *
 * ⚠️ The player is remembered by slot AND name, and both are checked again when
 * an item is chosen: a departure renumbers every slot above it, so a menu left
 * open across one would otherwise kick or ban whoever inherited the number.
 */
function openMenu(slot, anchor) {
  const player = state && state.players[slot];
  if (!player) return;

  menuFor = { slot, name: player.name };

  const menu = $('player-menu');
  const items = [];

  const heading = document.createElement('div');
  heading.className = 'menu-head';
  heading.textContent = player.name;
  items.push(heading);

  for (const [team, label] of [['Red', 'Move to Red'], ['Blue', 'Move to Blue'], ['Spectator', 'Move to Spectators']]) {
    if (player.team === team) continue;
    items.push(menuItem(label, `team-${team.toLowerCase()}`, () => send({ cmd: 'team', slot, team: team.toLowerCase() })));
  }

  if (player.connected) {
    items.push(Object.assign(document.createElement('div'), { className: 'menu-sep' }));

    items.push(menuItem('Kick', 'danger', async () => {
      if (await ask('Kick this player?', `${player.name} is disconnected immediately.`)) {
        send({ cmd: 'kick', slot });
      }
    }));

    // ⚠️ The ban is on the ADDRESS, permanently, and the dialog says so: anyone
    // behind the same NAT is banned with them. BanList has the case against
    // reading an address as a person.
    items.push(menuItem('Ban', 'danger', async () => {
      if (await ask(
        'Ban this player?',
        `${player.name} is disconnected and their address `
        + `${player.address ? `(${player.address}) ` : ''}is banned permanently. `
        + 'Anyone else on that address is banned too.')) {
        send({ cmd: 'ban', slot });
      }
    }));
  }

  menu.replaceChildren(...items);
  menu.hidden = false;

  // Below the button, right-aligned to it, and kept on screen.
  const box = anchor.getBoundingClientRect();
  const width = menu.offsetWidth;
  const height = menu.offsetHeight;
  const left = Math.max(8, Math.min(box.right - width, window.innerWidth - width - 8));
  const below = box.bottom + 4;
  const top = below + height > window.innerHeight - 8 ? box.top - height - 4 : below;

  menu.style.left = `${left + window.scrollX}px`;
  menu.style.top = `${Math.max(8, top) + window.scrollY}px`;

  if (state) roster(state);
  menu.querySelector('button')?.focus();
}

function menuItem(label, kind, action) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = `menu-item ${kind}`;
  button.setAttribute('role', 'menuitem');
  button.textContent = label;

  button.addEventListener('click', () => {
    const target = menuFor;
    closeMenu();

    const now = state && target && state.players[target.slot];

    if (!now || now.name !== target.name) {
      toast(`${target ? target.name : 'That player'} left or moved slot; nothing was done`, 'bad');
      return;
    }

    action();
  });

  return button;
}

function closeMenu() {
  if (!menuFor) return;

  menuFor = null;
  $('player-menu').hidden = true;
  if (state) roster(state);
}

document.addEventListener('click', (event) => {
  if (menuFor && !event.target.closest('#player-menu')) closeMenu();
});

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape') closeMenu();
});

window.addEventListener('scroll', closeMenu, { passive: true });

/* ---------- chat ----------------------------------------------------------- */

function appendChat(lines) {
  const box = $('chat');
  const atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 30;

  for (const line of lines) {
    if (line.seq <= chatSeen) continue;
    chatSeen = line.seq;

    const node = document.createElement('div');
    node.className = 'chat-line new';

    const time = document.createElement('span');
    time.className = 'chat-time';
    time.textContent = new Date(line.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    node.appendChild(time);

    if (line.teamOnly) {
      const scope = document.createElement('span');
      scope.className = 'chat-scope';
      scope.textContent = '(team)';
      node.appendChild(scope);
    }

    // ⚠️ textContent throughout: a name and a message are whatever a player typed.
    const name = document.createElement('span');
    name.className = 'chat-name ' + (line.team === 'Red' ? 'red' : line.team === 'Blue' ? 'blue' : '');
    name.textContent = line.name;

    node.append(name, document.createTextNode(': ' + line.text));
    box.appendChild(node);
  }

  while (box.childElementCount > 300) box.removeChild(box.firstChild);
  $('chat-empty').hidden = box.childElementCount > 0;
  if (atEnd) box.scrollTop = box.scrollHeight;
}

/* ---------- log ------------------------------------------------------------ */

function appendLog(lines) {
  const box = $('log');
  const follow = $('log-follow').checked;
  const atEnd = box.scrollTop + box.clientHeight >= box.scrollHeight - 30;

  for (const line of lines) {
    if (line.seq <= logSeen) continue;
    logSeen = line.seq;

    const node = document.createElement('div');
    node.className = 'log-line new' + (/warn|error|fail|late|drop/i.test(line.text) ? ' warn' : '');

    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = new Date(line.at).toLocaleTimeString();

    const text = document.createElement('span');
    text.className = 'log-text';
    text.textContent = line.text;

    node.append(time, text);
    box.appendChild(node);
  }

  while (box.childElementCount > 600) box.removeChild(box.firstChild);
  if (follow && atEnd) box.scrollTop = box.scrollHeight;
}

/* ---------- system info -----------------------------------------------------
 *
 * Static-ish reference: what the server is running ON, as against the graphs
 * above, which are how it is doing. Fetched once at sign-in so the collapsed
 * summary can say something useful, and again whenever the section is opened,
 * because half of it (memory, uptimes, threads, GC counts) moves.
 */

async function loadSystem(force) {
  try {
    const info = await (await fetch(api('/api/system'))).json();
    if (!info.ok) return;

    renderSystem(info);
  } catch {
    if (force) $('sys-body').replaceChildren(hint('the server did not answer'));
  }
}

function hint(text) {
  const p = document.createElement('p');
  p.className = 'hint';
  p.textContent = text;
  return p;
}

/** One `<dt>`/`<dd>` pair. A null value reads "unknown" rather than vanishing. */
function fact(list, name, value, className) {
  const dt = document.createElement('dt');
  dt.textContent = name;

  const dd = document.createElement('dd');

  if (value === null || value === undefined || value === '') {
    dd.textContent = 'unknown';
    dd.className = 'dim';
  } else if (value instanceof Node) {
    dd.appendChild(value);
  } else {
    dd.textContent = value;
  }

  if (className) dd.classList.add(className);

  list.append(dt, dd);
}

function group(title, build, wide) {
  const box = document.createElement('div');
  box.className = 'sys-group' + (wide ? ' wide' : '');

  const heading = document.createElement('h3');
  heading.textContent = title;

  const list = document.createElement('dl');
  build(list);

  box.append(heading, list);
  return box;
}

function badge(text, kind) {
  const span = document.createElement('span');
  span.className = 'sys-badge' + (kind ? ` ${kind}` : '');
  span.textContent = text;
  return span;
}

function hertz(mhz, nominal) {
  if (mhz === null || mhz === undefined) return null;

  const value = mhz >= 1000 ? `${(mhz / 1000).toFixed(2)} GHz` : `${mhz} MHz`;

  // ⚠️ The two platforms mean different things by this and the panel says so:
  // Windows records the clock at boot, Linux reports what core 0 is doing right
  // now — which on a shared VPS moves between requests.
  return `${value} ${nominal ? '(nominal)' : '(now)'}`;
}

function renderSystem(info) {
  const { os, cpu, memory, runtime, paths, disk } = info;

  // The collapsed one-liner. Enough to answer "what am I on" without opening it.
  $('sys-peek').textContent = [
    os.description.split(' ').slice(0, 2).join(' '),
    `${cpu.logicalCores} core${cpu.logicalCores === 1 ? '' : 's'}`,
    memory.totalBytes ? bytes(memory.totalBytes) : null,
    runtime.build === 'Debug' ? 'DEBUG BUILD' : null,
  ].filter(Boolean).join(' · ');

  $('sys-body').replaceChildren(
    group('Operating system', (l) => {
      fact(l, 'os', os.description);
      fact(l, 'architecture', os.architecture);
      fact(l, 'host', os.machine);
      fact(l, 'uptime', duration(os.uptimeSeconds));
    }),

    group('Processor', (l) => {
      fact(l, 'model', cpu.model);
      fact(l, 'clock', hertz(cpu.megahertz, cpu.clockIsNominal));

      // ⚠️ Logical, and what the RUNTIME sees — on a VPS with a CPU quota that
      // is not the host's core count, and it is the one that explains the tick.
      fact(l, 'logical cores', cpu.logicalCores);
    }),

    group('Memory', (l) => {
      fact(l, 'total', memory.totalBytes === null ? null : bytes(memory.totalBytes));
      fact(l, 'available', memory.availableBytes === null ? null : bytes(memory.availableBytes));

      // Says whether the two figures above are the container's or the host's.
      fact(l, 'reported by', memory.source);
      fact(l, 'this process', bytes(memory.processBytes));
      fact(l, 'managed heap', bytes(memory.managedBytes));
    }),

    group('Runtime', (l) => {
      fact(l, 'framework', runtime.framework);
      fact(l, 'identifier', runtime.identifier);
      fact(l, 'build', badge(runtime.build, runtime.build === 'Debug' ? 'debug' : null));
      fact(l, 'gc', `${runtime.serverGc ? 'server' : 'workstation'}, `
        + `${runtime.concurrentGc ? 'concurrent' : 'non-concurrent'}`);
      fact(l, 'collections', runtime.collections.join(' / '));
      fact(l, 'threads', runtime.threads);
      fact(l, 'process', `pid ${runtime.processId}, up ${duration(runtime.processUptimeSeconds)}`);
      fact(l, 'timer', runtime.timerResolution);
    }),

    group('Storage', (l) => {
      if (disk) {
        fact(l, 'mount', disk.mount, 'path');
        fact(l, 'free', `${bytes(disk.freeBytes)} of ${bytes(disk.totalBytes)}`, 'path');
      }

      fact(l, 'maps', paths.maps, 'path');
      fact(l, 'nav', paths.nav, 'path');
      fact(l, 'recordings', paths.recordings, 'path');
    }, true),
  );
}

/* ---------- maps ----------------------------------------------------------- */

let mapNames = [];
let rotationKey = '';

/**
 * The rotation editor, drawn from the state so an edit made anywhere — this
 * tab, another one, the control port — shows up. Redrawn only when the list or
 * the current entry changes, so a click is never lost to a rebuild.
 */
function rotation(s) {
  const key = JSON.stringify([s.server.rotation, s.server.rotationIndex]);
  if (key === rotationKey) return;
  rotationKey = key;

  const list = s.server.rotation;
  $('rotation-empty').hidden = list.length > 0;

  $('rotation-list').replaceChildren(...list.map((name, index) => {
    const item = document.createElement('li');
    item.className = 'rot-item' + (index === s.server.rotationIndex ? ' is-now' : '');

    const label = document.createElement('span');
    label.className = 'rot-name';
    label.textContent = name;
    item.appendChild(label);

    if (index === s.server.rotationIndex) item.appendChild(tag('NOW', 'now'));

    const tools = document.createElement('span');
    tools.className = 'rot-tools';

    for (const [action, text, title, disabled] of [
      ['up', '↑', 'Move up', index === 0],
      ['down', '↓', 'Move down', index === list.length - 1],
      ['remove', '×', 'Remove', false],
    ]) {
      const button = document.createElement('button');
      button.className = 'mini' + (action === 'remove' ? '' : ' mini-plain');
      button.type = 'button';
      button.textContent = text;
      button.title = title;
      button.disabled = disabled;
      button.dataset.rot = action;
      button.dataset.index = index;
      tools.appendChild(button);
    }

    item.appendChild(tools);
    return item;
  }));
}

function sendRotation(list) {
  return send({ cmd: 'rotation', maps: list });
}

/**
 * The Game and Bots cards' live settings. A field the operator is typing in is
 * left alone; everything else follows the server.
 */
function controls(s) {
  const autobalance = $('autobalance');
  autobalance.checked = s.server.autobalance;

  const limit = $('player-limit');
  if (document.activeElement !== limit) limit.value = s.server.slots;

  const botLimit = $('bot-limit');
  if (document.activeElement !== botLimit) botLimit.value = s.server.botLimit;

  const offline = s.server.offline;
  $('offline').textContent = offline ? 'Bring online' : 'Take offline';
  $('offline').classList.toggle('btn-danger', !offline);
  $('offline').classList.toggle('btn-primary', offline);
  $('offline-hint').textContent = offline
    ? 'Offline: connections are refused and no bots run. Settings changed here still apply.'
    : 'Disconnects every player, refuses new connections and removes the bots. This panel stays up; bring it back online from here.';

  $('password-state').textContent = s.server.passworded ? 'set' : 'none';
  $('password-state').classList.toggle('is-set', s.server.passworded);
  $('password-clear').disabled = !s.server.passworded;

  for (const button of document.querySelectorAll('#brain-seg .seg-btn')) {
    const mode = button.dataset.mode;
    button.classList.toggle('is-on', s.bots.mode === mode);

    // A policy brain needs a checkpoint the server was started with.
    button.disabled = (mode === 'policy' || mode === 'tiered') && !s.server.policy;
    button.title = button.disabled ? 'Needs a policy checkpoint (--policy) at startup' : '';
  }
}

async function loadMaps() {
  try {
    const list = await (await fetch(api('/api/maps'))).json();
    if (!list.ok) return;

    $('map-list').replaceChildren(...list.maps.map((name) => {
      const option = document.createElement('option');
      option.value = name;
      return option;
    }));

    mapNames = list.maps;
  } catch { /* the panel is still usable without the map list */ }
}

/* ---------- toasts + confirm ----------------------------------------------- */

function toast(text, kind) {
  const node = document.createElement('div');
  node.className = `toast ${kind || ''}`;
  node.textContent = text;
  $('toasts').appendChild(node);

  setTimeout(() => {
    node.classList.add('out');
    setTimeout(() => node.remove(), 220);
  }, 3400);
}

function ask(title, body) {
  return new Promise((resolve) => {
    const dialog = $('confirm');
    $('confirm-title').textContent = title;
    $('confirm-body').textContent = body;

    const finish = (answer) => {
      dialog.close();
      $('confirm-yes').onclick = null;
      $('confirm-no').onclick = null;
      resolve(answer);
    };

    $('confirm-yes').onclick = () => finish(true);
    $('confirm-no').onclick = () => finish(false);
    dialog.oncancel = () => finish(false);
    dialog.showModal();
  });
}

/* ---------- formatting ------------------------------------------------------ */

function mmss(ticks) {
  if (ticks <= 0) return '--:--';
  const seconds = Math.floor(ticks / 30);
  return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
}


/* ---------- settings -------------------------------------------------------- */

/*
 * The config-file editor.
 *
 * Three things this had to be told, none of them obvious from the endpoint:
 *
 *   1. ⚠️ It edits the FILE, not the running server. Every subsystem reads its
 *      options once, at startup — the listener has bound its port, the ban list
 *      has opened its file, this panel has hashed its own password — so there is
 *      no honest way to apply half of them live. The footer says so, and the
 *      "Save & restart" button is the one that makes a change real.
 *
 *   2. ⚠️ A flag beats the file, so a setting pinned on the command line can be
 *      saved correctly and still do nothing. The server sends `overridden` and
 *      those rows are marked, because "I changed it and nothing happened" with
 *      no explanation is the worst outcome this page can produce.
 *
 *   3. ⛔ A secret is never sent back. Its field is empty with a placeholder
 *      saying whether one is set, and an empty secret field is NOT submitted —
 *      or opening the page and pressing save would erase the admin password.
 */

let settings = [];
let overridden = new Set();
let settingsWritable = false;

/** The edited value of each field, keyed by setting id, or absent if untouched. */
function edits() {
  const changed = {};

  for (const setting of settings) {
    const field = document.getElementById(`set:${setting.id}`);
    if (!field) continue;

    const value = setting.kind === 'boolean' ? (field.checked ? '1' : '0') : field.value;

    // ⛔ An empty secret means "leave it alone", never "set it to empty". The
    // server does not send a secret back, so the field starts empty on every
    // open — and treating that as a value would clear the admin password the
    // first time anybody saved anything at all.
    if (setting.secret && value === '') continue;

    if (value !== (setting.secret ? '' : setting.value)) changed[setting.id] = value;
  }

  return changed;
}

function settingsDirty() {
  const count = Object.keys(edits()).length;
  const blocked = !settingsWritable;
  const waiting = settings.filter((s) => s.pending).length;

  $('settings-save').disabled = blocked || count === 0;
  $('settings-revert').disabled = count === 0;

  // ⚠️ "Save & restart" stays live with nothing edited when a saved change is
  // still waiting for one. The whole point of that state is that the restart is
  // the missing step, and disabling the button that performs it would leave an
  // operator staring at a warning with no way to act on it.
  $('settings-save-restart').disabled = blocked || (count === 0 && waiting === 0);

  const parts = [];

  if (count > 0) parts.push(`${count} change${count === 1 ? '' : 's'} not saved.`);

  if (waiting > 0) {
    parts.push(
      `${waiting} saved setting${waiting === 1 ? '' : 's'} `
      + `${waiting === 1 ? 'is' : 'are'} waiting for a restart.`);
  }

  if (parts.length === 0) parts.push('The file and the running server agree.');
  else parts.push('Settings take effect when the server restarts.');

  $('settings-status').textContent = parts.join(' ');
}

async function openSettings() {
  $('settings').showModal();
  $('settings-body').innerHTML = '<p class="sheet-empty">Loading…</p>';

  let config;

  try {
    const reply = await fetch(api('/api/config'));
    if (reply.status === 401) { $('settings').close(); showLogin(); return; }
    config = await reply.json();
  } catch {
    $('settings-body').innerHTML = '<p class="sheet-empty">The server did not answer.</p>';
    return;
  }

  if (!config.ok) {
    $('settings-body').innerHTML = '';
    $('settings-body').appendChild(Object.assign(
      document.createElement('p'), { className: 'sheet-empty', textContent: config.error }));
    return;
  }

  settings = config.settings;
  overridden = new Set(config.overridden || []);
  settingsWritable = config.writable;

  $('settings-file').textContent = config.writable
    ? config.file
    : 'no config file — started without --config';

  const readonly = $('settings-readonly');
  readonly.hidden = config.writable;

  if (!config.writable) {
    readonly.textContent =
      'This server was started without --config, so there is nowhere to save to. '
      + 'Restart it with --config <path> — the file does not have to exist yet — and '
      + 'these fields become editable.';
  }

  // A file pushed by scripts/deploy.sh --config. Saving here still works — it
  // is the right tool for a change that cannot wait — but the change lives only
  // on this server, and the next push stops for it until it is forced. Said
  // before the operator types, not after they wonder where their edit went.
  const managed = $('settings-managed');

  if (config.managed) {
    const parts = [
      'This file is managed by scripts/deploy.sh --config. A save here applies to '
      + 'this server only, and the next push will stop for it until it is copied into '
      + 'deploy/config or the push is forced.',
    ];
    if (config.editedSincePush) parts.push('It has been edited here since it was last pushed.');
    if (config.pushWaiting) {
      parts.push('A pushed configuration is waiting: the server applies it the next time '
        + 'nobody is connected.');
    }
    managed.textContent = parts.join(' ');
    managed.hidden = false;
  } else {
    managed.hidden = true;
  }

  renderSettings();
}

function renderSettings() {
  const filter = $('settings-filter').value.trim().toLowerCase();
  const body = $('settings-body');
  body.innerHTML = '';

  let section = null;
  let list = null;
  let shown = 0;

  for (const setting of settings) {
    // Matched against the id and the help text, so "password" finds the three
    // that are one and "keepalive" finds the four that mention it.
    if (filter && !`${setting.id} ${setting.help}`.toLowerCase().includes(filter)) continue;

    if (setting.section !== section) {
      section = setting.section;

      const group = document.createElement('section');
      group.className = 'sheet-group';

      const title = document.createElement('h4');
      title.textContent = section;
      group.appendChild(title);

      list = document.createElement('div');
      list.className = 'sheet-fields';
      group.appendChild(list);
      body.appendChild(group);
    }

    list.appendChild(field(setting));
    shown++;
  }

  if (shown === 0) {
    body.appendChild(Object.assign(
      document.createElement('p'),
      { className: 'sheet-empty', textContent: 'Nothing matches that filter.' }));
  }

  settingsDirty();
}

function field(setting) {
  const wrap = document.createElement('div');
  wrap.className = 'sheet-field';

  const label = document.createElement('label');
  label.htmlFor = `set:${setting.id}`;
  label.textContent = setting.key;
  wrap.appendChild(label);

  const input = control(setting);
  input.id = `set:${setting.id}`;
  input.disabled = !settingsWritable;
  input.addEventListener('input', settingsDirty);
  input.addEventListener('change', settingsDirty);
  wrap.appendChild(input);

  if (setting.help) {
    wrap.appendChild(Object.assign(
      document.createElement('p'), { className: 'hint', textContent: setting.help }));
  }

  // ⚠️ The warning that stops this page lying. A flag beats the config file, so
  // a value saved here will be overwritten at the next start by the command
  // line — the field stays editable, because saving it is still the right thing
  // to do before removing the flag, but the page says what will happen.
  if (overridden.has(setting.id)) {
    wrap.classList.add('is-overridden');
    wrap.appendChild(Object.assign(document.createElement('p'), {
      className: 'hint warn',
      textContent: 'Pinned on the command line: this server will keep using the flag’s '
        + 'value after a restart, whatever is saved here.',
    }));
  }

  // Saved into the file and not yet applied. Marked per field rather than only
  // counted in the footer, because "which one have I not restarted for" is the
  // question an operator actually has when they come back to this page.
  if (setting.pending) wrap.classList.add('is-pending');

  return wrap;
}

function control(setting) {
  if (setting.kind === 'boolean') {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'sheet-check';
    box.checked = setting.value === '1';
    return box;
  }

  if (setting.kind === 'choice') {
    const select = document.createElement('select');

    for (const choice of setting.choices || []) {
      const option = document.createElement('option');
      option.value = choice;
      option.textContent = choice;
      option.selected = choice === setting.value;
      select.appendChild(option);
    }

    return select;
  }

  const input = document.createElement('input');

  if (setting.secret) {
    input.type = 'password';
    input.autocomplete = 'new-password';
    input.value = '';

    // ⛔ Whether one is SET, never what it is. The server does not send a
    // secret's value at all, so there is nothing here to leak into a screenshot,
    // a browser's password manager or a cached page.
    input.placeholder = setting.set ? 'set — type to replace' : 'not set';
  } else {
    input.type = setting.kind === 'integer' ? 'number' : 'text';
    input.value = setting.value;
    input.spellcheck = false;

    if (setting.kind === 'list') input.placeholder = 'comma,separated';
  }

  return input;
}

/**
 * Saves the edited settings. Returns true when the file was written.
 *
 * ⚠️ The server VALIDATES before it writes, so a rejected value leaves the file
 * exactly as it was — which is what makes "Save & restart" safe to offer at all:
 * a config that would stop the server coming back up never reaches the disk.
 */
async function saveSettings() {
  const changed = edits();
  if (Object.keys(changed).length === 0) return true;

  const reply = await send({ cmd: 'config-set', settings: changed }, true);
  if (!reply.ok) return false;

  toast(`Saved ${reply.saved} setting${reply.saved === 1 ? '' : 's'}`, 'good');

  // Named individually rather than counted: the operator has to know WHICH of
  // the things they just saved the command line will keep overriding.
  if (reply.shadowed && reply.shadowed.length > 0) {
    toast(`Still pinned on the command line: ${reply.shadowed.join(', ')}`, 'bad');
  }

  await openSettings();
  return true;
}

/**
 * The restart itself, behind a confirmation that says what it costs.
 *
 * ⚠️ The stream will drop — the panel goes to "reconnecting" and comes back on
 * its own once the new server is listening. EventSource's own retry does the
 * work; connect()'s backoff covers the case where it does not come back.
 */
async function restartServer(extra) {
  if (!await ask(
    'Restart the server?',
    `${extra}Every player is disconnected and the round in progress is lost. The `
    + 'process stays up and comes back with the configuration file as it is now.')) {
    return;
  }

  await send({ cmd: 'restart' }, true);
  toast('Restarting — the panel reconnects on its own', 'good');

  // 'lost' rather than a new state: the stream is about to drop and this is the
  // same reconnecting badge every other drop shows. Inventing a fourth state
  // would mean a fourth thing for live() to know about for one second of it.
  live('lost');
}

/* ---------- wiring ---------------------------------------------------------- */

function showLogin() {
  if (stream) { stream.close(); stream = null; }
  $('boot').hidden = true;
  $('app').hidden = true;
  $('login').hidden = false;
  $('password').focus();
}

function showApp() {
  $('boot').hidden = true;
  $('login').hidden = true;
  $('app').hidden = false;
  buildCharts();
  connect();
  refreshLog();
  initFleet();
}

async function refreshLog() {
  try {
    const log = await (await fetch(api('/api/log'))).json();
    if (log.ok) appendLog(log.lines);
  } catch { /* the stream will carry the rest */ }
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();

  const button = $('login-button');
  const error = $('login-error');

  button.disabled = true;
  error.hidden = true;

  try {
    const reply = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ password: $('password').value }),
    });

    const json = await reply.json();

    if (json.ok) {
      csrf = json.csrf;
      $('password').value = '';
      showApp();
      return;
    }

    error.textContent = json.error || 'wrong password';
    error.hidden = false;
    $('login-form').classList.remove('shake');
    void $('login-form').offsetWidth;
    $('login-form').classList.add('shake');
    $('password').select();
  } catch {
    error.textContent = 'the server did not answer';
    error.hidden = false;
  } finally {
    button.disabled = false;
  }
});

/* ---------- analytics -------------------------------------------------------
 *
 * The sheet's loading and wiring. The view itself is analytics.js, which the
 * fleet page (scripts/fleet-analytics.sh) draws too.
 *
 * ⚠️ Fetched when the sheet is OPENED, never on the state cadence. The reply is
 * built on the tick thread and walks tables with up to twenty thousand player
 * records in them; putting that on a 5 Hz stream would put a per-tick cost on
 * every server with the panel open.
 */

/**
 * The map's geometry, fetched once for the life of the page.
 *
 * Eighty kilobytes of coastline that never changes, so it is not re-fetched
 * with the numbers — and a failure costs the map rather than the view.
 */
let worldGeometry = null;
let worldGeometryFailed = false;

async function openAnalytics() {
  $('analytics').showModal();
  await loadAnalytics();
}

async function loadAnalytics() {
  $('an-loading').hidden = false;
  $('an-loading').textContent = 'Loading…';
  $('an-content').hidden = true;
  $('an-off').hidden = true;

  let reply;

  try {
    const response = await fetch(api('/api/analytics'));

    if (response.status === 401) { $('analytics').close(); showLogin(); return; }

    reply = await response.json();
  } catch {
    $('an-loading').textContent = 'The server did not answer.';
    return;
  }

  if (!reply.ok) {
    $('an-loading').textContent = reply.error || 'the server refused';
    return;
  }

  if (reply.analytics.enabled && !worldGeometry && !worldGeometryFailed) {
    try {
      worldGeometry = await (await fetch('/world.json')).json();
    } catch {
      worldGeometryFailed = true;
    }
  }

  showAnalytics(reply.analytics, worldGeometry);
}

$('analytics-open').addEventListener('click', openAnalytics);
$('analytics-close').addEventListener('click', () => $('analytics').close());
$('analytics-refresh').addEventListener('click', loadAnalytics);

// Both canvases size themselves off their box, so a resized window redraws them
// or they stay at the old bitmap scaled to the new box — which on a map puts
// every marker in the wrong place.
window.addEventListener('resize', () => {
  if ($('analytics').open) redrawAnalytics();
});

$('settings-open').addEventListener('click', openSettings);
$('settings-close').addEventListener('click', () => $('settings').close());
$('settings-filter').addEventListener('input', renderSettings);

// Revert is a re-fetch rather than a form reset, so it also picks up anything
// another operator changed while this dialog was open.
$('settings-revert').addEventListener('click', openSettings);

$('settings-save').addEventListener('click', saveSettings);

$('settings-save-restart').addEventListener('click', async () => {
  // ⛔ Saved FIRST, and only restarted if the save succeeded. A restart on a
  // refused save would throw the change away and end the round for nothing.
  if (!await saveSettings()) return;

  $('settings').close();
  await restartServer('The settings you just saved will be applied. ');
});

$('restart').addEventListener('click', () => restartServer(''));

$('logout').addEventListener('click', async () => {
  await fetch('/api/logout', { method: 'POST', headers: { 'X-CSRF-Token': csrf } });
  csrf = '';
  showLogin();
});

$('chat-form').addEventListener('submit', async (event) => {
  event.preventDefault();

  const input = $('chat-input');
  const text = input.value.trim();
  if (!text) return;

  const reply = await send({ cmd: 'say', text }, true);
  if (reply.ok) input.value = '';
});

$('offline').addEventListener('click', async () => {
  if (!state) return;

  if (state.server.offline) {
    send({ cmd: 'offline', enabled: false });
    return;
  }

  const people = state.players.filter((p) => p.connected).length;

  if (await ask(
    'Take the server offline?',
    `${people === 0 ? 'Nobody is connected.' : `All ${people} connected player${people === 1 ? ' is' : 's are'} disconnected.`} `
    + 'New connections are refused and the bots are removed until you bring it back online. '
    + 'The process and this panel keep running.')) {
    send({ cmd: 'offline', enabled: true });
  }
});

// A button inside <summary> would also fold the card; the range buttons are
// for the charts, not the disclosure.
$('perf-tools').addEventListener('click', (event) => event.preventDefault());

$('perf').addEventListener('toggle', (event) => {
  if (event.target.open && perf.length > 0) drawCharts();
});

$('end-round').addEventListener('click', async () => {
  if (await ask('End the round?', 'Nobody wins, and the server moves on to the next map in the rotation after the usual ten seconds.')) {
    send({ cmd: 'end-round' });
  }
});

$('restart-round').addEventListener('click', async () => {
  if (await ask('Restart the round?', `Nobody wins, and ${state ? state.map : 'this map'} starts again from the beginning after the usual ten seconds.`)) {
    send({ cmd: 'restart-round' });
  }
});

$('autobalance').addEventListener('change', (event) => send({ cmd: 'autobalance', enabled: event.target.checked }));

$('limit-up').addEventListener('click', () => step($('player-limit'), 1));
$('limit-down').addEventListener('click', () => step($('player-limit'), -1));
$('limit-apply').addEventListener('click', () => send({ cmd: 'player-limit', value: Number($('player-limit').value) || 0 }));

$('password-form').addEventListener('submit', async (event) => {
  event.preventDefault();

  const value = $('game-password').value;
  if (!value) return;

  const reply = await send({ cmd: 'password', value });
  if (reply.ok) $('game-password').value = '';
});

$('password-clear').addEventListener('click', () => send({ cmd: 'password', value: '' }));

$('bot-limit-up').addEventListener('click', () => step($('bot-limit'), 1));
$('bot-limit-down').addEventListener('click', () => step($('bot-limit'), -1));
$('bot-limit-apply').addEventListener('click', () => send({ cmd: 'bot-limit', max: Number($('bot-limit').value) || 0 }));

for (const button of document.querySelectorAll('#brain-seg .seg-btn')) {
  button.addEventListener('click', async () => {
    const mode = button.dataset.mode;
    if (!state || state.bots.mode === mode) return;

    if (await ask('Change the bot brain?', `Every bot is removed and added back running ${mode}. Search and the policy brains cost more tick time than scripted.`)) {
      send({ cmd: 'bot-brain', mode });
    }
  });
}

$('rotation-list').addEventListener('click', (event) => {
  const button = event.target.closest('[data-rot]');
  if (!button || !state) return;

  const list = [...state.server.rotation];
  const index = Number(button.dataset.index);

  if (button.dataset.rot === 'remove') list.splice(index, 1);
  else {
    const other = button.dataset.rot === 'up' ? index - 1 : index + 1;
    if (other < 0 || other >= list.length) return;
    [list[index], list[other]] = [list[other], list[index]];
  }

  sendRotation(list);
});

$('rotation-add').addEventListener('click', async () => {
  const name = $('rotation-pick').value.trim();
  if (!name || !state) return;

  if (mapNames.length > 0 && !mapNames.includes(name)) {
    toast(`no map '${name}'`, 'bad');
    return;
  }

  const reply = await sendRotation([...state.server.rotation, name]);
  if (reply.ok) $('rotation-pick').value = '';
});

$('rotation-pick').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') $('rotation-add').click();
});

$('bots-up').addEventListener('click', () => step($('bots'), 1));
$('bots-down').addEventListener('click', () => step($('bots'), -1));
$('bots-apply').addEventListener('click', () => send({ cmd: 'bots', fill: Number($('bots').value) || 0 }));

function step(input, by) {
  input.value = Math.max(0, Math.min(48, (Number(input.value) || 0) + by));
}

$('map-go').addEventListener('click', async () => {
  const name = $('map-pick').value.trim();
  if (!name) return;

  if (await ask('Change the map?', `Everyone on the server is moved to ${name} and the round ends now.`)) {
    send({ cmd: 'map', name });
    $('map-pick').value = '';
  }
});

$('map-pick').addEventListener('keydown', (event) => {
  if (event.key === 'Enter') $('map-go').click();
});

// ⚠️ Re-read on every open, not just the first: the memory, the uptimes, the
// thread count and the GC counters are all live, and a section that showed what
// the box looked like an hour ago would be quietly wrong in the one place an
// operator goes to check a number.
$('system').addEventListener('toggle', (event) => {
  if (event.target.open) loadSystem(true);
});

$('filter').addEventListener('input', () => { if (state) roster(state); });

$('roster').addEventListener('click', (event) => {
  const more = event.target.closest('[data-menu]');

  if (more) {
    event.stopPropagation();
    const slot = Number(more.dataset.menu);

    if (menuFor && menuFor.slot === slot) closeMenu();
    else openMenu(slot, more);

    return;
  }

  const row = event.target.closest('tr[data-slot]');
  if (!row) return;

  const slot = Number(row.dataset.slot);
  selected = selected === slot ? -1 : slot;

  if (state) { roster(state); drawMap(); }
});

const canvas = $('map');

canvas.addEventListener('mousemove', (event) => {
  if (!state) return;

  const at = toWorld(event);
  const near = state.players
    .filter((p) => p.alive && p.x !== undefined)
    .map((p) => ({ p, d: Math.hypot(p.x - at.x, p.y - at.y) }))
    .sort((a, b) => a.d - b.d)[0];

  const tip = $('map-tip');

  const reach = state.match.width / canvas.getBoundingClientRect().width * 12;

  if (near && near.d < reach) {
    const box = canvas.getBoundingClientRect();
    tip.hidden = false;
    tip.textContent = `${near.p.name} · ${near.p.class} · ${Math.round(near.p.hp)} hp`;
    tip.style.left = `${(near.p.x / state.match.width) * box.width}px`;
    tip.style.top = `${(near.p.y / state.match.height) * box.height}px`;
  } else {
    tip.hidden = true;
  }
});

canvas.addEventListener('mouseleave', () => { $('map-tip').hidden = true; });

canvas.addEventListener('click', (event) => {
  if (!$('teleport-mode').checked || !state) return;

  if (selected < 0) { toast('select a player in the table first', 'bad'); return; }

  const at = toWorld(event);
  send({ cmd: 'teleport', slot: selected, x: Math.round(at.x), y: Math.round(at.y) });
});

$('teleport-mode').addEventListener('change', (event) => {
  canvas.classList.toggle('clickable', event.target.checked);
});

window.addEventListener('resize', () => {
  if (state) drawMap();
  if (perf.length > 0) drawCharts();
});

/* ---------- fleet ----------------------------------------------------------- */

/*
 * Only on a hub. /api/fleet answers `hub:false` on a server that is not one, and
 * the Fleet button stays hidden; on a hub it lists each member with that member's
 * own state object, fetched by the hub from all of them at once.
 */
async function initFleet() {
  if (member) {
    $('chip-host').hidden = false;
    $('chip-host-name').textContent = member;
  }

  try {
    const reply = await (await fetch('/api/fleet')).json();
    $('fleet-open').hidden = !reply.hub;
  } catch {
    // No fleet controls is the right degradation for a panel that cannot tell.
  }
}

async function loadFleet() {
  $('fleet-sub').textContent = 'Asking every member…';

  let reply;

  try {
    const response = await fetch('/api/fleet');
    if (response.status === 401) { $('fleet').close(); showLogin(); return; }
    reply = await response.json();
  } catch {
    $('fleet-sub').textContent = 'The hub did not answer.';
    return;
  }

  const rows = (reply.members || []).map((m) => {
    const tr = document.createElement('tr');
    const s = m.state;

    tr.appendChild(cell(m.name, 'fleet-name'));

    if (s) {
      const humans = s.players.filter((p) => p.connected).length;
      const bots = s.players.filter((p) => p.bot).length;
      const map = typeof s.map === 'string' ? s.map : (s.map && s.map.name) || '—';

      tr.appendChild(cell(s.server.name, ''));
      tr.appendChild(cell(map, 'mono'));
      tr.appendChild(cell(`${humans} + ${bots} bots / ${s.server.slots}`, 'num'));
      tr.appendChild(cell(`${s.server.tickMs.toFixed(2)} ms`, 'num'));
      tr.appendChild(cell(s.server.commit || s.server.version || '—', 'mono'));
    } else {
      const down = cell(m.error || 'unreachable', 'fleet-down');
      down.colSpan = 5;
      tr.appendChild(down);
    }

    const tools = document.createElement('td');
    tools.className = 'fleet-tools';

    const open = document.createElement('a');
    open.className = 'btn btn-quiet';
    open.textContent = 'Open';
    open.href = `/?host=${encodeURIComponent(m.name)}`;

    const remove = document.createElement('button');
    remove.className = 'btn btn-quiet btn-warn';
    remove.type = 'button';
    remove.textContent = 'Remove';
    remove.addEventListener('click', () => removeMember(m.name));

    tools.append(open, remove);
    tr.appendChild(tools);
    return tr;
  });

  $('fleet-rows').replaceChildren(...rows);
  $('fleet-empty').hidden = rows.length > 0;
  $('fleet-sub').textContent = `${rows.length} member${rows.length === 1 ? '' : 's'}, each pinned to its own certificate.`;
}

async function fleetPost(path, body) {
  const reply = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
    body: JSON.stringify(body),
  });

  if (reply.status === 401) { showLogin(); return { ok: false, error: 'signed out' }; }
  return reply.json();
}

async function removeMember(name) {
  const sure = await ask(
    `Remove ${name}?`,
    'The hub forgets its token and stops managing it. The server itself keeps running; '
    + 'pairing it again needs its pairing code.');

  if (!sure) return;

  const reply = await fleetPost('/api/fleet/remove', { name });
  if (!reply.ok) toast(reply.error || 'refused', 'bad');
  else toast(`Removed ${name}`, 'good');
  await loadFleet();
}

$('fleet-open').addEventListener('click', async () => {
  $('fleet').showModal();
  await loadFleet();
});

$('fleet-close').addEventListener('click', () => $('fleet').close());
$('fleet-refresh').addEventListener('click', loadFleet);

$('fleet-add').addEventListener('submit', async (event) => {
  event.preventDefault();

  const button = $('fleet-add-button');
  const error = $('fleet-error');

  button.disabled = true;
  button.textContent = 'Pairing…';
  error.hidden = true;

  try {
    const reply = await fleetPost('/api/fleet/add', {
      name: $('fleet-name').value.trim(),
      url: $('fleet-url').value.trim(),
      pairing: $('fleet-pairing').value.trim(),
    });

    if (!reply.ok) {
      error.textContent = reply.error || 'refused';
      error.hidden = false;
      return;
    }

    toast(`Paired with ${$('fleet-name').value.trim()}`, 'good');
    $('fleet-add').reset();
    await loadFleet();
  } catch {
    error.textContent = 'the hub did not answer';
    error.hidden = false;
  } finally {
    button.disabled = false;
    button.textContent = 'Pair';
  }
});

/* ---------- start ----------------------------------------------------------- */

(async () => {
  try {
    const session = await (await fetch('/api/session')).json();

    $('login-server').textContent = session.name || '';
    $('login-insecure').hidden = session.secure || location.hostname === 'localhost'
      || location.hostname === '127.0.0.1';

    // ⚠️ /api/session re-issues the CSRF token to a browser that already holds a
    // valid cookie, so a reload does not mean signing in again. It is safe to
    // hand back here and nowhere else: the reply is readable only by a page on
    // this origin — no CORS headers are sent — and the SameSite=Strict cookie
    // that authorised it is not attached to a cross-site request in the first
    // place.
    if (session.authenticated && session.csrf) {
      csrf = session.csrf;
      showApp();
      return;
    }

    showLogin();
  } catch {
    showLogin();
    $('login-error').textContent = 'the server did not answer';
    $('login-error').hidden = false;
  }
})();

