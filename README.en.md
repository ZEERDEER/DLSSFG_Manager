# DLSSFG Manager

English · [中文](README.md)

**DLSSFG Manager** is a graphical front end for [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86), the project that brings DLSS Frame Generation to RTX 20 / 30 series GPUs by dropping a proxy DLL (`version.dll`, `winmm.dll`, `dxgi.dll`, …) plus a `dlssg_sm86.ini` next to a game's rendering executable. Doing that by hand means finding the right EXE, picking a DLL and tracking upstream releases. This tool does all of that.

> This project only deploys and manages the patch. The frame-generation work itself — kernels, runtime, compatibility — is entirely by the upstream author **sdli1995**. Patch files are downloaded on demand from the upstream repository and verified; this repository ships no DLLs.

## Features

- **Game scanning** — on start it reads the Steam registry keys, `libraryfolders.vdf` and each app manifest, then locates the executable that actually renders (Unreal's `Binaries\Win64\*-Win64-Shipping.exe` is recognised; launchers, crash reporters and 32-bit binaries are skipped). Non-Steam games: **添加游戏** (Add game) picks a folder or EXE, or drop them onto the window.
- **Proxy DLL recommendation** — known-game rule → EXE import table → default `version.dll`; every row has a one-click picker, hover it for the reasoning.
- **Install / update / switch / uninstall** — per row or in bulk. Install downloads and verifies the patch, then copies it into the game folder (an existing INI is kept). Switching removes only a DLL that is recognisably this patch; unrelated files such as a game's own `dbghelp.dll` are never touched. Uninstall simply deletes the patch DLL and the INI.
- **Upstream updates** — latest stable release → tag resolved to a fixed commit → only the needed files are downloaded and verified by size and Git blob hash. Deployed DLLs are identified by hashing them against the upstream files, so no local records are needed.
- **Zero footprint** — no registry writes, no files next to the executable, no saved settings; downloads go to the system temp folder and are removed on exit.
- Localised game names from the Steam store, icons from the game EXE or Steam's cache; light/dark theme following Windows.

## Download & run

Grab a build from [Releases](https://github.com/ZEERDEER/DLSSFG_Manager/releases):

| File | Notes |
|---|---|
| `DLSSFG-Manager-x.y.z-win-x64.zip` | A single `DLSSFG Manager.exe` (~47 MB) with the runtime built in: unzip and run, nothing to install. |
| `DLSSFG-Manager-x.y.z-win-x64-lite.zip` | A single exe (~1 MB) that needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows x64); if it is missing, the app points you to the download. |

Requires Windows 10 1809+ or Windows 11, 64-bit. Run the exe from anywhere — it creates no files beside itself.

## How to use

1. On start the app scans your Steam library and queries the latest upstream release. For non-Steam games click **添加游戏** (Add game) or drag an EXE/folder in.
2. A row flagged as "multiple EXEs found" needs confirmation: right-click → **选择实际渲染 EXE** (choose the rendering EXE). The patch always goes next to the chosen EXE.
3. The proxy picker on each row defaults to the recommendation; when in doubt, keep it.
4. Click **安装** (Install) on the row, or tick rows and use **安装所选** (Install selected). The patch is downloaded and verified; each DLL is fetched once per session.
5. Launch the game to verify frame generation. "Installed" only means the files are in place; the patch writes its own logs to `dlssg_sm86\logs` in the game folder (right-click → open log folder).

Afterwards: rows behind the latest release show "可更新" (updatable); **更新** (Update) keeps the deployed proxy name and INI. Picking a different proxy turns the button into **切换** (Switch). **卸载** (Uninstall) deletes the patch DLL and INI.

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
dotnet publish Manager.App -c Release -r win-x64 --self-contained true  -o publish\full   # single file, runtime included
dotnet publish Manager.App -c Release -r win-x64 --self-contained false -o publish\lite   # single file, uses the installed runtime
```

```
Manager.Core/    business logic: scanning, recommendation, installer, upstream download + verification, Steam names (no UI dependency)
Manager.App/     WinForms UI (.NET 10, native dark mode, custom-drawn list and integrated caption)
Manager.Tests/   tests with a tiny assertion harness, fake PE files and fake GitHub / Steam endpoints
docs/            interface contract and the original implementation plan
```

## FAQ

- **GitHub rate limit** — unauthenticated requests are limited to 60 per hour, which is plenty; if you hit it, wait or set a `GITHUB_TOKEN` environment variable.
- **Offline** — scanning and status work offline; installing and updating need to download from upstream.
- **"Installed · unknown version"** — a proxy DLL and INI are present but the DLL's hash matches no file of the latest upstream release (older build or modified). Click **重装** (Reinstall) to replace it with the latest.

## Credits & license

- Frame-generation patch and all DLLs: [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86).
- This manager: designed by [zeer](https://github.com/ZEERDEER), released under the [MIT License](LICENSE).
- Dependency: [Newtonsoft.Json](https://github.com/JamesNewtonKing/Newtonsoft.Json) (MIT).

Not affiliated with NVIDIA, Valve or any game publisher. Use third-party patches at your own risk, especially in online games with anti-cheat.
