const $ = id => document.getElementById(id);
const json = async (url, options) => {
  const response = await fetch(url, options);
  if (response.status === 401) location.href = "/admin/login";
  if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
  return response.status === 204 ? null : response.json();
};

document.querySelectorAll(".nav").forEach(button => button.onclick = () => {
  document.querySelectorAll(".nav,.view").forEach(x => x.classList.remove("active"));
  button.classList.add("active");
  $(button.dataset.view).classList.add("active");
});

const escapeHtml = text => String(text ?? "").replace(/[&<>"']/g, c => ({
  "&":"&amp;","<":"&lt;",">":"&gt;",'"':"&quot;","'":"&#39;"
}[c]));

async function refreshOverview() {
  const data = await json("/api/admin/overview");
  $("activeLobbies").textContent = data.activeLobbies;
  $("playersOnline").textContent = data.playersOnline;
  $("events24h").textContent = data.events24h;
  const attempts = Number(data.joinAttempts24h);
  $("joinSuccess").textContent = attempts
    ? `${Math.round(Number(data.successfulJoins24h) * 100 / attempts)}%`
    : "—";
}

async function refreshLobbies() {
  const rows = await json("/api/admin/lobbies");
  $("lobbyRows").innerHTML = rows.map(x => `
    <tr>
      <td>${escapeHtml(x.lobbyName)}<br><small>${escapeHtml(x.steamLobbyId)}</small></td>
      <td>${escapeHtml(x.hostName)}<br><small>${escapeHtml(x.hostSteamId)}</small></td>
      <td>${x.playerCount}/${x.maxPlayers}</td>
      <td>${x.pvp ? "PVP" : "CO-OP"}</td>
      <td>${escapeHtml(x.pluginVersion)} / ${x.protocol}</td>
      <td>${new Date(x.lastHeartbeat).toLocaleTimeString()}</td>
      <td><button class="danger" onclick="removeLobby('${x.steamLobbyId}')">REMOVE</button></td>
    </tr>`).join("") || `<tr><td colspan="7">No active or recent lobbies.</td></tr>`;
}

async function refreshTraffic() {
  const rows = await json("/api/admin/events");
  $("trafficRows").innerHTML = rows.map(x => `
    <div class="event">
      <b>${escapeHtml(x.eventName)}</b>
      <span>${escapeHtml(x.lobbyId || "—")}</span>
      <span>${escapeHtml(x.detail || x.version || "—")}</span>
      <span>${new Date(x.createdAt).toLocaleString()}</span>
    </div>`).join("") || `<div class="event">No traffic recorded.</div>`;
}

async function refreshConfig() {
  const data = await json("/api/admin/config");
  $("announcement").value = data.announcement;
  $("maintenance").checked = data.maintenanceMode;
  $("publicEnabled").checked = data.publicLobbiesEnabled;
  $("launcherVersion").value = data.requiredLauncherVersion;
  $("pluginVersion").value = data.requiredPluginVersion;
  $("maintenanceSummary").textContent = data.maintenanceMode
    ? "Maintenance mode active"
    : data.publicLobbiesEnabled ? "Public matchmaking enabled" : "Public matchmaking disabled";
  $("versionSummary").textContent =
    data.requiredLauncherVersion || data.requiredPluginVersion
      ? `Required: launcher ${data.requiredLauncherVersion || "any"}, plugin ${data.requiredPluginVersion || "any"}`
      : "No forced version gate";
}

async function saveConfig() {
  await json("/api/admin/config", {
    method:"PUT",
    headers:{"Content-Type":"application/json"},
    body:JSON.stringify({
      announcement:$("announcement").value,
      maintenanceMode:$("maintenance").checked,
      publicLobbiesEnabled:$("publicEnabled").checked,
      requiredLauncherVersion:$("launcherVersion").value,
      requiredPluginVersion:$("pluginVersion").value
    })
  });
  $("saveState").textContent = "Configuration applied";
  setTimeout(() => $("saveState").textContent = "", 2500);
  await refreshConfig();
}

async function removeLobby(id) {
  if (!confirm("Remove this lobby from the public directory?")) return;
  await json(`/api/admin/lobbies/${id}`, {method:"DELETE"});
  await refreshLobbies();
  await refreshOverview();
}

async function refreshAll() {
  await Promise.all([refreshOverview(),refreshLobbies(),refreshTraffic(),refreshConfig()]);
}

setInterval(() => $("clock").textContent = new Date().toLocaleTimeString(), 1000);
setInterval(refreshAll, 15000);
refreshAll().catch(console.error);
