# DLSSFG Manager

English · [中文](README.md)

**DLSSFG Manager** is a graphical front end for [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86), the project that brings DLSS Frame Generation to RTX 20 / 30 series GPUs by dropping a proxy DLL (`version.dll`, `winmm.dll`, `dxgi.dll`, …) plus a `dlssg_sm86.ini` next to a game's rendering executable. Doing that by hand means finding the right EXE, picking a DLL, tracking upstream releases and keeping backups. This tool does all of that.

> This project only deploys and manages the patch. The frame-generation work itself — kernels, runtime, compatibility — is entirely by the upstream author **sdli1995**. Patch files are downloaded on demand from the upstream repository and verified; this repository ships no DLLs.

## Features

- **Game scanning** — reads the Steam registry keys, `libraryfolders.vdf` and each app manifest, then locates the executable that actually renders (Unreal's `Binaries\Win64\*-Win64-Shipping.exe` is recognised; launchers, crash reporters and 32-bit binaries are skipped). Any folder can be added, and EXEs or folders can be dropped onto the window.
- **Proxy DLL recommendation** — saved choice → known-game rule → EXE import table → default `version.dll`; every row has a one-click picker.
- **Install / update / switch / uninstall** — per row or in bulk. Existing DLLs are backed up and overwritten, an existing INI is kept byte-for-byte. Switching proxies removes only the proxy this tool deployed (hash-verified); unrelated files such as a game's own `dbghelp.dll` are never touched.
- **Transactional file operations** — check for a running game process and write access → back up and verify → stage and replace → verify → commit the install record. Failures roll back; an unfinished operation is recovered on the next start from its journal.
- **Upstream updates** — latest stable release → tag resolved to a fixed commit → only the needed files are downloaded, verified by size and Git blob hash, and fingerprinted with SHA-256. Works offline from the verified cache or from an imported local checkout.
- **Restore** — everything that gets replaced is kept under `data\backups` and can be restored.
- Localised game names from the Steam store (cached), icons from the game EXE or Steam's cache, all usable offline; light/dark theme following Windows.

## Download & run

Grab a build from [Releases](https://github.com/ZEERDEER/DLSSFG_Manager/releases):

| File | Notes |
|---|---|
| `DLSSFG-Manager-x.y.z-win-x64.zip` | Self-contained: unzip and run, no runtime to install (~110 MB). |
| `DLSSFG-Manager-x.y.z-win-x64-lite.zip` | ~1 MB, requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64). |

Requires Windows 10 1809+ or Windows 11, 64-bit. Extract anywhere writable and start `DLSSFG Manager.exe`. Settings, cache, backups and install records live in the `data` folder next to the executable — the whole folder is portable.

## How to use

1. Click **扫描游戏** (Scan). Steam games appear automatically; for others use **添加 ▾** (Add) or drag an EXE/folder in.
2. A row flagged as "multiple EXEs found" needs confirmation: right-click → **选择实际渲染 EXE** (choose the rendering EXE). The patch always goes next to the chosen EXE.
3. The proxy picker on each row defaults to the recommendation; hover it to see why. When in doubt, keep the default.
4. Click **安装** (Install) on the row, or tick rows and use **安装所选** (Install selected). The first install downloads and verifies the patch; later installs use the cache.
5. Launch the game to verify frame generation. "Installed" only means the files are in place; the patch writes its own logs to `dlssg_sm86\logs` in the game folder (right-click → open log folder).

Afterwards: **检查更新** (Check updates) marks rows as updatable; **更新** (Update) keeps the deployed proxy name and INI. Picking a different proxy turns the button into **切换** (Switch). **卸载** (Uninstall) backs up and removes the DLL and INI. Right-click → **恢复最近备份** restores the previous state.

## Known-game rules

| Game | Steam AppID | Rendering EXE | Default proxy |
|---|---:|---|---|
| Stellar Blade | 3489700 | `SB\Binaries\Win64\SB-Win64-Shipping.exe` | `version.dll` |
| Cyberpunk 2077 | 1091500 | `bin\x64\Cyberpunk2077.exe` | `dxgi.dll` |
| Nioh 3 | 3681010 | `Nioh3.exe` | `winmm.dll` |

Other games are recommended from the EXE import table, following the upstream preference order `version → winmm → dbghelp → dinput8 → dxgi → d3d12`. The table lives in `Manager.Core/ProxyRecommender.cs`; contributions welcome.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/ZEERDEER/DLSSFG_Manager.git
cd DLSSFG_Manager
dotnet build DLSSFG_Manager.sln
dotnet run --project Manager.Tests          # tests have no Windows dependency and also run on Linux
dotnet publish Manager.App -c Release -r win-x64 --self-contained true  -o publish\full
dotnet publish Manager.App -c Release -r win-x64 --self-contained false -o publish\lite
```

```
Manager.Core/    business logic: scanning, recommendation, transactional installer, upstream updater, Steam names (no UI dependency)
Manager.App/     WinForms UI (.NET 10, native dark mode, custom-drawn list and integrated caption)
Manager.Tests/   tests with a tiny assertion harness, fake PE files and fake GitHub / Steam endpoints
docs/            interface contract and the original implementation plan
```

## FAQ

- **GitHub rate limit** — unauthenticated requests are limited to 60 per hour, which is plenty; if you hit it, wait or set a `GITHUB_TOKEN` environment variable.
- **Offline use** — after one download, or after importing an upstream checkout (**⋯ → import local patch folder**), installs and reinstalls need no network.
- **"Modified externally"** — a DLL deployed by this tool was changed by something else. Switch and uninstall stop to avoid deleting the wrong file; use **重装** (Reinstall) to overwrite, or restore the last backup.

## Credits & license

- Frame-generation patch and all DLLs: [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86).
- This manager: designed by [zeer](https://github.com/ZEERDEER), released under the [MIT License](LICENSE).
- Dependency: [Newtonsoft.Json](https://github.com/JamesNewtonKing/Newtonsoft.Json) (MIT).

Not affiliated with NVIDIA, Valve or any game publisher. Use third-party patches at your own risk, especially in online games with anti-cheat.
