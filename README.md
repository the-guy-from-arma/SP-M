# Sea Power Four Player

The repository now contains three connected deliverables:

- `launcher/` — self-contained Windows installer, public lobby browser and game launcher
- `src/` — BepInEx four-player gameplay and Steam lobby plugin
- `backend/` — Railway-ready public lobby directory and creator traffic dashboard

See `backend/README.md` for the Railway + PostgreSQL deployment steps.

Experimental four-player multiplayer plugin for **Sea Power: Naval Combat in the Missile Age**.

This project supports one host and up to three clients. It is a standalone BepInEx plugin with its own assembly name, plugin GUID, protocol version, lobby, player slots, teams, per-client UID bands, targeted routing, ownership validation, ready tracking, and four-player overlay.

No missions are bundled or modified. The host may load an existing Sea Power scenario and synchronize it to the connected clients.

## Status

Version: `0.1.2-alpha`

Verified:

- Builds against the installed Sea Power assemblies.
- Protocol serialization smoke tests pass.
- Four lobby slots, three client assignments, fifth-player rejection, team updates, ready state, disconnect cleanup, slot reuse, and unique UID bands are covered by the smoke test.
- Both LiteNetLib and Steam transports compile with per-peer IDs and targeted sends.

Still requires live testing with four separate game instances:

- Full scenario load on three clients.
- Sustained entity, weapon, damage, and carrier-operation replication.
- Steam lobby invites with four Steam accounts.
- Reconnect during an active scenario.

## Important

Remove the original `SeapowerMultiplayer.dll` before installing this plugin. The new plugin declares it incompatible because both mods patch the same Sea Power methods.

## Installation

Recommended friend install:

1. Download `SeaPowerFourPlayerLauncher.exe`.
2. Close Sea Power.
3. Run the launcher and confirm the detected game directory.
4. Enter a player name and select Steam or Direct IP.
5. Click **Install Mod**, then **Launch Sea Power**.

The launcher is a self-contained Windows x64 executable. It embeds BepInEx,
LiteNetLib, and the four-player plugin, so friends do not need to run PowerShell
or manually copy DLLs. Its naval b-roll and original ambient soundtrack are also
local and embedded; no streaming media is required.

The experimental public fleet/lobby browser is intentionally not included in
the current launcher. Steam invites and Direct IP remain available while a
future FiveM-style lobby flow is designed and tested separately.

PvP team assignment supports a temporary neutral navigation role. With three
connected players, the third slot may move neutral merchant ships but cannot use
their weapons, sensors, aircraft, or combat systems. When a fourth player joins,
the game automatically returns neutral ships to AI and rebalances the players
into two Red and two Blue slots.

Manual package install:

1. Extract the complete release zip.
2. Close Sea Power.
3. Run `Install.ps1`.

The release includes BepInEx 5.4.23.5, Doorstop, LiteNetLib, and the four-player
plugin. The installer places the bootstrap files in the Sea Power root, installs
the BepInEx core, disables the conflicting two-player DLL, and installs:

   - `SeaPowerFourPlayer.dll`
   - `LiteNetLib.dll`
4. Launch Sea Power once to generate or refresh:
   - `BepInEx\config\com.seapower.fourplayer.cfg`
5. Press `Ctrl+F9` to show or hide the multiplayer panel.

The installer preserves the old network/Steam choices, adds the new lobby settings,
and reduces legacy 60/40 Hz sync tuning to the safer four-player defaults of 20/10 Hz.

## Direct-IP play

Host:

1. Set `Network.IsHost=true`.
2. Set `Network.Transport=LiteNetLib`.
3. Forward the configured UDP port if playing over the internet.
4. Click **Start Hosting**.

Clients:

1. Set `Network.IsHost=false`.
2. Set `Network.HostIP` to the host address.
3. Set the same UDP port and transport.
4. Click **Connect**.

After connecting:

1. Clients choose Blue or Red in PvP, select a control role, and mark themselves ready.
2. The host loads any existing scenario.
3. The host clicks **Send State & Wait**.
4. When all connected clients finish loading, the panel reports that all clients are ready.

After reconnecting, a client can press `Ctrl+F10` to request a targeted state resync without forcing the other clients to reload.

## Steam play

1. Set `Network.Transport=Steam`.
2. The host creates a lobby and invites up to three friends.
3. Clients accept the invitation and choose their team/ready state.
4. The host loads a scenario and clicks **Send State & Wait**.

The Steam lobby capacity is four total members.

## Configuration

| Section | Setting | Meaning |
|---|---|---|
| Network | `IsHost` | Host/server when true; client when false |
| Network | `HostIP` | Direct-IP host address |
| Network | `Port` | LiteNetLib UDP port |
| Network | `AutoConnect` | Automatically host or connect after startup |
| Network | `PvP` | Two-team PvP when true; shared Blue-side co-op when false |
| Network | `Transport` | `LiteNetLib` or `Steam` |
| Lobby | `PlayerName` | Name shown in the four-player roster |
| Lobby | `PreferredSlot` | `0` automatic, or request slot `2` through `4` |
| Lobby | `PreferredTeam` | `0` Blue, `1` Red, `255` automatic |
| Lobby | `PreferredRole` | `0` Any, `1` Surface, `2` Submarine, `3` Air, `4` Land |

For multi-instance testing, equivalent `SP4P_*` environment overrides are available, including `SP4P_ROLE`, `SP4P_HOSTIP`, `SP4P_PORT`, `SP4P_PLAYER_NAME`, `SP4P_SLOT`, `SP4P_TEAM`, and `SP4P_CONTROL_ROLE`.

## Authority model

- The host owns the authoritative simulation.
- Clients submit player orders to the host.
- The host validates both the requesting player's team and control role before applying an order.
- State, spawns, impacts, damage, destruction, deck state, and cosmetic events are replicated to every established client.
- Client-local entity IDs use separate 10,000,000-ID bands to prevent collisions.
- Co-op unit-selection locks are tracked per remote player.

## Build

```powershell
dotnet build src\SeaPowerFourPlayer.csproj -c Release `
  /p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Sea Power" `
  /p:InstallAfterBuild=false
```

Run protocol tests:

```powershell
dotnet run --project tests\ProtocolSmoke\ProtocolSmoke.csproj
```

Build the self-contained launcher and friend zip:

```powershell
.\scripts\build-launcher.ps1
```

## Reference implementations

The user-provided folders are explicit compatibility references:

- `C:\Users\llkoo\Desktop\SeaPowerMultiplayerMod-main`
- `C:\Users\llkoo\Desktop\SeaPowerMultiplayerMod-v0.3.0`

The public project and current Steam multiplayer discussions were also reviewed:

- https://github.com/malfboi/SeaPowerMultiplayerMod
- https://steamcommunity.com/app/1286220/discussions/

See `THIRD_PARTY_NOTICES.md` for details.
