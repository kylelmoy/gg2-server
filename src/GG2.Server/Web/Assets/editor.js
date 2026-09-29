'use strict';

/*
 * gg2-server config edit — the page.
 *
 * Two kinds of thing are edited and they are kept apart on purpose:
 *
 *   - "All servers" is common.ini. A change here reaches every host that does
 *     not override that setting.
 *   - A host is its overlay, hosts/<dest>.ini. Typing a value different from the
 *     shared one makes it an override; "use shared" removes the override.
 *
 * ⛔ A secret's value never comes back from the server. Its field starts empty,
 * and an empty secret field is never sent — or saving anything would erase the
 * admin password. FleetConfigEditor enforces the same rule on its side.
 *
 * ⚠️ Nothing here pushes. The page writes files on this machine; the sidebar
 * names the deploy.sh command that sends them, and each server applies a push
 * the next time nobody is connected.
 */

const $ = (id) => document.getElementById(id);

/* The token is in the URL fragment the command printed. Moved into session
   storage and off the address bar, so a screenshot or a copied URL does not
   carry it. */
const token = (() => {
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

async function api(path, options = {}) {
  const reply = await fetch(path, {
    ...options,
    headers: { 'X-Editor-Token': token, 'Content-Type': 'application/json' },
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
    $('ed-body').innerHTML = '<p class="sheet-empty">The editor is not running any more.</p>';
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

/* ---------- the sidebar -------------------------------------------------- */

function renderScopes() {
  const nav = $('ed-scopes');
  nav.innerHTML = '';

  nav.appendChild(scopeButton('common', 'All servers', 'common.ini', Object.keys(edits.common).length > 0, false));

  nav.appendChild(Object.assign(document.createElement('p'),
    { className: 'ed-scopes-label', textContent: 'Servers' }));

  for (const host of model.hosts) {
    nav.appendChild(scopeButton(host.dest, host.name, host.dest, isHostDirty(host.dest), !!host.error));
  }
}

function scopeButton(id, title, sub, dirty, bad) {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'ed-scope';
  button.classList.toggle('is-dirty', dirty);
  button.classList.toggle('is-bad', bad);
  button.setAttribute('aria-current', String(scope === id));
  button.append(
    Object.assign(document.createElement('strong'), { textContent: title }),
    Object.assign(document.createElement('span'), { textContent: sub }));
  button.addEventListener('click', () => { scope = id; render(); });
  return button;
}

/* ---------- the notes above the fields ----------------------------------- */

function renderNotes() {
  const notes = $('ed-notes');
  notes.innerHTML = '';

  const add = (text, kind) => notes.appendChild(
    Object.assign(document.createElement('p'), { className: `ed-note ${kind}`, textContent: text }));

  if (!model.commonExists) {
    add(`There is no common.ini yet, so every value below is a default and Save creates the file. `
      + `To start from a server's current configuration instead, copy its /opt/gg2-server/shared/gg2.ini `
      + `to ${model.directory}${model.directory.includes('\\') ? '\\' : '/'}common.ini and reload.`, 'warn');
  }

  if (model.commonError) add(`common.ini: ${model.commonError}`, 'bad');

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

function render() {
  renderScopes();
  renderNotes();

  const isHost = scope !== 'common';
  const host = isHost ? model.hosts.find((h) => h.dest === scope) : null;

  $('ed-title').textContent = isHost ? host.name : 'All servers';
  $('ed-file').textContent = isHost
    ? `${host.dest} — ${host.file}${host.exists ? '' : ' (not created yet)'} — wins over common.ini`
    : `common.ini — every server, unless a server overrides the setting`;
  $('ed-preview').hidden = !isHost;
  $('ed-only-wrap').hidden = !isHost;

  const filter = $('ed-filter').value.trim().toLowerCase();
  const onlyOwn = isHost && $('ed-only').checked;
  const body = $('ed-body');
  body.innerHTML = '';

  // On a host, the two settings that normally differ come first, in a group of
  // their own, so the common case is the first thing on the page.
  const first = isHost ? ['Server.ServerName', 'Server.WelcomeMessage'] : [];
  const ordered = [
    ...first.map((id) => model.settings.find((s) => s.id === id)).filter(Boolean),
    ...model.settings.filter((s) => !first.includes(s.id)),
  ];

  let group = null;
  let list = null;
  let shown = 0;

  for (const setting of ordered) {
    if (filter && !`${setting.id} ${setting.help}`.toLowerCase().includes(filter)) continue;
    if (onlyOwn && !hostRow(setting, scope).own) continue;

    const heading = first.includes(setting.id) ? 'This server' : setting.section;

    if (heading !== group) {
      group = heading;
      const section = document.createElement('section');
      section.className = 'sheet-group';
      section.appendChild(Object.assign(document.createElement('h4'), { textContent: heading }));
      list = document.createElement('div');
      list.className = 'sheet-fields';
      section.appendChild(list);
      body.appendChild(section);
    }

    list.appendChild(isHost ? hostField(setting, scope) : commonField(setting));
    shown++;
  }

  if (shown === 0) {
    body.appendChild(Object.assign(document.createElement('p'), {
      className: 'sheet-empty',
      textContent: onlyOwn ? 'This server overrides nothing that matches.' : 'Nothing matches that filter.',
    }));
  }

  renderStatus();
}

function commonField(setting) {
  const wrap = document.createElement('div');
  wrap.className = 'sheet-field';

  const head = document.createElement('div');
  head.className = 'ed-row-head';
  head.appendChild(Object.assign(document.createElement('label'),
    { htmlFor: `f:${setting.id}`, textContent: setting.key }));
  wrap.appendChild(head);

  const value = Object.prototype.hasOwnProperty.call(edits.common, setting.id)
    ? edits.common[setting.id]
    : setting.secret ? '' : setting.common.value;

  const input = control(setting, value, setting.secret
    ? (setting.common.set ? 'set — type to replace' : 'not set')
    : setting.common.resolved ? 'default — each server resolves its own' : '');
  input.id = `f:${setting.id}`;

  const onChange = () => {
    const typed = read(setting, input);
    const baseline = setting.secret ? '' : setting.common.value;
    if (typed === baseline) delete edits.common[setting.id];
    else edits.common[setting.id] = typed;
    renderScopes();
    renderStatus();
  };

  input.addEventListener('input', onChange);
  input.addEventListener('change', onChange);
  wrap.appendChild(input);

  if (setting.help) wrap.appendChild(Object.assign(document.createElement('p'), { className: 'hint', textContent: setting.help }));

  const overriding = model.hosts.filter((h) => hostRow(setting, h.dest).own);
  if (overriding.length > 0) {
    wrap.appendChild(Object.assign(document.createElement('p'), {
      className: 'ed-where',
      textContent: `Overridden on ${overriding.map((h) => h.name).join(', ')} — a change here does not reach ${overriding.length === 1 ? 'it' : 'them'}.`,
    }));
  }

  return wrap;
}

function hostField(setting, dest) {
  const row = hostRow(setting, dest);
  const wrap = document.createElement('div');
  wrap.className = 'sheet-field';
  wrap.classList.add(row.own ? 'is-host' : 'is-inherited');

  const head = document.createElement('div');
  head.className = 'ed-row-head';
  head.appendChild(Object.assign(document.createElement('label'),
    { htmlFor: `f:${setting.id}`, textContent: setting.key }));

  if (row.own) {
    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'ed-override';
    reset.textContent = 'use shared';
    reset.title = 'Remove this server’s own value; it goes back to common.ini’s.';
    reset.addEventListener('click', () => {
      const h = hostEdits(dest);
      delete h.set[setting.id];
      if (setting.hosts[dest].overridden) h.clear.add(setting.id);
      render();
    });
    head.appendChild(reset);
  } else {
    head.appendChild(Object.assign(document.createElement('span'), { className: 'ed-where', textContent: 'shared' }));
  }

  wrap.appendChild(head);

  let placeholder = setting.hosts[dest].effective.resolved ? 'default — this server resolves its own' : '';
  if (setting.secret) {
    const effective = setting.hosts[dest].effective.set;
    placeholder = row.own
      ? (effective ? 'set on this server — type to replace' : 'type to set on this server')
      : (setting.common.set ? 'shared value set — type to override' : 'not set — type to set here');
  }

  const input = control(setting, setting.secret ? (row.own && edits.hosts[dest]?.set[setting.id]) || '' : row.value, placeholder);
  input.id = `f:${setting.id}`;

  const onChange = () => {
    const h = hostEdits(dest);
    const typed = read(setting, input);
    const baseline = setting.secret ? '' : row.baseline;

    if (typed === baseline) {
      delete h.set[setting.id];
    } else {
      h.set[setting.id] = typed;
      h.clear.delete(setting.id);
    }

    const own = hostRow(setting, dest).own;
    wrap.classList.toggle('is-host', own);
    wrap.classList.toggle('is-inherited', !own);
    renderScopes();
    renderStatus();
  };

  input.addEventListener('input', onChange);
  input.addEventListener('change', () => { onChange(); render(); });
  wrap.appendChild(input);

  if (setting.help) wrap.appendChild(Object.assign(document.createElement('p'), { className: 'hint', textContent: setting.help }));

  return wrap;
}

function control(setting, value, placeholder) {
  if (setting.kind === 'boolean') {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'sheet-check';
    box.checked = value === '1';
    return box;
  }

  if (setting.kind === 'choice') {
    const select = document.createElement('select');
    for (const choice of setting.choices || []) {
      select.appendChild(Object.assign(document.createElement('option'),
        { value: choice, textContent: choice, selected: choice === value }));
    }
    return select;
  }

  const input = document.createElement('input');
  input.spellcheck = false;

  if (setting.secret) {
    input.type = 'password';
    input.autocomplete = 'new-password';
  } else {
    input.type = setting.kind === 'integer' ? 'number' : 'text';
    if (setting.kind === 'list') placeholder = placeholder || 'comma,separated';
  }

  input.value = value;
  input.placeholder = placeholder;
  return input;
}

function read(setting, input) {
  return setting.kind === 'boolean' ? (input.checked ? '1' : '0') : input.value;
}

function renderStatus() {
  const count = changeCount();
  $('ed-save').disabled = count === 0;
  $('ed-revert').disabled = count === 0;
  $('ed-status').textContent = count === 0
    ? 'No unsaved changes.'
    : `${count} unsaved change${count === 1 ? '' : 's'}. Saving checks every server's file before writing any.`;
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
    toast('The editor did not answer. Nothing was saved.', 'bad');
    renderStatus();
    return;
  }

  if (!reply.ok) {
    toast(`Not saved: ${reply.error}`, 'bad');
    renderStatus();
    return;
  }

  toast(reply.written.length === 0 ? 'Nothing to write' : `Saved ${reply.written.join(', ')}`, 'good');
  edits = freshEdits();
  await load();
}

async function preview() {
  const host = model.hosts.find((h) => h.dest === scope);
  if (!host) return;

  const reply = await api(`/api/preview?host=${encodeURIComponent(host.dest)}`);
  $('ed-preview-title').textContent = `${host.name} — gg2.ini`;

  if (!reply.ok) {
    $('ed-preview-sub').textContent = host.dest;
    $('ed-preview-text').textContent = reply.error;
  } else {
    $('ed-preview-sub').textContent = `${host.dest} — sha256 ${reply.sha256}`;
    $('ed-preview-text').textContent = reply.text;
  }

  $('ed-preview-dialog').showModal();
}

$('ed-save').addEventListener('click', save);
$('ed-revert').addEventListener('click', () => { edits = freshEdits(); render(); });
$('ed-filter').addEventListener('input', render);
$('ed-only').addEventListener('change', render);
$('ed-preview').addEventListener('click', preview);
$('ed-preview-close').addEventListener('click', () => $('ed-preview-dialog').close());

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
