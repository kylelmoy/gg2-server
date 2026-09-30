/*
 * The settings shell both editors draw into: the dashboard's Settings sheet
 * (app.js — one server's own gg2.ini) and the settings page (editor.js — the
 * fleet's common.ini and one overlay per server, served by a hub at
 * /fleet/settings and by `config edit`). One control, one field, one grouping
 * and one section rail, so the two cannot drift into two designs.
 *
 * Each editor still owns its rules: which value a field starts with, what a
 * change means, what Save sends. Nothing here knows about a file or a server.
 *
 * ⚠️ A classic script in the same global scope as common.js and whichever
 * editor loads it, so every top-level name starts with `cfg`.
 * WebAssetTests.NoTopLevelNameIsDeclaredTwice checks both pages' sets.
 */
'use strict';

/**
 * The input for one setting. A secret's `value` is whatever the operator has
 * typed so far, never the stored one — neither endpoint sends that.
 */
function cfgControl(setting, value, placeholder) {
  if (setting.kind === 'boolean') {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'cfg-check';
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
    if (setting.kind === 'list') placeholder = placeholder || t('settings.listPlaceholder');
  }

  input.value = value;
  input.placeholder = placeholder || '';
  return input;
}

/** A control's value in the file's own terms: "1"/"0" for a checkbox. */
function cfgRead(setting, input) {
  return setting.kind === 'boolean' ? (input.checked ? '1' : '0') : input.value;
}

/**
 * One field: a head row (label, tags, an optional action), the control, the
 * setting's help and any notes.
 *
 *   id       the control's element id
 *   value    what the control starts with
 *   tags     [{ text, kind: 'accent' | 'warn' | '', title }]
 *   action   { text, title, onClick } — a small button at the end of the head
 *   notes    [{ text, kind: 'warn' | '' }] — below the help
 *   classes  extra classes on the field: is-own, is-inherited
 *   onChange called with the control and the event on every input and change
 */
function cfgField(setting, opts) {
  const wrap = document.createElement('div');
  wrap.className = ['cfg-field', ...(opts.classes || [])].join(' ');
  wrap.dataset.setting = setting.id;

  const head = document.createElement('div');
  head.className = 'cfg-field-head';
  head.appendChild(Object.assign(document.createElement('label'),
    { htmlFor: opts.id, textContent: setting.key }));

  const tags = document.createElement('span');
  tags.className = 'cfg-tags';

  for (const spec of opts.tags || []) {
    tags.appendChild(Object.assign(document.createElement('span'),
      { className: `cfg-tag ${spec.kind || ''}`, textContent: spec.text, title: spec.title || '' }));
  }

  if (opts.action) {
    const button = Object.assign(document.createElement('button'),
      { type: 'button', className: 'cfg-action', textContent: opts.action.text, title: opts.action.title || '' });
    button.addEventListener('click', opts.action.onClick);
    tags.appendChild(button);
  }

  head.appendChild(tags);
  wrap.appendChild(head);

  const input = cfgControl(setting, opts.value, opts.placeholder);
  input.id = opts.id;
  input.disabled = !!opts.disabled;

  if (opts.onChange) {
    input.addEventListener('input', (event) => opts.onChange(input, event));
    input.addEventListener('change', (event) => opts.onChange(input, event));
  }

  wrap.appendChild(input);

  wrap.appendChild(Object.assign(document.createElement('p'), { className: 'hint', textContent: cfgHelp(setting) }));

  for (const n of opts.notes || []) {
    wrap.appendChild(Object.assign(document.createElement('p'), { className: `hint ${n.kind || ''}`, textContent: n.text }));
  }

  return wrap;
}

/**
 * A setting's one-line description, from strings.js by the setting's id. The
 * server sends ids, not descriptions; WebStringsTests holds strings.js to
 * exactly one entry per row of ServerSettings.All.
 */
function cfgHelp(setting) {
  return t(`settingHelp.${setting.id}`);
}

/** Whether a setting survives the filter box: its id and its help, so "password" finds all three. */
function cfgMatches(setting, filter) {
  return !filter || `${setting.id} ${cfgHelp(setting)}`.toLowerCase().includes(filter);
}

/**
 * Lays `items` ({ heading, node }) out under section headings in `body`, with
 * one rail entry per section in `rail`. Sections come in the order their first
 * item does and each holds all of its items: ServerSettings.All does not keep a
 * section's keys together, and a rail naming one section twice is a rail
 * nobody can navigate.
 *
 * Keeps the scroll position, so re-rendering after an edit does not jump; a
 * caller that changes WHAT is shown resets body.scrollTop itself.
 */
function cfgRenderGroups(body, rail, items, emptyText) {
  const top = body.scrollTop;
  body.replaceChildren();
  rail.replaceChildren();

  const lists = new Map();

  for (const item of items) {
    let list = lists.get(item.heading);

    if (!list) {
      const heading = item.heading;
      const section = document.createElement('section');
      section.className = 'sheet-group';
      section.dataset.section = heading;
      section.appendChild(Object.assign(document.createElement('h4'), { textContent: heading }));

      list = document.createElement('div');
      list.className = 'cfg-fields';
      section.appendChild(list);
      body.appendChild(section);

      const link = Object.assign(document.createElement('button'),
        { type: 'button', className: 'cfg-rail-item', textContent: heading });
      link.dataset.section = heading;
      link.addEventListener('click', () => body.scrollTo({ top: section.offsetTop, behavior: 'smooth' }));
      rail.appendChild(link);
      lists.set(heading, list);
    }

    list.appendChild(item.node);
  }

  if (items.length === 0) {
    body.appendChild(Object.assign(document.createElement('p'), { className: 'sheet-empty', textContent: emptyText }));
  }

  body.scrollTop = top;

  // A property rather than a listener, so a re-render replaces it.
  body.onscroll = () => cfgSpy(body, rail);
  cfgSpy(body, rail);
}

/** Marks the rail entry for the section at the top of the body. */
function cfgSpy(body, rail) {
  const groups = body.querySelectorAll('.sheet-group');
  if (groups.length === 0) return;

  let current = groups[0];
  const atEnd = body.scrollTop + body.clientHeight >= body.scrollHeight - 2;

  if (atEnd) {
    current = groups[groups.length - 1];
  } else {
    for (const group of groups) {
      if (group.offsetTop <= body.scrollTop + 24) current = group;
    }
  }

  for (const link of rail.children) {
    link.setAttribute('aria-current', String(link.dataset.section === current.dataset.section));
  }
}

/**
 * Marks a field as edited and not saved, and each rail entry whose section
 * holds one — the rail is how a change scrolled out of sight stays findable.
 */
function cfgMarkDirty(body, rail, settingId, dirty) {
  const wrap = body.querySelector(`.cfg-field[data-setting="${CSS.escape(settingId)}"]`);
  if (wrap) wrap.classList.toggle('is-dirty', dirty);

  for (const link of rail.children) {
    const section = body.querySelector(`.sheet-group[data-section="${CSS.escape(link.dataset.section)}"]`);
    link.classList.toggle('is-dirty', !!section && !!section.querySelector('.cfg-field.is-dirty'));
  }
}
