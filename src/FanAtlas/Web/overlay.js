"use strict";
const stage = document.getElementById("stage");
const notice = document.getElementById("notice");
const token = new URLSearchParams(location.search).get("token") || "";
const tiles = new Map();
let busy = false, lastSuccess = 0;
function resize() {
  const scale = Math.min(innerWidth / 1920, innerHeight / 1080);
  stage.style.transform = "scale(" + scale + ")";
  stage.style.left = ((innerWidth - 1920 * scale) / 2) + "px";
  stage.style.top = ((innerHeight - 1080 * scale) / 2) + "px";
}
addEventListener("resize", resize); resize();
function tileNode(tile) {
  let entry = tiles.get(tile.id);
  if (!entry) {
    const root = document.createElement("section"); root.className = "fan";
    const picture = document.createElement("div"); picture.className = "picture";
    const image = document.createElement("img"); image.src = "/assets/fan.png"; image.alt = "";
    const temp = document.createElement("div"); temp.className = "temperature";
    const caption = document.createElement("div"); caption.className = "caption";
    const name = document.createElement("div"); name.className = "name";
    const rpm = document.createElement("div"); rpm.className = "rpm";
    const state = document.createElement("div"); state.className = "state";
    picture.append(image,temp); caption.append(name,rpm,state); root.append(picture,caption); stage.append(root);
    entry = {root,temp,name,rpm,state}; tiles.set(tile.id,entry);
  }
  return entry;
}
function clearLive() {
  for (const e of tiles.values()) { e.temp.textContent = "—"; e.rpm.textContent = "—"; e.state.textContent = "FanAtlas offline"; e.root.classList.add("stale"); }
}
async function poll() {
  if (busy) return; busy = true;
  try {
    const response = await fetch("/api/overlay?token=" + encodeURIComponent(token), {cache:"no-store",signal:AbortSignal.timeout(3500)});
    if (!response.ok) throw Error("Verbindung");
    const data = await response.json();
    if (Date.now() - Date.parse(data.generatedUtc) > 10000 || !data.scene) throw Error("Veraltete Quelle");
    lastSuccess = Date.now();
    const bg = data.scene.background;
    stage.style.backgroundImage = ["stream-startet.jpg","bin-gleich-zurueck.jpg"].includes(bg) ? 'url("/assets/' + bg + '")' : "none";
    const sensors = new Map(data.sensors.map(s => [s.id,s]));
    const present = new Set();
    for (const tile of data.scene.tiles) {
      present.add(tile.id); const e = tileNode(tile);
      e.root.hidden = !tile.visible; e.root.style.left = tile.x + "px"; e.root.style.top = tile.y + "px"; e.root.style.width = tile.size + "px"; e.temp.style.fontSize = tile.size * .14 + "px";
      e.name.textContent = tile.name;
      const t = sensors.get(tile.temperatureSensorId), r = sensors.get(tile.rpmSensorId);
      e.temp.textContent = t?.fresh && Number.isFinite(t.value) ? t.value.toLocaleString("de-DE",{maximumFractionDigits:1}) + "°" : "—";
      e.rpm.textContent = r?.fresh && Number.isFinite(r.value) ? r.value.toLocaleString("de-DE",{maximumFractionDigits:r.unit==="RPM"?0:1}) + " " + r.unit : "—";
      e.state.textContent = r?.fresh ? "Live" : tile.fromProfile && !tile.rpmSensorId ? "Profil · nicht erkannt" : "Quelle fehlt / veraltet";
      e.root.classList.toggle("stale",!r?.fresh);
      e.root.setAttribute("aria-label", tile.name + ", " + e.temp.textContent + ", " + e.rpm.textContent);
    }
    for (const [id,e] of tiles) if (!present.has(id)) { e.root.remove(); tiles.delete(id); }
    notice.textContent = present.size ? "" : "Noch keine Lüfter platziert. Layout in FanAtlas erstellen.";
  } catch {
    notice.textContent = "FanAtlas nicht erreichbar · keine aktuellen Messwerte";
    if (!lastSuccess || Date.now() - lastSuccess > 5000) clearLive();
  } finally { busy = false; }
}
poll(); setInterval(poll,2000);

