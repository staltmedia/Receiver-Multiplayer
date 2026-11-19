# Receiver Multiplayer

Co-op multiplayer for Receiver (the first one), as a BepInEx 5 mod. Host or join from the pause menu over Steam or Direct IP (LAN, Radmin, Tailscale, Hamachi, port forwarding).

## Install (Windows)

Download from [Releases](https://github.com/staltmedia/Receiver-Multiplayer/releases/latest), then either:

- **Updater:** run `ReceiverMultiplayer-Updater.exe`. It finds Receiver (Steam libraries, or pick the folder) and installs the mod there.
- **Drag and drop:** unzip `ReceiverMultiplayer-<version>.zip` into the Receiver folder (the one with `Receiver.exe`).

Start the game, press Escape, and click **Multiplayer**.

Windows may warn about the updater because it isn't code-signed: choose **More info → Run anyway**, or use the zip.

## Updates

Everyone in a session needs the same version. When a new one is out, the game asks **Update to X?** at launch: OK updates and restarts the game, Cancel plays the version you have. If you tick **Don't show again**, update later by running `BepInEx\plugins\ReceiverMultiplayer\ReceiverMultiplayer-Updater.exe` (its checkbox turns the question back on).

## Good to know

- The update check asks GitHub for the latest version number at each launch; nothing about you is sent.
- Joining sends the host the list of add-ons you have (names and hashes), so everyone plays the same content.
- Direct IP traffic, including its password, isn't encrypted. Use it on networks you trust.

## Build

Requirements: .NET SDK 8 or newer, and Receiver installed.

```
dotnet build -c Release
```

The game is found through the Steam registry key at `<Steam>/steamapps/common/Receiver`. If it lives in another library, copy `Local.props.example` to `Local.props` and set `GameDir`. When BepInEx is installed in the game folder, the build copies the plugin to `BepInEx/plugins/ReceiverMultiplayer/`.

Release zip: `packaging/make-release.ps1 -BepInEx <BepInEx 5 x64 zip>`. The design notes are in [docs/SPEC.md](docs/SPEC.md).

## License

Receiver Multiplayer is MIT licensed (see [LICENSE](LICENSE)). It bundles BepInEx and UnityDoorstop (LGPL 2.1), HarmonyX, MonoMod, Mono.Cecil and LiteNetLib (MIT); see `packaging/THIRD-PARTY-NOTICES.txt`.
