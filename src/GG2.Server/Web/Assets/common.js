/*
 * What the admin panel and the fleet analytics page both need: the element
 * lookup, two table-cell builders, the theme tokens a canvas has to resolve,
 * and the number formats.
 *
 * ⚠️ Every file here is a classic script sharing ONE global scope, so a
 * top-level name declared in two of them silently replaces the first.
 * WebAssetTests.NoTopLevelNameIsDeclaredTwice scans all of them together.
 */
'use strict';

const $ = (id) => document.getElementById(id);

function tag(text, kind) {
  const span = document.createElement('span');
  span.className = `tag ${kind}`;
  span.textContent = text;
  return span;
}

function cell(text, className) {
  const td = document.createElement('td');
  td.className = className;
  td.textContent = text;
  return td;
}

/** The chart palette. The performance card's note in app.js says why these three. */
const SERIES = {
  aqua: '#199e70',
  orange: '#d95926',
  violet: '#9085e9',

  // ⚠️ The THEME's warning, not the viz palette's near-identical amber. Late
  // ticks are the one series that means "something is wrong", and the panel
  // says that in one colour — the live pill, the map-change banner, the tile
  // notes, the log. Legal here because that chart is a single series, so no
  // adjacent-pair check applies; only contrast, which is 9.98:1 on --surface.
  warning: () => token('--warning', '#f7b750'),
};

/**
 * A theme token, read once out of the stylesheet.
 *
 * ⚠️ <canvas> cannot use a CSS custom property, so every colour drawn on one has
 * to be resolved to a string first. Reading them from :root rather than
 * repeating the hex keeps ONE source of truth — the theme block in app.css —
 * and means the map and the charts re-theme with everything else. Canvas
 * accepts oklch() directly in every browser that supports the property at all,
 * which is the same browsers this stylesheet already requires.
 */
const THEME = {};

function token(name, fallback) {
  if (THEME[name] === undefined) {
    const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    THEME[name] = value || fallback;
  }

  return THEME[name];
}

const INK_3 = () => token('--muted', '#a29ea8');
const GRID = () => token('--separator', '#222123');
const SURFACE = () => token('--surface', '#19181a');
const EDGE = () => token('--background', '#060507');

/** 1, 2 or 5 × 10ⁿ at or above `value` — so the top gridline is a round number. */
function niceCeil(value) {
  if (!(value > 0)) return 1;

  const power = 10 ** Math.floor(Math.log10(value));
  const step = value / power;

  return (step <= 1 ? 1 : step <= 2 ? 2 : step <= 5 ? 5 : 10) * power;
}

function duration(seconds) {
  const s = Math.floor(seconds);
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.floor(s / 60)}m`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`;
  return `${Math.floor(s / 86400)}d ${Math.floor((s % 86400) / 3600)}h`;
}
