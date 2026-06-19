# Sea Power Four Player — Friend Install

You only need:

- A Windows 64-bit PC
- Sea Power installed through Steam
- `SeaPowerFourPlayerLauncher.exe`

## Install

1. Close Sea Power.
2. Run `SeaPowerFourPlayerLauncher.exe`.
3. Confirm the detected Sea Power directory, or use **Browse**.
4. Enter your player name and choose Steam or Direct IP.
5. Click **Install Mod**.
6. Click **Launch Sea Power**.
7. In game, press `Ctrl+F9` to open the four-player panel.

The launcher is self-contained. It carries BepInEx, LiteNetLib, and the
`SeaPowerFourPlayer.dll` plugin inside the EXE.

The launcher includes official Sea Power trailer footage from Steam as its
silent background and a four-song player in the bottom-right corner. Use
previous, play/pause, next, volume, and mute to control the playlist. Open
**Settings** for install location, connection, and host-side options.

## Public operations

- Open **Public Lobbies** to see active Steam sessions listed by the Railway service.
- Click **Create Public Lobby** to launch Sea Power and publish your Steam lobby.
- Select an operation and click **Join Selected** to launch directly into it.
- Public discovery needs Railway, but gameplay traffic still travels through Steam.
- If Railway is unavailable, normal launch, Steam invitations, and Direct IP continue
  to work.

## Steam party

- The host chooses **Steam lobby + invites**, installs, and launches.
- Friends do the same, then accept the host's Steam invitation.
- The lobby supports one host and up to three clients.

All players must use the same launcher and plugin release. If a connection is
refused for protocol or plugin mismatch, every PC should download the current
launcher and run **Repair Mod** before trying again.

## Direct IP

- The host chooses **Direct IP** and **Host**.
- Clients choose **Direct IP** and **Client**, then enter the host's IP.
- Everyone must use the same UDP port. The default is `7777`.

This is an alpha multiplayer mod. Keep the tested sync profile at 20 Hz for
missiles and 10 Hz for units until the group confirms a stable connection.
