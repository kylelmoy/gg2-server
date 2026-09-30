'use strict';

/*
 * The settings page — a fleet hub's /fleet/settings and `gg2-server config edit`.
 *
 * Two kinds of thing are edited and they are kept apart on purpose:
 *
 *   - "All servers" is common.ini. A change here reaches every host that does
 *     not override that setting.
 *   - A host is its overlay, hosts/<dest>.ini. Typing a value different from the
 *     shared one makes it an override; "use shared" removes the override.
 *
 * The fields, the section rail and the tags are settings.js's, shared with the
 * dashboard's Settings sheet; this file is the rules.
 *
 * ⛔ A secret's value never comes back from the server. Its field starts empty,
 * and an empty secret field is never sent — or saving anything would erase the
 * admin password. FleetConfigEditor enforces the same rule on its side.
 *
 * ⚠️ Saving pushes nothing. On a hub the Push dialog sends the saved settings
 * over HTTPS; under `config edit` it names the deploy.sh command that does.
 * Either way each server applies a push the next time nobody is connected.
 */

/* The token is in the URL fragment the command printed. Moved into session
   storage and off the address bar, so a screenshot or a copied URL does not
   carry it. */
const editorToken = (() => {
  const fromHash = location.hash.slice(1);
  try {
    if (fromHash) {
      sessionStorage.setItem('gg2-editor-token', fromHash);
      history.replaceState(null, '', location.pathname);
      return fromHash;
    }
    return sessionStorage.getItem('gg2-editor-token') || '';
  } catch {
    return fromHash;
  }
})();

let model = null;
let scope = 'common';
let edits = freshEdits();

/** Changes not yet saved. Only real differences from the loaded model are kept. */
function freshEdits() {
  return { common: {}, hosts: {} };
}

function hostEdits(dest) {
  if (!edits.hosts[dest]) edits.hosts[dest] = { set: {}, clear: new Set() };
  return edits.hosts[dest];
}

function isHostDirty(dest) {
  const h = edits.hosts[dest];
  return !!h && (Object.keys(h.set).length > 0 || h.clear.size > 0);
}

function changeCount() {
  let count = Object.keys(edits.common).length;
  for (const dest of Object.keys(edits.hosts)) {
    count += Object.keys(edits.hosts[dest].set).length + edits.hosts[dest].clear.size;
  }
  return count;
}

/*
 * The same page is also a fleet hub's settings page, served at /fleet/settings.
 * There it talks to the hub's /api/fleet/config/* routes with the hub panel's
 * session cookie and CSRF token instead of a one-run token, and a 401 sends the
 * browser back to the dashboard to sign in.
 */
const onHub = location.pathname.startsWith('/fleet/');
let hubCsrf = '';

async function ensureCsrf() {
  if (hubCsrf) return;
  const session = await (await fetch('/api/session')).json();
  if (!session.authenticated) { location.href = '/'; throw new Error('signed out'); }
  hubCsrf = session.csrf || '';
}

async function api(path, options = {}) {
  if (onHub) {
    await ensureCsrf();

    const reply = await fetch(path.replace('/api/', '/api/fleet/config/'), {
      ...options,
      headers: { 'X-CSRF-Token': hubCsrf, 'Content-Type': 'application/json' },
    });

    if (reply.status === 401) { location.href = '/'; throw new Error('signed out'); }
    return reply.json();
  }

  const reply = await fetch(path, {
    ...options,
    headers: { 'X-Editor-Token': editorToken, 'Content-Type': 'application/json' },
  });
  return reply.json();
}

function toast(text, kind) {
  const node = document.createElement('div');
  node.className = `toast ${kind || ''}`;
  node.textContent = text;
  $('toasts').appendChild(node);
  setTimeout(() => {
    node.classList.add('out');
    setTimeout(() => node.remove(), 220);
  }, kind === 'bad' ? 7000 : 3400);
}

async function load() {
  let reply;
  try {
    reply = await api('/api/model');
  } catch {
    $('ed-body').replaceChildren(Object.assign(document.createElement('p'),
      { className: 'sheet-empty', textContent: t('editor.notRunning') }));
    return;
  }

  if (!reply.ok) {
    $('ed-body').innerHTML = '';
    $('ed-body').appendChild(Object.assign(document.createElement('p'),
      { className: 'sheet-empty', textContent: reply.error }));
    return;
  }

  model = reply;
  if (scope !== 'common' && !model.hosts.some((h) => h.dest === scope)) scope = 'common';
  $('ed-directory').textContent = model.directory;
  render();
}

/* ---------- the scope tabs ------------------------------------------------ */

/*
 * The dashboard's server tabs, in the same place under the top bar: the shared
 * settings first, then each server by the name the hub knows it by. A dot after
 * the name is an unsaved edit in that scope.
 */
function renderScopes() {
  const tabs = [scopeTab('common', t('editor.allServers'), t('editor.commonTabTitle'),
    Object.keys(edits.common).length > 0, false)];

  for (const host of model.hosts) {
    tabs.push(scopeTab(host.dest, host.dest, host.error || t('editor.hostTabTitle', { name: host.name, file: host.file }),
      isHostDirty(host.dest), !!host.error));
  }

  $('ed-scopes').replaceChildren(...tabs);
}

function scopeTab(id, label, title, dirty, bad) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'host-tab';
  button.classList.toggle('is-dirty', dirty);
  button.classList.toggle('is-bad', bad);
  button.title = title;
  if (scope === id) button.setAttribute('aria-current', 'page');
  button.appendChild(Object.assign(document.createElement('span'), { textContent: label }));
  button.addEventListener('click', () => {
    if (scope === id) return;
    scope = id;
    $('ed-body').scrollTop = 0;
    render();
  });
  return button;
}

/* ---------- the notes above the fields ----------------------------------- */

function renderNotes() {
  const notes = $('ed-notes');
  notes.innerHTML = '';

  const add = (text, kind) => notes.appendChild(
    Object.assign(document.createElement('p'), { className: `cfg-note ${kind}`, textContent: text }));

  if (!model.commonExists && onHub) {
    add(t('editor.notes.noFleetSettings'), 'warn');
  } else if (!model.commonExists) {
    add(t('editor.notes.noCommonIni', {
      path: `${model.directory}${model.directory.includes('\\') ? '\\' : '/'}common.ini`,
    }), 'warn');
  }

  if (model.commonError) add(t('editor.notes.commonError', { error: model.commonError }), 'bad');

  if (scope === 'common') {
    for (const warning of model.warnings) add(warning, 'warn');
  } else {
    const host = model.hosts.find((h) => h.dest === scope);
    if (host && host.error) add(host.error, 'bad');
  }
}

/* ---------- the fields ---------------------------------------------------- */

function sharedValue(setting) {
  return Object.prototype.hasOwnProperty.call(edits.common, setting.id)
    ? edits.common[setting.id]
    : setting.common.value;
}

/** What a host row shows and whether it is the host's own value. */
function hostRow(setting, dest) {
  const h = edits.hosts[dest];
  const original = setting.hosts[dest];
  const cleared = !!h && h.clear.has(setting.id);
  const pending = !!h && Object.prototype.hasOwnProperty.call(h.set, setting.id);
  const standing = original.overridden && !cleared;

  return {
    own: pending || standing,
    standing,
    value: pending ? h.set[setting.id] : standing ? original.effective.value : sharedValue(setting),
    baseline: standing ? original.effective.value : sharedValue(setting),
  };
}

/** Whether a setting has an unsaved edit in the scope on screen. */
function isDirty(setting) {
  if (scope === 'common') return Object.prototype.hasOwnProperty.call(edits.common, setting.id);
  const h = edits.hosts[scope];
  return !!h && (Object.prototype.hasOwnProperty.call(h.set, setting.id) || h.clear.has(setting.id));
}

function markDirty(setting) {
  cfgMarkDirty($('ed-body'), $('ed-rail'), setting.id, isDirty(setting));
}

function render() {
  renderScopes();
  renderNotes();

  const isHost = scope !== 'common';
  const host = isHost ? model.hosts.find((h) => h.dest === scope) : null;

  $('ed-title').textContent = isHost ? host.dest : t('editor.allServers');
  $('ed-file').textContent = isHost
    ? (host.exists
      ? t('editor.hostFile', { name: host.name, file: host.file })
      : t('editor.hostFileNotCreated', { name: host.name, file: host.file }))
    : t('editor.commonFile');
  $('ed-preview').hidden = !isHost;
  $('ed-adopt').hidden = !isHost || !onHub;
  $('ed-only-wrap').hidden = !isHost;

  const filter = $('ed-filter').value.trim().toLowerCase();
  const onlyOwn = isHost && $('ed-only').checked;

  // On a host, the two settings that normally differ come first, in a group of
  // their own, so the common case is the first thing on the page.
  const first = isHost ? ['Server.ServerName', 'Server.WelcomeMessage'] : [];
  const ordered = [
    ...first.map((id) => model.settings.find((s) => s.id === id)).filter(Boolean),
    ...model.settings.filter((s) => !first.includes(s.id)),
  ];

  const items = ordered
    .filter((setting) => cfgMatches(setting, filter))
    .filter((setting) => !onlyOwn || hostRow(setting, scope).own)
    .map((setting) => ({
      heading: first.includes(setting.id) ? t('editor.thisServerGroup') : setting.section,
      node: isHost ? hostField(setting, scope) : commonField(setting),
    }));

  cfgRenderGroups($('ed-body'), $('ed-rail'), items,
    onlyOwn ? t('editor.noOverridesMatch') : t('editor.noMatch'));

  for (const setting of model.settings) markDirty(setting);
  renderStatus();
}

function commonField(setting) {
  const value = Object.prototype.hasOwnProperty.call(edits.common, setting.id)
    ? edits.common[setting.id]
    : setting.secret ? '' : setting.common.value;

  const overriding = model.hosts.filter((h) => hostRow(setting, h.dest).own);
  const names = overriding.map((h) => h.dest).join(', ');

  return cfgField(setting, {
    id: `f:${setting.id}`,
    value,
    placeholder: setting.secret
      ? (setting.common.set ? t('editor.placeholder.secretSet') : t('editor.placeholder.secretNotSet'))
      : setting.common.resolved ? t('editor.placeholder.commonResolved') : '',
    tags: overriding.length > 0
      ? [{ text: t('editor.overrides', { count: overriding.length }), title: t('editor.overriddenOn', { names }) }]
      : [],
    notes: overriding.length > 0
      ? [{ text: t('editor.overriddenNote', { count: overriding.length, names }) }]
      : [],
    onChange: (input) => {
      const typed = cfgRead(setting, input);
      const baseline = setting.secret ? '' : setting.common.value;
      if (typed === baseline) delete edits.common[setting.id];
      else edits.common[setting.id] = typed;
      markDirty(setting);
      renderScopes();
      renderStatus();
    },
  });
}

function hostField(setting, dest) {
  const row = hostRow(setting, dest);

  let placeholder = setting.hosts[dest].effective.resolved ? t('editor.placeholder.hostResolved') : '';
  if (setting.secret) {
    const effective = setting.hosts[dest].effective.set;
    placeholder = row.own
      ? (effective ? t('editor.placeholder.hostSecretSet') : t('editor.placeholder.hostSecretType'))
      : (setting.common.set ? t('editor.placeholder.sharedSecretSet') : t('editor.placeholder.sharedSecretNotSet'));
  }

  const field = cfgField(setting, {
    id: `f:${setting.id}`,
    value: setting.secret ? (row.own && edits.hosts[dest]?.set[setting.id]) || '' : row.value,
    placeholder,
    classes: [row.own ? 'is-own' : 'is-inherited'],
    tags: row.own ? [] : [{ text: t('editor.sharedTag'), title: t('editor.sharedTagTitle') }],
    action: row.own
      ? {
        text: t('editor.useShared'),
        title: t('editor.useSharedTitle'),
        onClick: () => {
          const h = hostEdits(dest);
          delete h.set[setting.id];
          if (setting.hosts[dest].overridden) h.clear.add(setting.id);
          render();
        },
      }
      : null,
    onChange: (input, event) => {
      const h = hostEdits(dest);
      const typed = cfgRead(setting, input);
      const baseline = setting.secret ? '' : row.baseline;

      if (typed === baseline) {
        delete h.set[setting.id];
      } else {
        h.set[setting.id] = typed;
        h.clear.delete(setting.id);
      }

      // Redrawn on a committed change, so the tag becomes "use shared" and back;
      // while typing only the colour follows, or the field would lose focus.
      if (event.type === 'change') { render(); return; }

      const own = hostRow(setting, dest).own;
      field.classList.toggle('is-own', own);
      field.classList.toggle('is-inherited', !own);
      markDirty(setting);
      renderScopes();
      renderStatus();
    },
  });

  return field;
}

function renderStatus() {
  const count = changeCount();
  $('ed-save').disabled = count === 0;
  $('ed-revert').disabled = count === 0;
  $('ed-status').textContent = count === 0 ? t('editor.status.clean') : t('editor.status.dirty', { count });
}

/* ---------- save, revert, preview ----------------------------------------- */

async function save() {
  const payload = { common: edits.common, hosts: {} };

  for (const [dest, h] of Object.entries(edits.hosts)) {
    if (Object.keys(h.set).length === 0 && h.clear.size === 0) continue;
    payload.hosts[dest] = { set: h.set, clear: [...h.clear] };
  }

  $('ed-save').disabled = true;
  let reply;

  try {
    reply = await api('/api/save', { method: 'POST', body: JSON.stringify(payload) });
  } catch {
    toast(t('editor.toast.saveNoAnswer'), 'bad');
    renderStatus();
    return;
  }

  if (!reply.ok) {
    toast(t('editor.toast.notSaved', { error: reply.error }), 'bad');
    renderStatus();
    return;
  }

  toast(reply.written.length === 0 ? t('editor.toast.nothingToWrite') : t('editor.toast.saved', { files: reply.written.join(', ') }), 'good');
  edits = freshEdits();
  await load();
}

async function preview() {
  const host = model.hosts.find((h) => h.dest === scope);
  if (!host) return;

  const reply = await api(`/api/preview?host=${encodeURIComponent(host.dest)}`);
  $('ed-preview-title').textContent = t('editor.preview.title', { host: host.dest });

  if (!reply.ok) {
    $('ed-preview-sub').textContent = host.dest;
    $('ed-preview-text').textContent = reply.error;
  } else {
    $('ed-preview-sub').textContent = t('editor.preview.sub', { host: host.dest, sha256: reply.sha256 });
    $('ed-preview-text').textContent = reply.text;
  }

  $('ed-preview-dialog').showModal();
}

/* ---------- push (hub only) ----------------------------------------------- */

/*
 * Check asks every member for a DRY RUN of config-install: the member renders
 * what it would write — with its own host-local values kept — and answers with
 * the masked diff and the hash of its current file. Push then sends that hash
 * back as `expected`, so a file that changed in between is refused rather than
 * overwritten. A member whose file was never pushed, or was edited since, needs
 * an explicit overwrite.
 */
let pushChecks = [];

async function hubPost(path, body) {
  await ensureCsrf();
  const reply = await fetch(path, {
    method: 'POST',
    headers: { 'X-CSRF-Token': hubCsrf, 'Content-Type': 'application/json' },
    body: JSON.stringify(body || {}),
  });
  if (reply.status === 401) { location.href = '/'; throw new Error('signed out'); }
  return reply.json();
}

async function openPush() {
  $('ed-push-unsaved').hidden = changeCount() === 0;
  $('ed-push-dialog').showModal();
  if (onHub) await checkPush();
}

async function checkPush() {
  const list = $('ed-push-list');
  list.replaceChildren(pushMessage(t('editor.push.asking'), ''));

  let reply;
  try {
    reply = await hubPost('/api/fleet/push/check');
  } catch {
    list.replaceChildren(pushMessage(t('editor.push.noAnswer'), 'warn'));
    return;
  }

  pushChecks = reply.members || [];
  renderPush();
}

function pushMessage(text, kind) {
  const tr = document.createElement('tr');
  const td = cell('', '');
  td.colSpan = 3;
  td.appendChild(Object.assign(document.createElement('p'), { className: `hint ${kind}`, textContent: text }));
  tr.appendChild(td);
  return tr;
}

function renderPush() {
  const rows = pushChecks.map((m) => {
    const tr = document.createElement('tr');
    tr.appendChild(cell(m.name, 'ed-push-name'));

    const noteCell = cell('', '');
    const note = document.createElement('p');
    noteCell.appendChild(note);
    tr.appendChild(noteCell);

    const toolsCell = cell('', '');
    const tools = document.createElement('div');
    tools.className = 'btn-row';
    toolsCell.appendChild(tools);
    tr.appendChild(toolsCell);

    const c = m.check;

    if (!c.ok) {
      note.className = 'hint warn';
      note.textContent = c.error;
      tools.appendChild(adoptButton(m.name));
      return tr;
    }

    const changes = c.diff.filter((d) => d.op === '+' || d.op === '-').length;

    if (c.unchanged && c.managed && !c.editedSincePush) {
      note.className = 'hint';
      note.textContent = t('editor.push.upToDate');
      tools.appendChild(adoptButton(m.name));
      return tr;
    }

    const needsForce = !c.managed || c.editedSincePush;
    note.className = needsForce ? 'hint warn' : 'hint';
    note.textContent = c.unchanged
      ? t('editor.push.sameSettings')
      : !c.managed ? t('editor.push.changesNeverPushed', { count: changes })
        : c.editedSincePush ? t('editor.push.changesEdited', { count: changes })
          : t('editor.push.changes', { count: changes });

    const diff = document.createElement('button');
    diff.type = 'button';
    diff.className = 'btn btn-quiet';
    diff.textContent = t('editor.push.diff');
    diff.addEventListener('click', () => showDiff(m));

    const push = document.createElement('button');
    push.type = 'button';
    push.className = needsForce ? 'btn btn-warn' : 'btn btn-primary';
    push.textContent = needsForce ? t('editor.push.overwrite') : t('editor.push.push');
    push.addEventListener('click', () => doPush(m, needsForce, false));

    const now = document.createElement('button');
    now.type = 'button';
    now.className = 'btn btn-quiet btn-warn';
    now.textContent = t('editor.push.pushRestart');
    now.title = t('editor.push.pushRestartTitle');
    now.addEventListener('click', () => doPush(m, needsForce, true));

    tools.append(diff, adoptButton(m.name), push, now);
    return tr;
  });

  $('ed-push-list').replaceChildren(...(rows.length ? rows : [pushMessage(t('editor.push.noMembers'), '')]));
}

let adopting = null;

function adoptButton(name) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'btn btn-quiet';
  button.textContent = t('editor.adopt.button');
  button.title = t('editor.adopt.buttonTitle');
  button.addEventListener('click', () => openAdopt(name));
  return button;
}

async function openAdopt(name) {
  const reply = await hubPost('/api/fleet/adopt', { name });

  if (!reply.ok) { toast(t('editor.toast.hostError', { name, error: reply.error }), 'bad'); return; }

  adopting = name;
  $('ed-adopt-title').textContent = t('editor.adopt.title', { name });
  $('ed-adopt-sub').textContent = reply.differences.length === 0
    ? t('editor.adopt.nothingDiffers')
    : t('editor.adopt.differ', { count: reply.differences.length });

  const body = $('ed-adopt-body');
  body.replaceChildren();

  for (const d of reply.differences) {
    const label = document.createElement('label');
    label.className = 'ed-adopt-row';

    const box = Object.assign(document.createElement('input'), { type: 'checkbox', checked: true, value: d.id });
    const id = Object.assign(document.createElement('strong'), { textContent: d.id });
    const values = Object.assign(document.createElement('span'), {
      className: 'hint',
      textContent: t('editor.adopt.values', {
        fleet: d.fleet === '' ? t('editor.adopt.empty') : d.fleet,
        member: d.member === '' ? t('editor.adopt.empty') : d.member,
      }),
    });

    label.append(box, id, values);
    body.appendChild(label);
  }

  $('ed-adopt-secrets').textContent = reply.secrets.length
    ? t('editor.adopt.secrets', { names: reply.secrets.join(', ') })
    : '';

  $('ed-adopt-common').disabled = reply.differences.length === 0;
  $('ed-adopt-host').disabled = reply.differences.length === 0;
  $('ed-adopt-dialog').showModal();
}

async function adopt(into) {
  if (changeCount() > 0) { toast(t('editor.toast.unsavedFirst'), 'bad'); return; }

  const ids = [...$('ed-adopt-body').querySelectorAll('input:checked')].map((box) => box.value);
  if (ids.length === 0) { toast(t('editor.toast.nothingTicked'), 'bad'); return; }

  const reply = await hubPost('/api/fleet/adopt', { name: adopting, ids, into });

  if (!reply.ok) { toast(t('editor.toast.notAdopted', { error: reply.error }), 'bad'); return; }

  toast(t('editor.toast.adopted', { count: ids.length, name: adopting }), 'good');
  $('ed-adopt-dialog').close();
  await load();
  if ($('ed-push-dialog').open) await checkPush();
}

function showDiff(m) {
  $('ed-preview-title').textContent = t('editor.diff.title', { name: m.name });
  $('ed-preview-sub').textContent = t('editor.diff.sub', { now: m.check.current.slice(0, 12), after: m.check.next.slice(0, 12) });
  $('ed-preview-text').textContent = m.check.diff
    .map((d) => (d.op === '…' ? '…' : `${d.op} ${d.text}`))
    .join('\n') || t('editor.diff.none');
  $('ed-preview-dialog').showModal();
}

async function doPush(m, force, restart) {
  const warning = restart
    ? t('editor.confirm.pushRestart', { name: m.name })
    : force
      ? t('editor.confirm.overwrite', { name: m.name })
      : null;

  if (warning && !window.confirm(warning)) return;

  const reply = await hubPost('/api/fleet/push', { name: m.name, expected: m.check.current, force, restart });

  if (!reply.ok) toast(t('editor.toast.hostError', { name: m.name, error: reply.error }), 'bad');
  else if (reply.restarted) toast(t('editor.toast.pushedRestarting', { name: m.name }), 'good');
  else toast(t('editor.toast.pushed', { name: m.name }), 'good');

  await checkPush();
}

/* ---------- wiring --------------------------------------------------------- */

if (onHub) {
  document.title = t('editor.hubPageTitle');
  $('ed-brand').textContent = t('editor.hubBrand');
  $('ed-back').hidden = false;
}

$('ed-hub').hidden = !onHub;
$('ed-deploy').hidden = onHub;
$('ed-push-check').hidden = !onHub;

$('ed-save').addEventListener('click', save);
$('ed-revert').addEventListener('click', () => { edits = freshEdits(); render(); });
$('ed-filter').addEventListener('input', () => { $('ed-body').scrollTop = 0; render(); });
$('ed-only').addEventListener('change', () => { $('ed-body').scrollTop = 0; render(); });
$('ed-preview').addEventListener('click', preview);
$('ed-preview-close').addEventListener('click', () => $('ed-preview-dialog').close());
$('ed-adopt').addEventListener('click', () => openAdopt(scope));
$('ed-adopt-close').addEventListener('click', () => $('ed-adopt-dialog').close());
$('ed-adopt-common').addEventListener('click', () => adopt('common'));
$('ed-adopt-host').addEventListener('click', () => adopt('host'));
$('ed-push-open').addEventListener('click', openPush);
$('ed-push-check').addEventListener('click', checkPush);
$('ed-push-close').addEventListener('click', () => $('ed-push-dialog').close());

window.addEventListener('beforeunload', (event) => {
  if (changeCount() > 0) event.preventDefault();
});

document.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key === 's') {
    event.preventDefault();
    if (changeCount() > 0) save();
  }
});

load();
