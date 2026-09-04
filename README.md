![xlcore_sized](https://user-images.githubusercontent.com/16760685/197423373-b6082cdb-dc1f-46db-8768-3f507f182ba8.png)

# XIVLauncher.Core

> **基于 [rankynbass/XIVLauncher.Core](https://github.com/rankynbass/XIVLauncher.Core)（RB 分支）移植的 Proton/UMU 支持改进。**

对原版 CN XIVLauncher.Core 的主要更改：

### 🎮 Proton/UMU 游戏运行
- 用 **Proton + UMU-Launcher** 替换了原有的 managed-Wine 方案
- Proton 一律通过 **umu-launcher（umu-run）** 启动，不再直接调用 proton 脚本；UMU 提供类似 Steam 的运行环境，使 Proton 内置的 DXVK/NVAPI 正常工作
- 支持 Proton 版本选择（proton-xiv 等内置版本、自定义 Proton 路径）
- 支持 UMU Launcher（System / Builtin 两模式）
- 完整的 ESync/FSync/NTSync 同步原语支持

### 🎨 图形栈直接使用 Proton 内置组件
- 不再单独下载/安装 DXVK 与 dxvk-nvapi 覆盖到 prefix——DXVK、NVAPI/DLSS 全部由 Proton 内置版本提供
- DLSS 由 Proton 的 dxvk-nvapi 在 umu 环境下按需启用

### 🧹 界面简化
- 彻底移除了旧的 managed-Wine / 自定义 Wine 启动模式及其代码路径（WineStartupType、WineEnv、ESync/MSync/FSync、DXMT/MetalFX 等）
- 移除了 DXVK / Nvapi 版本选择、自定义路径等不再需要的配置项
- 运行模式只保留 Proton（内置版本）/ Custom（自定义 Proton 路径）两个选项

[反馈频道](https://qun.qq.com/qqweb/qunpro/share?_wv=3&_wwv=128&inviteCode=CZtWN&from=181074&biz=ka&shareSource=5)

## 在 Steam Deck 上使用

如果您想在 Steam Deck 上使用 XIVLauncher，请随时[按照我们常见问题解答中的指南](https://aonyx.ffxiv.wang/faq/steamdeck)。 如果您遇到问题，可以[加入我们的反馈频道](<[https://discord.gg/3NMcUV5](https://qun.qq.com/qqweb/qunpro/share?_wv=3&_wwv=128&inviteCode=CZtWN&from=181074&biz=ka&shareSource=5)>) - 请不要使用 GitHub issues 进行故障排除，除非您确定您的问题是与 XIVLauncher 的代码相关。

## Building & Contributing

1. Clone this repository with submodules
2. Make sure you have a recent(.NET 6.0.400+) version of the .NET SDK installed
3. Run `dotnet build` or `dotnet publish`

Common components that are shared with the Windows version of XIVLauncher are linked as a submodule in the "lib" folder. XIVLauncher Core can run on Windows, but is by far not as polished as the [original Windows version](https://github.com/goatcorp/FFXIVQuickLauncher). Windows users should not use this application unless for troubleshooting purposes or development work.

## 分发

XIVLauncher Core 具有适用于各种 Linux 发行版的社区包。 请注意，**只有 Flathub 版本是官方版本**，但其他版本是由社区成员**打包**。 社区包可能并不总是最新的，或者可能有损坏的版本或包含正在测试的功能（特别是如果标记为不稳定或 git）。 我们对其安全性或可靠性不承担任何责任。

| 仓库                                                                                  | 状态                                                                                                                                                                                                                                                |
| ------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [**Flathub (official)**](https://flathub.org/apps/details/cn.ottercorp.xivlaunchercn) | ![Flathub](https://img.shields.io/flathub/v/cn.ottercorp.xivlaunchercn)                                                                                                                                                                             |
| [AUR](https://aur.archlinux.org/packages/xivlauncher-cn-git)                          | ![AUR version](https://img.shields.io/aur/version/xivlauncher-cn-git)                                                                                                                                                                               |
| [MPR (Debian+Ubuntu)](https://mpr.makedeb.org/packages/xivlauncher-cn)                | ![MPR package](https://repology.org/badge/version-for-repo/mpr/xivlauncher-cn.svg?header=MPR)                                                                                                                                                       |
| [PRM (Fedora+Opensuse)](https://github.com/bamdragonfly/lure-repo)                    | ![RPM package](https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com%2Fbamdragonfly%2Flure-repo%2Fmaster%2Fxivlauncher-cn%2Fversion.json&query=%24.version&prefix=v&label=RPM&color=pink)                           |
| [Chiyuki-Overlay (Gentoo)](https://github.com/IllyaTheHath/gentoo-overlay)            | ![Ebuild package](https://img.shields.io/badge/dynamic/xml?url=https%3A%2F%2Fraw.githubusercontent.com%2FIllyaTheHath%2Fgentoo-overlay%2Fmaster%2Fgames-util%2Fxivlauncher-cn%2Fversion.xml&query=%2F%2Fversion&prefix=v&label=Ebuild&color=6E56AF) |
