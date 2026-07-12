# LurkBait Twitch Fishing - Mods

Three small BepInEx plugins for LurkBait Twitch Fishing.
They're independent, so install any combination. None of them touch the game's own files, BepInEx just loads them at startup. Delete a plugin's DLL to turn it off.

## Contents

- [What each plugin does](#what-each-plugin-does)
  - [No Chatbot Outage](#no-chatbot-outage)
  - [Stable User IDs](#stable-user-ids)
  - [Remote Control](#remote-control)
- [Install](#install)
- [Existing data & the Twitch limitation](#existing-data--the-twitch-limitation)
- [Remote Control endpoints](#remote-control-endpoints)
  - [Streamer.bot example](#streamerbot-example)
- [Building](#building)
- [Checksums](#checksums)

## What each plugin does

### No Chatbot Outage
- Hides the stale "Temporary Chatbot Outage" popup that shows on every launch.
- It skips only that one popup. Any other announcement still shows normally.

### Stable User IDs
Keeps a viewer's gold, points and casts when they change their Twitch username, by
tracking their stable numeric Twitch id instead of the name. What it does, in order:

- When a viewer fishes, it looks up their Twitch id and saves an id to username map (`StableUserIds.map`, next to your save). It only records people who actually fish.
- On startup, once you're connected to Twitch, it back-fills ids for your existing roster so future renames are covered too.
- When a known id shows up under a new name, it merges the old record into the new one (gold, casts, snapshots) and rewrites their catch history to the new name. Merges only add, they never delete anything.
- When a rename is merged, it shows an in-game message like `@oldname is now known as @NewName!`.
- Before its first merge it writes a one-time backup: `*.premod-backup.txt`.

It can't fix a viewer who renamed before you installed it (Twitch can't map an old, released name back to its id). See "Existing data" below for the manual fix.

### Remote Control
Adds a local HTTP endpoint (127.0.0.1 only) so tools like Streamer.bot or SAMMI can change a player's gold and read the result back. What it does, in order:

- Starts a local server on launch (default port 30500), and once you're connected to Twitch it shows an in-game message like `Remote Control enabled on port 30500!`.
- Generates a token on first run that every call has to include.
- Applies each change on the game's main thread and can show an in-game toast.

Endpoints and setup are further down.

## Install

1. Get BepInEx 5 (x64, Windows) from https://github.com/BepInEx/BepInEx/releases and download `BepInEx_win_x64_5.4.x.zip`.
2. Close the game.
3. Extract the zip into the game folder, the one with `LurkBait Twitch Fishing.exe`. In Steam you can open it with right-click the game, Manage, Browse local files. You should end up with `winhttp.dll` and a `BepInEx` folder next to the exe.
4. Launch the game once and quit (this lets BepInEx finish setting up), then launch again.
5. Download the plugin DLLs from our [Releases](../../releases/latest) page and drop the ones you want into `BepInEx\plugins\`.

## Existing data & the Twitch limitation

Stable User IDs protects renames going forward. It can't retroactively fix a viewer who renamed before you installed it. If you know some old to new name pairs, you can merge them by hand (the save is plain JSON):

1. In the game, open Settings, scroll down almost to the end, click "Open Save Data Location", then close the game.
2. Back up `PlayerData.txt` and `CatchData.txt`. (The mod also makes its own `*.premod-backup.txt` before its first change.)
3. In `PlayerData.txt`, find the old-name and new-name entries (keys are lowercase). Add the old entry's `gold`, `totalCasts`, `goldSnapshot` and `totalCastsSnapshot` into the new entry, then delete the old entry.
4. Optional, for catch history: in `CatchData.txt`, rename the old username to the new one on matching entries.
5. Save and relaunch.

## Remote Control endpoints

Call these with GET or POST, params go in the query string. Bound to `127.0.0.1` only.

| Path | Params | Effect |
|---|---|---|
| `/ping` | none | Health check |
| `/gold/get` | `user` | Report current gold |
| `/gold/add` | `user`, `amount` | Add gold |
| `/gold/subtract` | `user`, `amount`, `strict?` | Subtract (clamps to 0; `strict=true` returns 409 if they can't afford it) |
| `/gold/set` | `user`, `amount` | Set gold |

Every call needs the token, either as `?token=...` or an `Authorization: Bearer ...` header, or it returns 401. The token is generated on first run and saved to `BepInEx\config\dev.irensuidas.lurkbait.remotecontrol.token`. Delete that file to rotate it. Port and toasts live in `BepInEx\config\dev.irensuidas.lurkbait.remotecontrol.cfg`.

Status codes: 200 done, 400 bad params, 401 bad token, 404 no such player, 409 not enough gold (strict), 503 game not ready. The JSON body carries `ok`, `user`, `display`, `existed`, `gold`, `requested`, `applied` and a ready-to-post `message`.

### Streamer.bot example

A simple command that subtracts 100 gold from whoever runs it. Streamer.bot's Fetch URL sub-action only does GET requests, which our endpoints accept, so no C# is needed.

1. In Streamer.bot, create a new action and name it something like "Remove gold".
2. Add a trigger: Twitch, Chat Message, Command, and set the command to `!buy`.
3. Add a sub-action: Core, Network, Fetch URL.
4. Set the URL to the one below, and set Variable Name to `response`.
5. Tick "Parse result as JSON" so you get `%response.message%`, `%response.gold%` and the rest of the fields.
6. Optional: add a Twitch, Send Message sub-action that posts `%response.message%` back to chat.

```
http://127.0.0.1:30500/gold/subtract?user=%user%&amount=100&token=YOUR_TOKEN
```

`%user%` is whoever ran the command. To let a mod target someone else, have them type `!buy someviewer` and use `%input0%` in place of `%user%`. Replace `YOUR_TOKEN` with the value from your `...remotecontrol.token` file, and change `30500` if you set a different port.

## Building

You need the .NET 10 SDK.
The plugins reference the game's own assemblies in place, so they're never committed.

Run the build script:

```powershell
./scripts/build-release.ps1
```

It finds your Steam install automatically, builds the three plugins, and packs the DLLs into a zip under `dist/`. If it can't find the game, pass the path yourself:

```powershell
./scripts/build-release.ps1 -GameManagedDir "C:\...\LurkBait Twitch Fishing_Data\Managed"
```

## Checksums

SHA256 of each plugin DLL in the current release. After downloading, run `Get-FileHash -Algorithm SHA256 <file>` and check it against the matching line here.

```
34f5b882c5a725151097704d976438be18a6b0178c620ad1c555c25ae0e2783a  LurkBait.NoChatbotOutage.dll
c944a50c382c4a1a96f926dca8471f803cc2555f28cfe79ac8879aaad6acce23  LurkBait.StableUserIds.dll
3b8e4f3e72e9dce545b233a292b766636276fdf4872feaac6e57e3c272cdf1f7  LurkBait.RemoteControl.dll
```