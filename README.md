# DLSSFG Manager

[English](README.en.md) · 中文

**DLSSFG Manager** 是 [sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86) 的图形化管理器：那个项目让 RTX 20 / 30 系显卡在支持 DLSS 帧生成的游戏里也能开启帧生成，方法是把一个代理 DLL（`version.dll`、`winmm.dll`、`dxgi.dll` 等）和一份 `dlssg_sm86.ini` 放到游戏渲染 EXE 旁边。手动操作要自己找 EXE、挑 DLL、盯更新、留备份，这个管理器把这些都接过来了。

> 本项目只负责"部署与管理"。帧生成本身的实现、内核和兼容性工作全部来自上游作者 **sdli1995**，补丁文件也是从上游仓库按需下载并校验的，本仓库不包含任何 DLL。

## 功能

- **扫描游戏**：读取 Steam 注册表、`libraryfolders.vdf` 和每个游戏的安装清单，定位真正在渲染的 EXE（识别 UE 的 `Binaries\Win64\*-Win64-Shipping.exe`，排除启动器、崩溃报告程序和 32 位程序）；也可以添加任意目录，或直接把 EXE / 文件夹拖进窗口。
- **推荐代理 DLL**：按"用户已保存的选择 → 已知游戏规则 → EXE 导入表 → 默认 `version.dll`"的顺序推荐，每一行都可以一键改选。
- **安装 / 更新 / 切换 / 卸载**：单个游戏或勾选后批量执行。已有的旧 DLL 自动备份后覆盖，已有的 INI 原样保留；切换代理时只移除本项目部署且校验一致的旧代理，同目录里游戏自带的 `dbghelp.dll` 之类文件不会被碰。
- **事务式文件操作**：检查游戏进程、写入权限 → 备份并校验 → 暂存替换 → 校验结果 → 写入安装记录。中途失败自动回滚；程序异常退出后，下次启动按待执行记录恢复。
- **上游更新**：查询最新稳定 Release → 解析 tag 对应的提交 → 只下载需要的文件，按大小和 Git blob 哈希校验，另存 SHA-256 作为版本指纹；支持离线使用已校验缓存，或导入本地补丁目录。
- **恢复备份**：每次改动前的文件都保存在 `data\backups`，可随时恢复到操作前的状态。
- 中文游戏名来自 Steam 商店（本地缓存），图标取自游戏 EXE 或 Steam 缓存，全程可离线；跟随系统的浅色 / 深色主题。

## 下载与运行

到 [Releases](https://github.com/ZEERDEER/DLSSFG_Manager/releases) 下载：

| 文件 | 说明 |
|---|---|
| `DLSSFG-Manager-x.y.z-win-x64.zip` | 自包含版，解压即用，无需安装任何运行时（约 110 MB）。 |
| `DLSSFG-Manager-x.y.z-win-x64-lite.zip` | 精简版（约 1 MB），需要先安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（Windows x64，"Desktop Runtime"）。 |

系统要求：Windows 10 1809 或更新 / Windows 11，64 位。解压到任意可写目录后运行 `DLSSFG Manager.exe`；设置、缓存、备份和安装记录都保存在同目录的 `data` 文件夹里，整个文件夹可以随意搬走。

## 使用步骤

1. 点击右上角 **扫描游戏**。Steam 库里的游戏会自动出现；非 Steam 游戏用 **添加 ▾** 选目录或 EXE，或直接拖进来。
2. 如果某一行被标成"检测到多个 EXE"，右键该行 → **选择实际渲染 EXE**。补丁总是安装到所选 EXE 的同目录。
3. 每一行的代理 DLL 选择器默认是推荐值，鼠标悬停可以看到推荐依据；不确定就保持默认。
4. 点该行的 **安装**（或勾选多行后点底部 **安装所选**）。首次安装会从上游下载并校验补丁；之后直接用缓存。
5. 启动游戏验证帧生成是否生效。管理器里的"已安装"只表示文件已部署；日志在游戏目录的 `dlssg_sm86\logs`（右键该行 → 打开日志目录）。

后续操作：**检查更新** 后状态列会提示"可更新"，点 **更新** 会保留原来的代理名称和 INI；改选另一个代理后按钮变成 **切换**；**卸载** 会先备份再移除 DLL 与 INI；右键 → **恢复最近备份** 可以回到上一步之前。

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
dotnet publish Manager.App -c Release -r win-x64 --self-contained true  -o publish\full
dotnet publish Manager.App -c Release -r win-x64 --self-contained false -o publish\lite
```

```
Manager.Core/    业务核心：扫描、推荐、安装事务、上游更新、Steam 名称（无界面依赖）
Manager.App/     WinForms 界面（.NET 10，原生深色模式，自绘列表与窗口标题区）
Manager.Tests/   测试（自带轻量断言，构造假 PE 文件和假 GitHub / Steam 接口）
docs/            接口约定与最初的实施计划
```

## 常见问题

- **上游 API 限流**：GitHub 未登录时每小时 60 次请求，正常够用；如果提示限流，可以稍后再试，或设置环境变量 `GITHUB_TOKEN` 提高额度。
- **没有网络**：导入过一次上游目录（**⋯ → 导入本地补丁目录**）或下载过一次之后，安装和重装都不需要联网。
- **游戏名显示英文**：中文名来自 Steam 商店接口，首次需要联网，之后缓存 30 天；非 Steam 游戏显示 EXE 名。
- **状态显示"已被外部修改"**：管理器部署的 DLL 被其他程序改动过。为避免误删，切换和卸载会停止；用该行的"重装"覆盖，或"恢复最近备份"。

## 致谢与许可

- 帧生成补丁及全部 DLL：[sdli1995/dlssg_for_sm86](https://github.com/sdli1995/dlssg_for_sm86)。
- 本管理器：Designed by [zeer](https://github.com/ZEERDEER)，以 [MIT 许可证](LICENSE) 开源。
- 依赖：[Newtonsoft.Json](https://github.com/JamesNewtonKing/Newtonsoft.Json)（MIT）。

本软件与 NVIDIA、Valve 及任何游戏厂商无关。使用第三方补丁请自行评估风险，尤其是带反作弊的在线游戏。
