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

    if (reply.status === 401) { showLogin(); return { ok: false, error: t('app.toast.signedOut') }; }

    const json = await reply.json();

    // A refusal is the interesting case and the server always explains itself,
    // so it is shown whether or not the caller asked to be quiet.
    if (!json.ok) toast(json.error || t('app.toast.refused'), 'bad');
    else if (!quiet) toast(describe(body), 'good');

    return json;
  } catch (e) {
    toast(t('app.toast.noAnswer'), 'bad');
    return { ok: false, error: String(e) };
  }
}

function describe(body) {
  switch (body.cmd) {
    case 'bots': return t('app.done.botFill', { fill: body.fill });
    case 'map': return t('app.done.map', { name: body.name });
    case 'kick': return t('app.done.kick');
    case 'ban': return t('app.done.ban');
    case 'offline': return body.enabled ? t('app.done.offline') : t('app.done.online');
    case 'end-round': return t('app.done.endRound');
    case 'restart-round': return t('app.done.restartRound');
    case 'autobalance': return body.enabled ? t('app.done.autobalanceOn') : t('app.done.autobalanceOff');
    case 'player-limit': return t('app.done.playerLimit', { value: body.value });
    case 'password': return body.value ? t('app.done.passwordSet') : t('app.done.passwordRemoved');
    case 'rotation': return t('app.done.rotation');
    case 'team': return t('app.done.team', { team: body.team });
    case 'bot-limit': return t('app.done.botLimit', { max: body.max });
    case 'bot-brain': return t('app.done.botBrain', { mode: body.mode });
    case 'bot-difficulty': return t('app.done.botDifficulty', { tier: body.tier });
    case 'teleport': return t('app.done.teleport');
    case 'config-set': return t('app.done.configSet');
    case 'restart': return t('app.done.restart');
    default: return t('app.done.generic');
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
  $('live-text').textContent = kind === 'live' ? t('app.live.live') : kind === 'lost' ? t('app.live.lost') : t('app.live.connecting');
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
  $('server-port').textContent = t('app.server.port', { port: next.server.port });
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
  // The map's name is bold, so the sentence is split around its placeholder
  // rather than concatenated, and a translation may put it anywhere.
  const [before, after] = t('app.banner.changingMap', { seconds: Math.max(0, Math.ceil(s.match.changeIn / 30)) })
    .split('{map}');

  const name = document.createElement('b');
  name.textContent = s.match.nextMap;
  node.replaceChildren(before, name, after ?? '');
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
    ? (s.round.winner === 'Spectator' ? t('app.clock.stalemate') : t('app.clock.wins', { team: s.round.winner.toUpperCase() }))
    : t('app.clock.firstTo', { count: s.match.capLimit });

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
  $('tile-tick-note').textContent = t('app.tiles.tickNote', { share: share.toFixed(1), budget: budget.toFixed(1) });

  const bots = s.players.filter((p) => p.bot).length;
  const humans = s.players.filter((p) => p.connected).length;

  $('tile-players').textContent = humans + bots;
  $('tile-slots').textContent = t('app.tiles.slots', { slots: s.server.slots });
  $('tile-bots').textContent = t('app.tiles.population', {
    humans: t('app.tiles.humans', { count: humans }),
    bots: t('app.tiles.bots', { count: bots }),
  });

  $('tile-uptime').textContent = duration(s.server.uptimeSeconds);
  $('tile-state').textContent = s.server.offline ? t('app.tiles.offline')
    : s.frozen ? t('app.tiles.frozen') : s.server.idle ? t('app.tiles.idle') : t('app.tiles.running');
  $('tile-state').classList.toggle('warn', s.frozen || s.server.offline);

  $('tile-joins').textContent = s.server.joins.toLocaleString();

  // Where the chat box's lines go, which a player sees very differently: chat
  // through a plugin, or an on-screen notice that replaces the last one.
  $('chat-hint').textContent = s.server.chatPlugin
    ? t('app.chat.hintPlugin', { name: s.players[0] ? s.players[0].name : t('app.chat.theServer'), plugin: s.server.chatPlugin })
    : t('app.chat.hintNotice');

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
    ? t('app.bots.suspended', { count: Math.ceil(s.server.botSuspendSeconds / 60) })
    : blocked
    ? t('app.bots.blocked', { count: s.server.botMinHumans, humans })
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
    parts.push(t('app.brain.arbiter', {
      budget: s.bots.arbiterMicroseconds.toLocaleString(),
      spent: s.bots.arbiterSpentMicroseconds.toLocaleString(),
    }));
  }

  if (all.length === 0 && s.bots.tiers.length > 0) {
    parts.push(t('app.brain.noBots'));
  } else if (s.bots.engage === 'humans') {
    // ⛔ The gate every tier is behind, and the reason a correctly configured
    // brain can drive far less than an operator expects — a live server hands a
    // controller the wheel for a HUMAN enemy only.
    //
    // ⚠️ Stated as the CONFIGURATION, never as a prediction that they will not
    // drive. The share beside each tier above is the measurement and it is the
    // one to believe — the gate rests on PlayerState.IsBot surviving a respawn,
    // which is a simulation property this page cannot check.
    parts.push(t('app.brain.humansOnly'));
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
  if (tier.classes) drives.title = t('app.brain.drives', { classes: tier.classes.join(', ') });
  row.appendChild(drives);

  const live = document.createElement('span');
  live.className = 'tier-live';

  const driving = mine.filter((p) => p.driving).length;
  const micro = mine.reduce((sum, p) => sum + (p.microTicks || 0), 0);
  const polls = micro + mine.reduce((sum, p) => sum + (p.scriptedTicks || 0), 0);

  live.textContent = mine.length === 0
    ? t('app.brain.tierEmpty')
    : t('app.brain.tierBots', { count: mine.length })
      + (share && polls > 0 ? ` · ${((micro / polls) * 100).toFixed(1)}%` : '');

  live.classList.toggle('is-driving', driving > 0);

  live.title = share
    ? t('app.brain.tierShare', { driving, micro: micro.toLocaleString(), polls: polls.toLocaleString() })
    : t('app.brain.tierScripted');

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
  $('roster-empty').textContent = filter ? t('app.roster.noMatch') : t('app.roster.nobody');
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

  if (player.bot) name.appendChild(tag(t('app.roster.botTag'), 'bot'));

  // ⚠️ The brain that WILL drive this slot, lit while it actually is. A
  // controller holds the wheel only inside the engagement window, so an unlit
  // badge is the ordinary case and not a brain that has stopped working — the
  // Bots card carries the share that answers that.
  if (player.bot && player.brain && player.brain !== 'scripted') {
    const badge = tag(player.brain, 'brain' + (player.driving ? ' is-driving' : ''));
    const polls = (player.microTicks || 0) + (player.scriptedTicks || 0);

    badge.title = polls > 0
      ? t('app.roster.brainShare', {
        brain: player.brain,
        share: ((player.microTicks / polls) * 100).toFixed(1),
        micro: player.microTicks.toLocaleString(),
        polls: polls.toLocaleString(),
      })
      : player.brain;

    name.appendChild(badge);
  }
  if (player.intel) name.appendChild(tag(t('app.roster.intelTag'), 'intel'));

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
  more.title = t('app.roster.actionsFor', { name: player.name });
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

  for (const [kind, intel] of [['red', s.redIntel], ['blue', s.blueIntel]]) {
    if (!intel.present) continue;

    const carrier = intel.carrier >= 0 ? s.players[intel.carrier] : null;
    const home = intel.x === intel.homeX && intel.y === intel.homeY;
    const status = carrier ? t('app.objective.takenBy', { name: carrier.name })
      : home ? t('app.objective.atBase') : t('app.objective.dropped');
    const label = kind === 'red' ? t('app.objective.redIntel') : t('app.objective.blueIntel');

    parts.push(pill(label, status, kind));
  }

  s.points.forEach((point, i) => {
    // The owner is the server's team name: None, Red or Blue.
    const owner = t(`app.objective.owner.${point.owner}`);
    const node = pill(t('app.objective.point', { number: i + 1 }), owner, point.owner === 'Red' ? 'red' : point.owner === 'Blue' ? 'blue' : '');

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
  image.onerror = () => { $('map-empty').textContent = t('app.map.noImage', { name }); };
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
    title: t('app.charts.tick.title'),
    unit: t('app.charts.tick.unit'),
    floor: 5,
    digits: 2,
    rule: (s) => s.tickBudget,
    ruleLabel: (ms) => t('app.charts.tick.budget', { ms: ms.toFixed(1) }),
    series: [
      { field: 'tickMean', label: t('app.charts.tick.mean'), color: SERIES.aqua, fill: true },
      { field: 'tickPeak', label: t('app.charts.tick.worst'), color: SERIES.orange },
    ],
  },
  {
    key: 'cpu',
    title: t('app.charts.cpu.title'),
    unit: t('app.charts.cpu.unit'),
    floor: 10,
    digits: 1,
    series: [{ field: 'cpu', label: t('app.charts.cpu.cpu'), color: SERIES.aqua, fill: true }],
  },
  {
    key: 'mem',
    title: t('app.charts.mem.title'),
    unit: t('app.charts.mem.unit'),
    floor: 64,
    digits: 0,
    series: [
      { field: 'rss', label: t('app.charts.mem.resident'), color: SERIES.aqua, fill: true },
      { field: 'heap', label: t('app.charts.mem.heap'), color: SERIES.violet },
    ],
  },
  {
    key: 'late',
    title: t('app.charts.late.title'),
    unit: t('app.charts.late.unit'),
    floor: 2,
    digits: 0,
    series: [{ field: 'late', label: t('app.charts.late.late'), color: SERIES.warning, fill: true }],
    // (SERIES.warning is a function; resolve() below calls it.)
  },
  {
    key: 'net',
    title: t('app.charts.net.title'),
    unit: t('app.charts.net.unit'),
    floor: 8,
    digits: 1,
    series: [{ field: 'bytes', label: t('app.charts.net.out'), color: SERIES.aqua, fill: true, scale: 1 / 1024 }],
  },
  {
    key: 'load',
    title: t('app.charts.load.title'),
    unit: t('app.charts.load.unit'),
    floor: 4,
    digits: 0,
    series: [
      { field: 'characters', label: t('app.charts.load.alive'), color: SERIES.aqua, fill: true },
      { field: 'bots', label: t('app.charts.load.bots'), color: SERIES.orange },
      { field: 'humans', label: t('app.charts.load.humans'), color: SERIES.violet },
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
    title.textContent = t('app.charts.caption', { title: chart.title, unit: chart.unit });

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
    canvas.setAttribute('aria-label', t('app.charts.aria', { title: chart.title, unit: chart.unit }));

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

      $('axis-from').textContent = t('app.charts.minutesAgo', { count: range / 60 });
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
    g.fillText(t('app.charts.waiting'), pad.left + 1, pad.top + plotH / 2);
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
    ? t('app.kpi.worstOverrun', { ms: state.server.worstOverrunMs.toFixed(1) })
    : t('app.kpi.none');

  $('kpi-sent').textContent = bytes(state.server.bytesSent);
}

function bytes(value) {
  if (value < 1024) return t('app.units.b', { value });
  if (value < 1024 * 1024) return t('app.units.kb', { value: (value / 1024).toFixed(1) });
  if (value < 1024 * 1024 * 1024) return t('app.units.mb', { value: (value / 1024 / 1024).toFixed(1) });
  return t('app.units.gb', { value: (value / 1024 / 1024 / 1024).toFixed(2) });
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

  for (const [team, label] of [
    ['Red', t('app.menu.moveRed')],
    ['Blue', t('app.menu.moveBlue')],
    ['Spectator', t('app.menu.moveSpectators')],
  ]) {
    if (player.team === team) continue;
    items.push(menuItem(label, `team-${team.toLowerCase()}`, () => send({ cmd: 'team', slot, team: team.toLowerCase() })));
  }

  if (player.connected) {
    items.push(Object.assign(document.createElement('div'), { className: 'menu-sep' }));

    items.push(menuItem(t('app.menu.kick'), 'danger', async () => {
      if (await ask(t('app.menu.kickTitle'), t('app.menu.kickBody', { name: player.name }))) {
        send({ cmd: 'kick', slot });
      }
    }));

    // ⚠️ The ban is on the ADDRESS, permanently, and the dialog says so: anyone
    // behind the same NAT is banned with them. BanList has the case against
    // reading an address as a person.
    items.push(menuItem(t('app.menu.ban'), 'danger', async () => {
      if (await ask(
        t('app.menu.banTitle'),
        player.address
          ? t('app.menu.banBodyAddress', { name: player.name, address: player.address })
          : t('app.menu.banBody', { name: player.name }))) {
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
      toast(t('app.menu.gone', { name: target ? target.name : t('app.menu.thatPlayer') }), 'bad');
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
      scope.textContent = t('app.chat.teamOnly');
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
    if (force) $('sys-body').replaceChildren(hint(t('app.toast.noAnswer')));
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
    dd.textContent = t('app.system.unknown');
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

  const value = mhz >= 1000 ? t('app.units.ghz', { value: (mhz / 1000).toFixed(2) }) : t('app.units.mhz', { value: mhz });

  // ⚠️ The two platforms mean different things by this and the panel says so:
  // Windows records the clock at boot, Linux reports what core 0 is doing right
  // now — which on a shared VPS moves between requests.
  return nominal ? t('app.system.clockNominal', { clock: value }) : t('app.system.clockNow', { clock: value });
}

function renderSystem(info) {
  const { os, cpu, memory, runtime, paths, disk } = info;

  // The collapsed one-liner. Enough to answer "what am I on" without opening it.
  $('sys-peek').textContent = [
    os.description.split(' ').slice(0, 2).join(' '),
    t('app.system.cores', { count: cpu.logicalCores }),
    memory.totalBytes ? bytes(memory.totalBytes) : null,
    runtime.build === 'Debug' ? t('app.system.debugBuild') : null,
  ].filter(Boolean).join(' · ');

  $('sys-body').replaceChildren(
    group(t('app.system.osTitle'), (l) => {
      fact(l, t('app.system.os'), os.description);
      fact(l, t('app.system.architecture'), os.architecture);
      fact(l, t('app.system.host'), os.machine);
      fact(l, t('app.system.uptime'), duration(os.uptimeSeconds));
    }),

    group(t('app.system.cpuTitle'), (l) => {
      fact(l, t('app.system.model'), cpu.model);
      fact(l, t('app.system.clock'), hertz(cpu.megahertz, cpu.clockIsNominal));

      // ⚠️ Logical, and what the RUNTIME sees — on a VPS with a CPU quota that
      // is not the host's core count, and it is the one that explains the tick.
      fact(l, t('app.system.logicalCores'), cpu.logicalCores);
    }),

    group(t('app.system.memoryTitle'), (l) => {
      fact(l, t('app.system.total'), memory.totalBytes === null ? null : bytes(memory.totalBytes));
      fact(l, t('app.system.available'), memory.availableBytes === null ? null : bytes(memory.availableBytes));

      // Says whether the two figures above are the container's or the host's.
      fact(l, t('app.system.reportedBy'), memory.source);
      fact(l, t('app.system.thisProcess'), bytes(memory.processBytes));
      fact(l, t('app.system.managedHeap'), bytes(memory.managedBytes));
    }),

    group(t('app.system.runtimeTitle'), (l) => {
      fact(l, t('app.system.framework'), runtime.framework);
      fact(l, t('app.system.identifier'), runtime.identifier);
      fact(l, t('app.system.build'), badge(runtime.build, runtime.build === 'Debug' ? 'debug' : null));
      fact(l, t('app.system.gc'), t('app.system.gcMode', {
        kind: runtime.serverGc ? t('app.system.gcServer') : t('app.system.gcWorkstation'),
        concurrency: runtime.concurrentGc ? t('app.system.gcConcurrent') : t('app.system.gcNonConcurrent'),
      }));
      fact(l, t('app.system.collections'), runtime.collections.join(' / '));
      fact(l, t('app.system.threads'), runtime.threads);
      fact(l, t('app.system.process'), t('app.system.processValue', {
        pid: runtime.processId,
        uptime: duration(runtime.processUptimeSeconds),
      }));
      fact(l, t('app.system.timer'), runtime.timerResolution);
    }),

    group(t('app.system.storageTitle'), (l) => {
      if (disk) {
        fact(l, t('app.system.mount'), disk.mount, 'path');
        fact(l, t('app.system.free'), t('app.system.freeValue', { free: bytes(disk.freeBytes), total: bytes(disk.totalBytes) }), 'path');
      }

      fact(l, t('app.system.maps'), paths.maps, 'path');
      fact(l, t('app.system.nav'), paths.nav, 'path');
      fact(l, t('app.system.recordings'), paths.recordings, 'path');
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

    if (index === s.server.rotationIndex) item.appendChild(tag(t('app.rotation.now'), 'now'));

    const tools = document.createElement('span');
    tools.className = 'rot-tools';

    for (const [action, text, title, disabled] of [
      ['up', '↑', t('app.rotation.moveUp'), index === 0],
      ['down', '↓', t('app.rotation.moveDown'), index === list.length - 1],
      ['remove', '×', t('app.rotation.remove'), false],
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
  $('offline').textContent = offline ? t('app.offline.bringOnline') : t('app.offline.takeOffline');
  $('offline').classList.toggle('btn-danger', !offline);
  $('offline').classList.toggle('btn-primary', offline);
  $('offline-hint').textContent = offline
    ? t('app.offline.hintOffline')
    : t('app.offline.hintOnline');

  $('password-state').textContent = s.server.passworded ? t('app.password.set') : t('app.password.none');
  $('password-state').classList.toggle('is-set', s.server.passworded);
  $('password-clear').disabled = !s.server.passworded;

  for (const button of document.querySelectorAll('#difficulty-seg .seg-btn')) {
    button.classList.toggle('is-on', s.bots.difficulty === Number(button.dataset.tier));
  }

  for (const button of document.querySelectorAll('#brain-seg .seg-btn')) {
    const mode = button.dataset.mode;
    button.classList.toggle('is-on', s.bots.mode === mode);

    // A policy brain needs a checkpoint the server was started with.
    button.disabled = (mode === 'policy' || mode === 'tiered') && !s.server.policy;
    button.title = button.disabled ? t('app.brain.needsPolicy') : '';
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

/** A sheet's single-line placeholder: loading, failed, or the server's own error. */
function sheetEmpty(text) {
  return Object.assign(document.createElement('p'), { className: 'sheet-empty', textContent: text });
}

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
  const changed = edits();
  const count = Object.keys(changed).length;

  for (const setting of settings) {
    cfgMarkDirty($('settings-body'), $('settings-rail'), setting.id, setting.id in changed);
  }

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

  if (count > 0) parts.push(t('app.settings.unsaved', { count }));

  if (waiting > 0) parts.push(t('app.settings.waiting', { count: waiting }));

  if (parts.length === 0) parts.push(t('app.settings.agree'));
  else parts.push(t('app.settings.takeEffect'));

  $('settings-status').textContent = parts.join(' ');
}

async function openSettings() {
  $('settings').showModal();
  $('settings-body').replaceChildren(sheetEmpty(t('app.settings.loading')));
  $('settings-rail').replaceChildren();

  let config;

  try {
    const reply = await fetch(api('/api/config'));
    if (reply.status === 401) { $('settings').close(); showLogin(); return; }
    config = await reply.json();
  } catch {
    $('settings-body').replaceChildren(sheetEmpty(t('app.settings.noAnswer')));
    return;
  }

  if (!config.ok) {
    $('settings-body').replaceChildren(sheetEmpty(config.error));
    return;
  }

  settings = config.settings;
  overridden = new Set(config.overridden || []);
  settingsWritable = config.writable;

  $('settings-file').textContent = config.writable
    ? config.file
    : t('app.settings.noFile');

  const readonly = $('settings-readonly');
  readonly.hidden = config.writable;

  if (!config.writable) {
    readonly.textContent = t('app.settings.readonly');
  }

  // A file pushed by scripts/deploy.sh --config. Saving here still works — it
  // is the right tool for a change that cannot wait — but the change lives only
  // on this server, and the next push stops for it until it is forced. Said
  // before the operator types, not after they wonder where their edit went.
  const managed = $('settings-managed');

  if (config.managed) {
    const parts = [t('app.settings.managed')];
    if (config.editedSincePush) parts.push(t('app.settings.editedSincePush'));
    if (config.pushWaiting) parts.push(t('app.settings.pushWaiting'));
    managed.textContent = parts.join(' ');
    managed.hidden = false;
  } else {
    managed.hidden = true;
  }

  renderSettings();
}

function renderSettings() {
  const filter = $('settings-filter').value.trim().toLowerCase();

  const items = settings
    .filter((setting) => cfgMatches(setting, filter))
    .map((setting) => ({ heading: setting.section, node: settingField(setting) }));

  cfgRenderGroups($('settings-body'), $('settings-rail'), items, t('app.settings.noMatch'));
  settingsDirty();
}

function settingField(setting) {
  const tags = [];
  const notes = [];

  // Saved into the file and not yet applied. Tagged per field rather than only
  // counted in the footer, because "which one have I not restarted for" is the
  // question an operator actually has when they come back to this page. The
  // accent rather than --warning: nothing is wrong, a step is outstanding —
  // and "pinned" means the save will never take, which must not look the same.
  if (setting.pending) {
    tags.push({ text: t('app.settings.pendingTag'), kind: 'accent', title: t('app.settings.pendingTitle') });
  }

  // ⚠️ The warning that stops this page lying. A flag beats the config file, so
  // a value saved here will be overwritten at the next start by the command
  // line — the field stays editable, because saving it is still the right thing
  // to do before removing the flag, but the page says what will happen.
  if (overridden.has(setting.id)) {
    tags.push({ text: t('app.settings.pinnedTag'), kind: 'warn', title: t('app.settings.pinnedTitle') });
    notes.push({ kind: 'warn', text: t('app.settings.pinnedNote') });
  }

  return cfgField(setting, {
    id: `set:${setting.id}`,
    // ⛔ Whether a secret is SET, never what it is: the server does not send
    // its value, so the field starts empty on every open (see edits()).
    value: setting.secret ? '' : setting.value,
    placeholder: setting.secret ? (setting.set ? t('app.settings.secretSet') : t('app.settings.secretNotSet')) : '',
    disabled: !settingsWritable,
    tags,
    notes,
    onChange: settingsDirty,
  });
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

  toast(t('app.settings.saved', { count: reply.saved }), 'good');

  // Named individually rather than counted: the operator has to know WHICH of
  // the things they just saved the command line will keep overriding.
  if (reply.shadowed && reply.shadowed.length > 0) {
    toast(t('app.settings.stillPinned', { names: reply.shadowed.join(', ') }), 'bad');
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
  const body = t('app.restart.body');

  if (!await ask(t('app.restart.title'), extra ? `${extra} ${body}` : body)) {
    return;
  }

  await send({ cmd: 'restart' }, true);
  toast(t('app.restart.toast'), 'good');

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

    error.textContent = json.error || t('app.login.wrongPassword');
    error.hidden = false;
    $('login-form').classList.remove('shake');
    void $('login-form').offsetWidth;
    $('login-form').classList.add('shake');
    $('password').select();
  } catch {
    error.textContent = t('app.toast.noAnswer');
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

/**
 * Which server the sheet shows: '' for the hub's own (or, off a hub, this
 * page's), a member's name, or '*' for every server added together. Starts on
 * the server the page is showing.
 */
let anSelected = member;

/** Bumped per load, so a slow reply for a server no longer selected is dropped. */
let anLoad = 0;

async function openAnalytics() {
  $('analytics').showModal();

  if (hubMembers) {
    analyticsHosts([['', t('app.fleet.hubTab'), t('app.fleet.hubTitle')], ...hubMembers.map((name) => [name, name, name])], anSelected);
  }

  await loadAnalytics();
}

/**
 * One server's `analytics` reply: `{ analytics }`, `{ error }`, or `{ signedOut }`.
 *
 * The path is spelled out rather than built by api(): on a member's page the
 * sheet can still show the hub or another member.
 */
async function fetchAnalytics(server) {
  const path = server ? `/h/${encodeURIComponent(server)}/api/analytics` : '/api/analytics';

  try {
    const response = await fetch(path);
    if (response.status === 401) return { signedOut: true };

    const reply = await response.json();
    return reply.ok ? { analytics: reply.analytics } : { error: reply.error || t('app.analytics.refused') };
  } catch {
    return { error: t('app.settings.noAnswer') };
  }
}

function serverLabel(server) {
  return server || t('app.fleet.hubTab');
}

async function loadAnalytics() {
  const load = ++anLoad;
  const all = anSelected === '*';

  $('an-loading').hidden = false;
  $('an-loading').textContent = t('app.settings.loading');
  $('an-content').hidden = true;
  $('an-off').hidden = true;
  $('an-note').hidden = true;

  for (const button of $('an-hosts').querySelectorAll('.seg-btn'))
    button.classList.toggle('is-on', button.dataset.host === anSelected);

  // ⚠️ All servers are asked at once, and one that does not answer costs its
  // own figures, not the sum's: it is named above them instead.
  const servers = all ? ['', ...hubMembers] : [anSelected];
  const replies = await Promise.all(servers.map(fetchAnalytics));

  if (load !== anLoad) return;

  if (replies.some((r) => r.signedOut)) { $('analytics').close(); showLogin(); return; }

  let data;

  if (all) {
    const notes = [];

    servers.forEach((server, i) => {
      if (replies[i].error) notes.push(t('app.analytics.noAnswer', { name: serverLabel(server) }));
      else if (!replies[i].analytics.enabled) notes.push(t('app.analytics.off', { name: serverLabel(server) }));
    });

    data = sumAnalytics(servers
      .map((server, i) => ({ host: serverLabel(server), analytics: replies[i].analytics }))
      .filter((h) => h.analytics));

    $('an-note').textContent = notes.join(' ');
    $('an-note').hidden = notes.length === 0;
  } else {
    if (replies[0].error) {
      $('an-loading').textContent = replies[0].error;
      return;
    }

    data = replies[0].analytics;
  }

  if (data.enabled && !worldGeometry && !worldGeometryFailed) {
    try {
      worldGeometry = await (await fetch('/world.json')).json();
    } catch {
      worldGeometryFailed = true;
    }

    if (load !== anLoad) return;
  }

  showAnalytics(data, worldGeometry);

  // On a hub the sheet can show any server, so it says which.
  if (hubMembers && !all) {
    $('analytics-sub').textContent = t('app.analytics.sub', { server: serverLabel(anSelected), file: $('analytics-sub').textContent });
  }
}

$('an-hosts').addEventListener('click', (event) => {
  const button = event.target.closest('.seg-btn');
  if (!button || button.dataset.host === anSelected) return;

  anSelected = button.dataset.host;
  loadAnalytics();
});

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
$('settings-filter').addEventListener('input', () => {
  $('settings-body').scrollTop = 0;
  renderSettings();
});

// Revert is a re-fetch rather than a form reset, so it also picks up anything
// another operator changed while this dialog was open.
$('settings-revert').addEventListener('click', openSettings);

$('settings-save').addEventListener('click', saveSettings);

$('settings-save-restart').addEventListener('click', async () => {
  // ⛔ Saved FIRST, and only restarted if the save succeeded. A restart on a
  // refused save would throw the change away and end the round for nothing.
  if (!await saveSettings()) return;

  $('settings').close();
  await restartServer(t('app.restart.afterSave'));
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

  const who = people === 0 ? t('app.offline.askNobody') : t('app.offline.askPeople', { count: people });

  if (await ask(t('app.offline.askTitle'), `${who} ${t('app.offline.askBody')}`)) {
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
  if (await ask(t('app.round.endTitle'), t('app.round.endBody'))) {
    send({ cmd: 'end-round' });
  }
});

$('restart-round').addEventListener('click', async () => {
  if (await ask(t('app.round.restartTitle'), t('app.round.restartBody', { map: state ? state.map : t('app.round.thisMap') }))) {
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

// Applied to the running bots in place, so no confirmation: nobody is removed.
for (const button of document.querySelectorAll('#difficulty-seg .seg-btn')) {
  button.addEventListener('click', () => {
    const tier = Number(button.dataset.tier);
    if (!state || state.bots.difficulty === tier) return;
    send({ cmd: 'bot-difficulty', tier });
  });
}

for (const button of document.querySelectorAll('#brain-seg .seg-btn')) {
  button.addEventListener('click', async () => {
    const mode = button.dataset.mode;
    if (!state || state.bots.mode === mode) return;

    if (await ask(t('app.brain.changeTitle'), t('app.brain.changeBody', { mode }))) {
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
    toast(t('app.rotation.noMap', { name }), 'bad');
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

  if (await ask(t('app.map.changeTitle'), t('app.map.changeBody', { name }))) {
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
    tip.textContent = t('app.map.tip', { name: near.p.name, class: near.p.class, hp: Math.round(near.p.hp) });
    tip.style.left = `${(near.p.x / state.match.width) * box.width}px`;
    tip.style.top = `${(near.p.y / state.match.height) * box.height}px`;
  } else {
    tip.hidden = true;
  }
});

canvas.addEventListener('mouseleave', () => { $('map-tip').hidden = true; });

canvas.addEventListener('click', (event) => {
  if (!$('teleport-mode').checked || !state) return;

  if (selected < 0) { toast(t('app.map.selectFirst'), 'bad'); return; }

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
/** The hub's members by name, from the last /api/fleet; null off a hub. */
let hubMembers = null;

async function initFleet() {
  let reply;

  try {
    reply = await (await fetch('/api/fleet')).json();
  } catch {
    // No fleet controls is the right degradation for a panel that cannot tell.
    return;
  }

  $('fleet-open').hidden = !reply.hub;
  if (!reply.hub) return;

  renderHosts(reply.members || []);

  // The dots are the only thing here that goes stale, and the overview asks
  // every member for a full state, so this is slow on purpose and skipped while
  // the tab is in the background.
  setInterval(async () => {
    if (document.hidden) return;
    try {
      const again = await (await fetch('/api/fleet')).json();
      if (again.hub) renderHosts(again.members || []);
    } catch {
      // Keep the last tabs; the next round tries again.
    }
  }, 20000);
}

/**
 * The server tabs under the top bar: the hub's own server, then each member,
 * with a dot for whether the hub reached it on its last try. Each is a plain
 * link, so a middle click opens a member in a new tab.
 */
function renderHosts(members) {
  hubMembers = members.map((m) => m.name);

  const tabs = [hostTab('', t('app.fleet.hubTab'), true, t('app.fleet.hubTitle'))];

  for (const m of members) {
    const s = m.state;
    const map = s && (typeof s.map === 'string' ? s.map : (s.map && s.map.name));
    const humans = s ? s.players.filter((p) => p.connected).length : 0;
    const title = s
      ? t('app.fleet.tabTitle', { name: s.server.name, map: map || t('app.fleet.noMap'), count: humans })
      : (m.error || t('app.fleet.unreachable'));
    tabs.push(hostTab(m.name, m.name, !!s, title));
  }

  // A member removed since this page loaded still names where the page is.
  if (member && !members.some((m) => m.name === member)) {
    tabs.push(hostTab(member, member, false, t('app.fleet.notMember')));
  }

  $('hosts').replaceChildren(...tabs);
  $('hosts').hidden = false;
}

function hostTab(name, label, up, title) {
  const a = document.createElement('a');
  a.className = 'host-tab';
  a.href = name ? `/?host=${encodeURIComponent(name)}` : '/';
  a.title = title;
  if (name === member) a.setAttribute('aria-current', 'page');

  const dot = document.createElement('span');
  dot.className = up ? 'host-dot' : 'host-dot is-down';

  const text = document.createElement('span');
  text.textContent = label;

  a.append(dot, text);
  return a;
}

async function loadFleet() {
  $('fleet-sub').textContent = t('app.fleet.asking');

  let reply;

  try {
    const response = await fetch('/api/fleet');
    if (response.status === 401) { $('fleet').close(); showLogin(); return; }
    reply = await response.json();
  } catch {
    $('fleet-sub').textContent = t('app.fleet.hubNoAnswerSentence');
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
      tr.appendChild(cell(t('app.fleet.players', { humans, bots, slots: s.server.slots }), 'num'));
      tr.appendChild(cell(t('app.fleet.tick', { ms: s.server.tickMs.toFixed(2) }), 'num'));
      tr.appendChild(cell(s.server.commit || s.server.version || '—', 'mono'));
    } else {
      const down = cell(m.error || t('app.fleet.unreachable'), 'fleet-down');
      down.colSpan = 5;
      tr.appendChild(down);
    }

    const tools = document.createElement('td');
    tools.className = 'fleet-tools';

    const open = document.createElement('a');
    open.className = 'btn btn-quiet';
    open.textContent = t('app.fleet.open');
    open.href = `/?host=${encodeURIComponent(m.name)}`;

    const remove = document.createElement('button');
    remove.className = 'btn btn-quiet btn-warn';
    remove.type = 'button';
    remove.textContent = t('app.fleet.remove');
    remove.addEventListener('click', () => removeMember(m.name));

    tools.append(open, remove);
    tr.appendChild(tools);
    return tr;
  });

  $('fleet-rows').replaceChildren(...rows);
  renderHosts(reply.members || []);
  $('fleet-empty').hidden = rows.length > 0;
  $('fleet-sub').textContent = t('app.fleet.members', { count: rows.length });
}

async function fleetPost(path, body) {
  const reply = await fetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
    body: JSON.stringify(body),
  });

  if (reply.status === 401) { showLogin(); return { ok: false, error: t('app.toast.signedOut') }; }
  return reply.json();
}

async function removeMember(name) {
  const sure = await ask(t('app.fleet.removeTitle', { name }), t('app.fleet.removeBody'));

  if (!sure) return;

  const reply = await fleetPost('/api/fleet/remove', { name });
  if (!reply.ok) toast(reply.error || t('app.toast.refused'), 'bad');
  else toast(t('app.fleet.removed', { name }), 'good');
  await loadFleet();
}

$('fleet-open').addEventListener('click', async () => {
  $('fleet').showModal();
  await loadFleet();
});

/** One command to every member; a toast per member that refused. */
async function broadcast(body, done) {
  let reply;

  try {
    reply = await fleetPost('/api/fleet/broadcast', body);
  } catch {
    toast(t('app.fleet.hubNoAnswer'), 'bad');
    return;
  }

  if (!reply.ok) { toast(reply.error || t('app.toast.refused'), 'bad'); return; }

  const refused = reply.results.filter((r) => !r.reply.ok);
  for (const r of refused) toast(t('app.fleet.memberRefused', { name: r.name, error: r.reply.error || t('app.toast.refused') }), 'bad');

  const count = reply.results.length - refused.length;
  toast(t('app.fleet.doneOn', { done, count }), count > 0 ? 'good' : 'bad');
  await loadFleet();
}

$('fleet-say').addEventListener('submit', async (event) => {
  event.preventDefault();
  const text = $('fleet-say-text').value.trim();
  if (!text) return;
  await broadcast({ cmd: 'say', text }, t('app.fleet.said'));
  $('fleet-say-text').value = '';
});

$('fleet-offline').addEventListener('click', async () => {
  const sure = await ask(t('app.fleet.offlineTitle'), t('app.fleet.offlineBody'));
  if (sure) await broadcast({ cmd: 'offline', enabled: true }, t('app.fleet.offline'));
});

$('fleet-online').addEventListener('click', () => broadcast({ cmd: 'offline', enabled: false }, t('app.fleet.online')));

$('fleet-end-round').addEventListener('click', async () => {
  const sure = await ask(t('app.fleet.endRoundTitle'), t('app.fleet.endRoundBody'));
  if (sure) await broadcast({ cmd: 'end-round' }, t('app.fleet.roundEnded'));
});

$('fleet-close').addEventListener('click', () => $('fleet').close());
$('fleet-refresh').addEventListener('click', loadFleet);

$('fleet-add').addEventListener('submit', async (event) => {
  event.preventDefault();

  const button = $('fleet-add-button');
  const error = $('fleet-error');

  button.disabled = true;
  button.textContent = t('app.fleet.pairing');
  error.hidden = true;

  try {
    const reply = await fleetPost('/api/fleet/add', {
      name: $('fleet-name').value.trim(),
      url: $('fleet-url').value.trim(),
      pairing: $('fleet-pairing').value.trim(),
    });

    if (!reply.ok) {
      error.textContent = reply.error || t('app.toast.refused');
      error.hidden = false;
      return;
    }

    toast(t('app.fleet.paired', { name: $('fleet-name').value.trim() }), 'good');
    $('fleet-add').reset();
    await loadFleet();
  } catch {
    error.textContent = t('app.fleet.hubNoAnswer');
    error.hidden = false;
  } finally {
    button.disabled = false;
    button.textContent = t('app.fleet.pair');
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
    $('login-error').textContent = t('app.toast.noAnswer');
    $('login-error').hidden = false;
  }
})();

