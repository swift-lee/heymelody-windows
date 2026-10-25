# HeyMelody for Windows / macOS

在 Windows 和 Apple Silicon Mac 上查看和调整 OPPO 耳机设置，让欢律里的常用功能也能在电脑上使用。

这是一个社区维护的桌面客户端，使用 Avalonia 和 FluentAvalonia 构建。项目提供耳机状态、降噪、均衡器和校准界面，支持 OPPO、OnePlus、realme 的部分耳机型号；与 OPPO 官方没有隶属关系。

[下载与发布](https://github.com/CberYellowstone/heymelody-windows/releases) · [使用说明](docs/usage.md) · [版本记录](docs/history/release-notes.md) · [反馈问题](https://github.com/CberYellowstone/heymelody-windows/issues)

## 可以做什么

- 查看耳机电量、连接状态、设备信息和固件版本。
- 切换降噪、通透等模式，调整型号提供的细分选项。
- 使用内置均衡器预设，编辑、保存自定义 EQ；支持相应型号的 BassWave™ 动态低音。
- 运行耳塞贴合度测试和黄金听感向导，查看左右耳结果，并比较增强前后的试听。
- 调整耳机支持的游戏模式、Hi-Res、空间音效及其他设置。
- 展示耳机图片、动画或三维模型；关闭主窗口后，托盘后台继续管理耳机。

项目仅使用欢律 App 的配置与资源，预装目录包含 85 个耳机产品 ID；这是目录覆盖，不代表所有型号和功能都已通过实机验证。实际可用设置还取决于欢律配置、设备能力和项目已实现的协议，固件条件仅产生提醒。当前来源与升级说明见[欢律单源说明](docs/research/heymelody-only.md)。

## 安装与开始使用

Apple Silicon Mac 需要 **macOS 15 及以上**，打开 DMG 后将完整的 `HeyMelody.app` 复制到“应用程序”，详见 [macOS 安装说明](docs/macos.md#使用)。

Windows 平台基线为 **Windows 10 2004（Build 19041）及以上**，以下步骤适用于 Windows。发布包自带 .NET 运行时，日常使用无需安装 SDK；下载时以发布页实际提供的架构为准。

1. 在[发布页](https://github.com/CberYellowstone/heymelody-windows/releases)选择可用的预编译包，完整解压到一个目录。单架构包应与 Windows 架构匹配。
2. 先在 Windows 蓝牙设置中配对并连接耳机。
3. 双击 `Start-HeyMelody.cmd`，通过设备管理器选中要管理的耳机并保存名单。
4. 在主界面选择耳机，等待连接和状态读取完成，再调整设置。

本地构建另可封装为 Inno Setup 安装器，安装到当前用户的 `%LOCALAPPDATA%\Programs\HeyMelody`，也可在目录页自定义位置，无需管理员权限。开始菜单只添加一个 HeyMelody 入口；可从 Windows“应用和功能”卸载，保留用户设置和已下载资源。此安装器由独立脚本生成，GitHub Actions 继续生成目录与 ZIP，发布页未必提供安装 EXE，详见[安装器说明](docs/installer.md)。

请保留包内的 `backend`、`ResourceBundles`、DLL 和许可证文件。关闭主窗口后后台仍在托盘运行；完全退出时，右键托盘图标选择“退出”。更新前先关闭所有界面并退出旧版后台，再使用完整的新包，避免新界面连接到旧后台。

没有耳机也可以体验界面：运行包内的 `Start-3D-Demo.cmd`，进入离线演示。Demo 的控件操作只修改模拟数据，不代表演示型号的实际功能。

详细的连接、EQ、校准、资源设置和常见问题见[使用说明](docs/usage.md)。

## 当前进展

本分支增加 **macOS 15+ / Apple Silicon** 原生实现，版本保持 **0.9.2**。DMG 内含自包含运行时，将 `HeyMelody.app` 复制到“应用程序”后启动；先在系统设置中配对连接耳机，再通过应用的设备管理器保存名单。后台位于菜单栏，关闭窗口后继续运行；⌘Q 或菜单栏“退出”执行所有窗口的协同退出。黄金听感音乐试听需要明确选择蓝牙音频输出。

本轮 DMG 使用本地 ad-hoc 签名，尚未公证。macOS 蓝牙连接、控制和校准效果尚待耳机真机验证；本机 Metal、离线与包验收边界及构建方法见 [macOS 说明](docs/macos.md)。

Windows 部分沿用 **0.9.2** 的控制服务缓存修复，并在连接失败时显示具体步骤和原因。双击底栏中的当前耳机刷新状态，三击重新连接（也可按 F5／Shift+F5）。Air4 Pro 真机恢复与 Windows ARM64 硬件尚未验证；改动与验证范围见[版本记录](docs/history/release-notes.md)。

项目仍在完善设备适配。不同型号、蓝牙适配器、手机同时控制及 ARM64 硬件行为需要分别验证；OTA 固件更新、录音、翻译和健康功能尚未提供。默认开启在线资源，可在设置中关闭；缓存和本地设置的说明见[使用说明](docs/usage.md#资源与本地数据)。

## 构建与贡献

源码构建需要 **.NET 10 SDK**。从仓库根目录运行：

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
```

普通构建使用仓库自带的资源包，不需要原始 APK 或本地研究档案。模块、运行、测试和打包步骤见[开发指南](docs/development.md)。

本分支使用 Filament 渲染三维模型：Windows 默认 Vulkan，也可手动选择 OpenGL；macOS 使用 Metal。模型帧率可选 30帧、60帧、无限制，默认无限制。Linux 目前保留渲染构建与平台接口，完整蓝牙后端留待后续实现，见 [Filament 构建说明](docs/filament-renderer.md)。

欢迎通过 [Issues](https://github.com/CberYellowstone/heymelody-windows/issues) 提交问题、设备兼容性反馈和功能建议，也欢迎提交 Pull Request。反馈时请注明应用版本、操作系统版本与架构、耳机型号与固件、配置内容版本和复现步骤；分享日志前移除蓝牙地址、密钥等个人数据。开始修改项目时，请阅读[协作规则与文档导航](AGENTS.md)。

## 许可与来源

当前仓库尚未提供顶层源码许可证。第三方库和原厂耳机资源保留各自的版权、许可及来源声明；具体信息见 [SOURCE-NOTES.md](packaging/SOURCE-NOTES.md)、[第三方许可](docs/licenses/)和发布包中的 notices。项目的研究依据与公开结论见[研究索引](docs/research/README.md)。
