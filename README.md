# DLSSFG Manager

[English](README.en.md) · 中文

**DLSSFG Manager** 是 [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 的图形化管理器：那个项目让 RTX 20 / 30 系显卡在支持 DLSS 帧生成的游戏里也能开启帧生成，方法是把一个代理 DLL（`version.dll`、`winmm.dll`、`dxgi.dll` 等）和一份 `dlssg_sm86.ini` 放到游戏渲染 EXE 旁边。手动操作要自己找 EXE、挑 DLL、盯更新，这个管理器把这些都接过来了。

> 本项目只负责"部署与管理"。帧生成本身的实现、内核和兼容性工作全部来自上游作者 **sdli1995**，补丁文件也是从上游仓库按需下载并校验的，本仓库不包含任何 DLL。

## 功能

- **扫描游戏**：启动时自动读取 Steam 注册表、`libraryfolders.vdf` 和每个游戏的安装清单，定位真正在渲染的 EXE（识别 UE 的 `Binaries\Win64\*-Win64-Shipping.exe`，排除启动器、崩溃报告程序和 32 位程序）；非 Steam 游戏用「添加游戏」选目录或 EXE，或直接把 EXE / 文件夹拖进窗口。
- **推荐代理 DLL**：按"已知游戏规则 → EXE 导入表 → 默认 `version.dll`"的顺序推荐，每一行都可以一键改选，鼠标悬停能看到推荐依据。
- **安装 / 更新 / 切换 / 卸载**：单个游戏或勾选后批量执行。安装从上游下载并校验后复制到游戏目录（已有的 INI 原样保留）；切换代理只移除能识别为本补丁的旧 DLL，游戏自带的 `dbghelp.dll` 之类文件不会被碰；卸载直接删除补丁 DLL 与 INI。
- **上游更新**：查询最新稳定 Release → 解析 tag 对应的提交 → 只下载需要的文件，按大小和 Git blob 哈希校验。已部署的 DLL 通过哈希与上游文件比对来识别版本，因此不需要任何本地记录。
- **零痕迹**：不写注册表、不在 exe 旁边生成任何文件、不保存设置；下载的补丁放在系统临时目录，退出时清理。
- 中文游戏名来自 Steam 商店，图标取自游戏 EXE 或 Steam 缓存；跟随系统的浅色 / 深色主题。

## 下载与运行

到 [Releases](https://github.com/ZEERDEER/DLSSFG_Manager/releases) 下载：

| 文件 | 说明 |
|---|---|
| `DLSSFG-Manager-x.y.z-win-x64.zip` | 单个 `DLSSFG Manager.exe`（约 47 MB），运行时已内置，解压即用、无需安装。 |
| `DLSSFG-Manager-x.y.z-win-x64-lite.zip` | 单个 exe（约 1 MB），需要先安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（Windows x64，"Desktop Runtime"）；没装的话启动时会给出下载链接。 |

系统要求：Windows 10 1809 或更新 / Windows 11，64 位。把 exe 放在任何位置运行即可，它不会在旁边创建文件。

## 使用步骤

1. 启动后会自动扫描 Steam 库并查询上游最新版本。非 Steam 游戏点右上角 **添加游戏** 选目录或 EXE，或直接拖进来。
2. 如果某一行被标成"检测到多个 EXE"，右键该行 → **选择实际渲染 EXE**。补丁总是安装到所选 EXE 的同目录。
3. 每一行的代理 DLL 选择器默认是推荐值；不确定就保持默认。
4. 点该行的 **安装**（或勾选多行后点底部 **安装所选**）。补丁会从上游下载并校验，同一次运行里同一个 DLL 只下载一次。
5. 启动游戏验证帧生成是否生效。管理器里的"已安装"只表示文件已部署；日志在游戏目录的 `dlssg_sm86\logs`（右键该行 → 打开日志目录）。

后续操作：版本落后时状态列会提示"可更新"，点 **更新** 会保留原来的代理名称和 INI；改选另一个代理后按钮变成 **切换**；**卸载** 删除补丁 DLL 与 INI。

## 已知游戏规则

| 游戏 | Steam AppID | 渲染 EXE | 默认代理 |
|---|---:|---|---|
| 剑星 (Stellar Blade) | 3489700 | `SB\Binaries\Win64\SB-Win64-Shipping.exe` | `version.dll` |
| 赛博朋克 2077 | 1091500 | `bin\x64\Cyberpunk2077.exe` | `dxgi.dll` |
| 仁王 3 | 3681010 | `Nioh3.exe` | `winmm.dll` |

其他游戏按 EXE 的导入表推荐，并遵循上游的优先顺序 `version → winmm → dbghelp → dinput8 → dxgi → d3d12`。规则表在 `Manager.Core/ProxyRecommender.cs`，欢迎补充。

## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
git clone https://github.com/ZEERDEER/DLSSFG_Manager.git
cd DLSSFG_Manager
dotnet build DLSSFG_Manager.sln
dotnet run --project Manager.Tests          # 运行测试（不依赖 Windows，Linux 上也能跑）
dotnet publish Manager.App -c Release -r win-x64 --self-contained true  -o publish\full   # 单文件，内置运行时
dotnet publish Manager.App -c Release -r win-x64 --self-contained false -o publish\lite   # 单文件，依赖已安装的运行时
```

```
Manager.Core/    业务核心：扫描、推荐、安装、上游下载与校验、Steam 名称（无界面依赖）
Manager.App/     WinForms 界面（.NET 10，原生深色模式，自绘列表与窗口标题区）
Manager.Tests/   测试（自带轻量断言，构造假 PE 文件和假 GitHub / Steam 接口）
docs/            接口约定与最初的实施计划
```

## 常见问题

- **上游 API 限流**：GitHub 未登录时每小时 60 次请求，正常够用；如果提示限流，可以稍后再试，或设置环境变量 `GITHUB_TOKEN` 提高额度。
- **没有网络**：扫描和状态显示不需要联网；安装、更新需要从上游下载，离线时无法进行。
- **游戏名显示英文**：中文名来自 Steam 商店接口，需要联网；非 Steam 游戏显示 EXE 名。
- **状态显示"已安装 · 未知版本"**：目录里有代理 DLL 和 INI，但哈希不匹配上游最新版的任何文件（可能是旧版本或手工修改过）。点"重装"即可换成最新版。

## 致谢与许可

- 帧生成补丁及全部 DLL：[sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86)。
- 本管理器：Designed by [zeer](https://github.com/ZEERDEER)，以 [MIT 许可证](LICENSE) 开源。
- 依赖：[Newtonsoft.Json](https://github.com/JamesNewtonKing/Newtonsoft.Json)（MIT）。

本软件与 NVIDIA、Valve 及任何游戏厂商无关。使用第三方补丁请自行评估风险，尤其是带反作弊的在线游戏。
