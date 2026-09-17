# LuaShareX

Export licensed Steam games as `.lua` files — app token, depots and decryption keys included.

## Download

Grab the latest release from
[GitHub Releases](https://github.com/manifest-dex/LuaShareX/releases):

- `LuaShareX-Setup-v*-win-x64.exe` — installer (recommended, per-user, no admin)
- `LuaShareX-v*-win-x64.zip` — portable, unzip and run

Requires Windows 10/11 x64, the .NET 8 Desktop Runtime, and Steam.

The app checks for updates automatically on startup (and via the header
button) and installs them from GitHub Releases.

## Modes

- **SteamKit2** — sign in with QR code or username + password (Steam Guard
  supported). Lists your full owned library with ownership tokens.
- **Remembered accounts** — choose an account under **Accounts on this computer**
  and click **Continue with selected account**. Uses Steam's remembered Windows
  login, or a login previously saved by LuaShareX. Use **Refresh** after
  adding an account in Steam. If its login is missing, expired or revoked, use
  QR/password instead.
- **Local (account-free)** — reads `config.vdf`, `stplug-in/*.lua` and every
  `appmanifest_*.acf` across all library folders. No login.

Multiple accounts are supported, with one active account at a time. **Switch
account** keeps remembered logins and clears the previous account's library and
pending login before opening the account picker. **Logout** forgets only the
current account's LuaShareX login; it does not sign out of Steam or remove Steam's
remembered login. Accounts added with QR/password also appear in the picker.

Steam's local login must be readable by the same Windows user who saved it.
LuaShareX reads `loginusers.vdf` and `%LOCALAPPDATA%\Steam\local.vdf` without
modifying them. Local token parsing is not an official SteamKit2 API and may need
updates when Steam changes its format. Saved LuaShareX refresh tokens are encrypted
with Windows DPAPI per account in `%APPDATA%\LuaShareX\accounts.json`. The old
`token.txt` is migrated when its account can be identified, then removed after
successful encrypted storage. No password is saved.

## Export format

One game exports as `<appid>.lua`; selecting several exports a `.zip` with
one `<appid>.lua` per game. Depot keys are fetched lazily at export time,
only for the games you select.

## Build

```powershell
dotnet build LuaShareX.csproj -c Release
```

Run the Windows regression checks (synthetic accounts, no Steam login):

```powershell
dotnet run --project Tests/LuaShareX.Tests.csproj -c Release
```

Add `-- --render` to render the real login XAML with synthetic accounts to
`Tests/bin/Release/net8.0-windows/login-preview.png`.

## Release

Push a tag like `v1.0.0` — the [release workflow](.github/workflows/release.yml)
builds the win-x64 zip and publishes the GitHub Release the updater consumes.
