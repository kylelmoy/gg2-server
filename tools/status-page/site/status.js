// gg2server.net status page. Reads /status.json, which gg2-status.py rewrites
// every 30 seconds; this page only renders it.
//
// Server names and maps come from whoever announces to the lobby, so every
// string from status.json goes through esc() before it reaches innerHTML.
"use strict";

const REFRESH_MS = 15000;
const $ = (id) => document.getElementById(id);
const esc = (v) => String(v ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const STATE_WORD = { up: "Up", degraded: "Partial", down: "Down", unknown: "Unknown" };

let last = null;

function ago(seconds) {
  if (seconds < 60) return `${Math.max(0, Math.round(seconds))}s ago`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  return `${(seconds / 3600).toFixed(1)} h ago`;
}

function duration(seconds) {
  const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600);
  return d > 0 ? `${d}d ${h}h` : `${h}h ${Math.floor((seconds % 3600) / 60)}m`;
}

function pct(fraction) {
  if (fraction == null) return "—";
  const p = fraction * 100;
  return (p >= 99.995 ? "100" : p.toFixed(2)) + "%";
}

function segClass(v) {
  if (v == null) return "";
  if (v >= 0.999) return "up";
  if (v > 0) return "degraded";
  return "down";
}

// Bucket i of n covers [end - (n-i)*size, end - (n-i-1)*size).
function bucketLabel(generated, i, n, size) {
  const end = Date.parse(generated) - (n - 1 - i) * size * 1000;
  const fmt = (t) => new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return `${fmt(end - size * 1000)}–${fmt(end)}`;
}

function renderBanner(s) {
  const counts = { up: 0, degraded: 0, down: 0 };
  for (const c of s.checks) if (c.state in counts) counts[c.state]++;
  const problems = s.checks.filter((c) => c.state === "down" || c.state === "degraded");
  const banner = $("banner");
  banner.dataset.state = s.overall;
  $("banner-text").textContent =
    s.overall === "up" ? "All systems operational" : s.overall === "degraded" ? "Partial outage" : "Major outage";
  $("banner-sub").textContent = problems.length
    ? problems.map((c) => `${c.name}: ${c.detail || STATE_WORD[c.state]}`).join(" · ")
    : `${counts.up} checks passing`;
}

function renderTiles(s) {
  const lobby = s.lobby;
  $("t-humans").textContent = lobby.humans;
  $("t-slots").textContent = `/ ${lobby.slots} slots`;
  $("t-servers").textContent = lobby.servers.length;

  const live = s.rooms.filter((r) => r.maxRooms != null);
  const used = live.reduce((a, r) => a + (r.rooms || 0), 0);
  const max = live.reduce((a, r) => a + (r.maxRooms || 0), 0);
  $("t-rooms").textContent = used;
  $("t-rooms-max").textContent = `/ ${max} capacity`;
  const f = max ? used / max : 0;
  const bar = $("t-rooms-bar");
  bar.style.width = `${(f * 100).toFixed(1)}%`;
  bar.className = f >= 0.9 ? "high" : f >= 0.6 ? "mid" : "";

  const holders = s.awards.holders;
  $("t-awards").textContent = s.awards.state === "up" ? holders.length : "—";
}

function renderServices(s) {
  const n = s.checks[0]?.history.length ?? 48;
  let html = "", group = null;
  for (const c of s.checks) {
    if (c.group !== group) {
      group = c.group;
      html += `<div class="group-head">${esc(group)}</div>`;
    }
    const segs = c.history.map((v, i) =>
      `<span class="seg ${segClass(v)}" data-tip="${esc(bucketLabel(s.generated, i, n, s.bucketSeconds))} · ${v == null ? "no data" : pct(v) + " up"}"></span>`).join("");
    html += `<div class="svc">
      <span class="dot ${esc(c.state)}"></span>
      <div class="svc-name"><b>${esc(c.name)}</b><span>${esc(c.sub)}</span></div>
      <div class="svc-bar"><div class="segs">${segs}</div>
        <div class="svc-meta"><span class="detail">${esc(c.detail || "")}</span><span class="num">${pct(c.uptime)} · 24 h</span></div></div>
      <div class="svc-right"><span class="state ${esc(c.state)}">${STATE_WORD[c.state] || esc(c.state)}</span><span class="ms">${c.ms != null ? Math.round(c.ms) + " ms" : ""}</span></div>
    </div>`;
  }
  $("services").innerHTML = html;
}

function renderLobby(s) {
  const servers = [...s.lobby.servers].sort((a, b) => (b.players - a.players) || String(a.name).localeCompare(b.name));
  $("lobby-empty").hidden = servers.length > 0;
  $("lobby").innerHTML = servers.map((v) => `<tr>
    <td class="srv"><b>${esc(v.name)}</b>${v.passworded ? '<span class="tag warn">password</span>' : ""}${v.native ? '<span class="tag accent">native</span>' : ""}
      <span class="addr">${esc(v.ip)}:${esc(v.port)}</span></td>
    <td class="map">${esc(v.map)}</td>
    <td class="c-num"><span class="${v.players > 0 ? "has-humans" : ""}">${esc(v.players)}</span> / ${esc(v.slots)}</td>
    <td class="c-num">${esc(v.bots ?? 0)}</td>
    <td class="c-ver">${esc(v.game || "")} ${esc(v.version || "")}</td>
  </tr>`).join("");
}

function renderSpark(s) {
  const h = s.humansHistory;
  const peak = Math.max(1, ...h.filter((v) => v != null));
  const seen = h.filter((v) => v != null);
  $("spark-peak").textContent = seen.length ? `peak ${Math.max(...seen)}` : "";
  $("spark").innerHTML = h.map((v, i) => {
    const label = `${bucketLabel(s.generated, i, h.length, s.bucketSeconds)} · ${v == null ? "no data" : `peak ${v} player${v === 1 ? "" : "s"}`}`;
    if (v == null) return `<span class="bar none" style="height:1px" data-tip="${esc(label)}"></span>`;
    return `<span class="bar ${v === 0 ? "zero" : ""}" style="height:${v === 0 ? 2 : Math.max(4, (v / peak) * 64)}px" data-tip="${esc(label)}"></span>`;
  }).join("");
}

function renderRooms(s) {
  $("rooms").innerHTML = s.rooms.map((r) => {
    const up = r.maxRooms != null;
    const f = up && r.maxRooms ? r.rooms / r.maxRooms : 0;
    return `<div class="row">
      <span class="avatar">${esc(r.prefix)}</span>
      <div class="row-main"><b>${esc(r.region)}</b>${r.draining ? '<span class="tag warn">draining</span>' : ""}
        <span class="sub">${esc(r.hostname)}${r.ms != null ? ` · ${Math.round(r.ms)} ms` : ""}</span>
        ${up ? `<div class="meter"><span class="${f >= 0.9 ? "high" : f >= 0.6 ? "mid" : ""}" style="width:${(f * 100).toFixed(1)}%"></span></div>` : ""}</div>
      <div class="row-num">${up ? `${esc(r.rooms)}<i> / ${esc(r.maxRooms)}</i>` : `<span class="state down">Down</span>`}</div>
    </div>`;
  }).join("") || '<p class="empty">No private room hosts in the directory.</p>';
}

function renderAwards(s) {
  const a = s.awards;
  $("auth-endpoint").textContent = a.endpoint || "";
  if (a.state !== "up") {
    $("awards").innerHTML = `<p class="empty">The award table could not be read.</p>`;
    return;
  }
  if (!a.holders.length) {
    $("awards").innerHTML = `<p class="empty">No rewards have been issued here yet.</p>`;
    return;
  }
  $("awards").innerHTML = a.holders.map((h) => `<div class="row">
    <span class="avatar">${esc(h.id)}</span>
    <div class="row-main">${h.note ? `<b>${esc(h.note)}</b><br>` : ""}${h.rewards.length
      ? h.rewards.map((r) => `<span class="tag reward">${esc(r)}</span>`).join("")
      : '<span class="sub">no rewards enabled</span>'}</div>
  </div>`).join("");
}

function renderCerts(s) {
  $("certs").innerHTML = s.certificates.map((c) => `<div class="row">
    <span class="dot ${esc(c.state)}"></span>
    <div class="row-main"><b class="mono">${esc(c.host)}</b>
      <span class="sub">${c.expires ? `${esc(c.issuer)} · expires ${esc(c.expires.slice(0, 10))}` : esc(c.detail)}</span></div>
    <div class="row-num">${c.daysLeft != null ? `${Math.floor(c.daysLeft)}<i> days</i>` : '<span class="state down">Down</span>'}</div>
  </div>`).join("");
}

function renderHost(s) {
  const h = s.host;
  $("host-name").textContent = h.hostname || "";
  const rows = [];
  if (h.load) rows.push(["Load", `${h.load.join(" · ")} <span style="color:var(--muted)">on ${esc(h.cpus)} CPU${h.cpus === 1 ? "" : "s"}</span>`]);
  if (h.memTotalMb) rows.push(["Memory", `${esc(h.memTotalMb - h.memAvailableMb)} / ${esc(h.memTotalMb)} MB used`]);
  if (h.diskTotalGb) rows.push(["Disk", `${esc((h.diskTotalGb - h.diskFreeGb).toFixed(1))} / ${esc(h.diskTotalGb)} GB used`]);
  if (h.uptimeSeconds) rows.push(["Uptime", esc(duration(h.uptimeSeconds))]);
  const units = Object.entries(h.units || {});
  if (units.length) rows.push(["Units", units.map(([u, st]) =>
    `<span class="unit"><span class="dot ${st === "active" ? "up" : "down"}" style="display:inline-block;margin-right:5px"></span>${esc(u)}</span>`).join("")]);
  $("host").innerHTML = rows.map(([k, v]) => `<span class="k">${k}</span><span class="v mono">${v}</span>`).join("");
}

function render(s) {
  last = s;
  if (s.lobby.announce) $("ep-announce").textContent = s.lobby.announce;
  if (s.awards.endpoint) $("ep-auth").textContent = s.awards.endpoint;
  renderBanner(s);
  renderTiles(s);
  renderServices(s);
  renderLobby(s);
  renderSpark(s);
  renderRooms(s);
  renderAwards(s);
  renderCerts(s);
  renderHost(s);
  tick();
}

// The page's own freshness: a checker that stopped writing must not leave a
// green banner up, so old data says so in the banner as well as the pill.
function tick() {
  if (!last) return;
  const age = (Date.now() - Date.parse(last.generated)) / 1000;
  const stale = age > last.intervalSeconds * 4;
  const live = $("live");
  live.dataset.state = stale ? "stale" : "live";
  $("live-text").textContent = `Updated ${ago(age)}`;
  if (stale) {
    $("banner").dataset.state = "stale";
    $("banner-text").textContent = "Status data is out of date";
    $("banner-sub").textContent = `Last checked ${ago(age)}; the checker itself may be down.`;
  }
  $("foot").textContent = `Checked ${new Date(last.generated).toLocaleString()} in ${last.elapsedMs} ms · every ${last.intervalSeconds} s from ${last.host.hostname || "the lobby host"}`;
}

async function load() {
  try {
    const r = await fetch("/status.json", { cache: "no-cache" });
    if (!r.ok) throw new Error(`HTTP ${r.status}`);
    render(await r.json());
  } catch (e) {
    const live = $("live");
    live.dataset.state = "lost";
    $("live-text").textContent = "Status unavailable";
    if (!last) {
      $("banner").dataset.state = "down";
      $("banner-text").textContent = "Status data unavailable";
      $("banner-sub").textContent = String(e.message || e);
    }
  }
}

// One tooltip for every bar and segment on the page.
const tip = $("tip");
document.addEventListener("pointerover", (e) => {
  const t = e.target.closest("[data-tip]");
  if (!t) { tip.hidden = true; return; }
  tip.textContent = t.dataset.tip;
  const r = t.getBoundingClientRect();
  tip.style.left = `${Math.min(window.innerWidth - 90, Math.max(90, r.left + r.width / 2))}px`;
  tip.style.top = `${r.top}px`;
  tip.hidden = false;
});
document.addEventListener("scroll", () => { tip.hidden = true; }, { passive: true });

load();
setInterval(load, REFRESH_MS);
setInterval(tick, 1000);
