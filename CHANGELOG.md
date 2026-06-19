# Changelog

## Launcher v0.6.3 / Plugin v0.1.6-alpha

- The launcher now detects the active legacy two-player plugin that causes
  BepInEx to reject the four-player plugin, blocks launch, and directs the user
  to one-click Repair.
- The public lobby browser refreshes automatically while it is open and remains
  visible while a launcher-created public Steam operation starts.
- The website's primary download now serves the complete launcher package with
  a direct one-click installer EXE, with the complete ZIP retained separately.
- Removed the generic discovery/deployment marketing sections from the download
  page so it stays focused on the launcher.
- Release publishing copies the full package into the web service so the
  download button no longer depends on an unset Railway asset URL.

## Launcher v0.6.2 / Plugin v0.1.6-alpha

- Bumped the multiplayer protocol to 403 so old launcher builds cannot appear
  compatible with the corrected Steam join flow.
- The host now checks the exact plugin version during Hello/Welcome and returns
  a clear update-and-repair message for mixed installations.
- Centralized the launcher's protocol metadata so lobby discovery and telemetry
  cannot accidentally keep querying an older protocol.
- Updated the Railway release metadata and public download page for v0.6.2.

## Launcher v0.6.1 / Plugin v0.1.5-alpha

- Fixed launcher-selected Steam lobby joins being queued after Steam callbacks
  had already initialized and therefore never executing.
- Added a short-lived launcher-to-game command file so Steam process restarts
  cannot discard public-host or join-lobby instructions.
- Public lobby creation now opens the Steam invite overlay after the lobby is
  successfully created.
- Repair now finds legacy `com.seapowermultiplayer.plugin` DLLs recursively,
  even when renamed or placed in plugin subfolders, quarantines them, and
  verifies that only the four-player plugin remains active.

## Launcher v0.6.0 / Plugin v0.1.4-alpha

- Added an Aegis Combat System-inspired startup sequence with phased-array sweep,
  command-bus checks, tactical initialization log, and animated readiness bar.
- Added a FiveM-style public operations browser to the launcher.
- Added one-click **Create Public Lobby** and **Join Selected** flows.
- Public hosts now create searchable Steam lobbies, register them with Railway,
  send 20-second heartbeats, and disappear automatically when the host leaves.
- Added the Railway-ready lobby API, PostgreSQL schema, traffic telemetry,
  announcements, maintenance controls, version gates, and private creator dashboard.
- Added rate sliders and tested-profile recommendations to Settings.
- Reduced rounded “bubble” styling in favor of sharper naval-console geometry.
- Hardened Steam trailer playback with an independent loop watchdog.
- Replaced the GTA 6 playlist file with the new lyrics version and added
  Joyner Lucas - I'm Ill as the fourth embedded track.
- Normal launch, Steam invites, and Direct IP remain available if Railway is offline.

## Launcher v0.5.0 / Plugin v0.1.3-alpha

- Replaced the rendered still-frame loop with the official Sea Power launch
  trailer served by Steam, played silently as a fullscreen looping background.
- Rebuilt the launcher home screen around the footage with floating launch and
  music controls instead of a permanent wall of configuration widgets.
- Moved install, player, connection, host-side, and sync settings into a smooth
  slide-in setup drawer.
- Added persistent music volume and mute controls to the bottom-right player.
- Kept a local fallback image for PCs where Windows cannot decode the trailer.

## Launcher v0.4.0 / Plugin v0.1.3-alpha

- Added the three user-provided MP3 tracks as an embedded bottom-right playlist
  with previous, play/pause, next, artist, and track title controls.
- Added host Blue/Red selection to the launcher and handshake.
- Added dynamic PvP assignments:
  - two players remain Red versus Blue;
  - with three players, slot 3 may navigate neutral merchant ships;
  - with four players, teams rebalance to 2v2 and neutral ships return to AI.
- Neutral-player authority accepts movement and waypoint orders only. Weapon,
  sensor, aircraft, targeting, and other combat orders are rejected by the host.
- Added automatic targeted state resync when slot 3 changes between Neutral and
  a combat team.
- Bumped the multiplayer wire protocol to 402.

## Launcher v0.3.0

- Removed the unfinished public fleet browser, registry publishing, browser
  protocol handler, and launcher-side lobby controls.
- Simplified the interface to game location, player name, connection type,
  install/repair, and launch.
- Added a locally rendered 12-second naval b-roll loop embedded in the EXE.
- Added an original 42-second ambient soundtrack with a persistent mute control.
- Added `--smoke-install <directory>` so the published EXE can prove that its
  embedded BepInEx, Doorstop, plugin, LiteNetLib, and config payload installs correctly.
- Kept future FiveM-style lobby work out of this release until the backend and
  joining flow are ready.

## Launcher v0.2.0

- Added a self-contained Windows x64 launcher carrying BepInEx, LiteNetLib, and
  `SeaPowerFourPlayer.dll` inside one EXE.
- Replaced the original stacked form UI with an animated cinematic naval command
  deck, including image crossfades, slow camera drift, radar sweep, and scanline motion.
- Added four-player crew identity controls for player name, preferred slot, team,
  and control role.
- Updated install/repair to disable the conflicting two-player DLL, preserve
  backups, clear the generated chainloader cache, and install the correct plugin.
- Added SHA-256 verified launcher self-updates through an uploadable `latest.json`
  manifest.
- Added a friend-ready zip package and short installation guide.

## 0.1.2-alpha

- Added a complete BepInEx 5.4.23.5 + Doorstop install bundle.
- Fixed missing root `winhttp.dll` and `doorstop_config.ini` bootstrap installation.
- Clear BepInEx's generated chainloader cache during installation so newly installed plugins are discovered.
- Use numeric `0.1.2` BepInEx plugin metadata; prerelease suffixes cause BepInEx 5 to skip the DLL.
- Replaced bursty 450 KB Steam fragments with paced 32 KB reliable transfer queues.
- Added exact 2.2 MB fragmentation/reassembly smoke coverage.
- Added queued-transfer progress to the multiplayer overlay.
- Widened and clarified the four-player roster and Steam host/client status.
- Disabled state sync until at least one established client is connected and all connected clients are ready.

## 0.1.1-alpha

- Fixed the initial roster being dropped when Welcome and PlayerRoster arrive in one poll.
- Relayed validated client orders to the other connected clients.
- Fixed host time broadcasts when two-player voting is configured but 3-4 players are connected.
- Aggregated Steam send failures across all targeted clients.
- Added malformed-packet guards and case-insensitive transport selection.
- Added a safe installer that disables the conflicting two-player plugin, migrates its config,
  and normalizes legacy high-rate sync settings for four-player traffic.
- Expanded protocol and lobby smoke coverage.

## 0.1.0-alpha

- Created a standalone plugin identity and assembly.
- Added one-host/three-client transport support.
- Added per-peer connection IDs and targeted sends for LiteNetLib and Steam.
- Added four player slots, names, teams, ready state, disconnect cleanup, and roster replication.
- Added per-client handshake state and unique UID bands.
- Added team-based host validation for client orders.
- Added Any, Surface, Submarine, Air, and Land control roles for dividing a team.
- Added multi-client session-ready tracking.
- Added per-player co-op selection locks and relaying.
- Added a four-player in-game roster and controls.
- Increased Steam lobby capacity to four.
- Disabled two-player time voting when more than two players are connected.
- Added protocol and lobby smoke tests.
