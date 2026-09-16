# LuaShareX

Export licensed Steam games as `.lua` files — app token, depots and decryption keys included.

## Download

Grab the latest `LuaShareX-v*-win-x64.zip` from
[GitHub Releases](https://github.com/manifest-dex/LuaShareX/releases),
unzip and run. Requires Windows 10/11 x64, the .NET 8 Desktop Runtime, and Steam.

The app checks for updates automatically on startup (and via the header
button) and installs them from GitHub Releases.

## Modes

- **SteamKit2** — sign in with QR code or username + password (Steam Guard
  supported). Lists your full owned library with ownership tokens.
- **Local (account-free)** — reads `config.vdf`, `stplug-in/*.lua` and every
  `appmanifest_*.acf` across all library folders. No login.

## Export format

One game exports as `<appid>.lua`; selecting several exports a `.zip` with
one `<appid>.lua` per game. Depot keys are fetched lazily at export time,
only for the games you select.

## Build

```powershell
dotnet build LuaShareX.csproj -c Release
```

## Release

Push a tag like `v1.0.0` — the [release workflow](.github/workflows/release.yml)
builds the win-x64 zip and publishes the GitHub Release the updater consumes.
