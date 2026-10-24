# 开发、构建与验证

本文描述 0.9.2 的源码入口和现行脚本。本轮在 Windows 本地构建，尚未发布到 GitHub。用户操作见根目录 [README](../README.md)，协作规则与文档导航见 [AGENTS.md](../AGENTS.md)，过去版本的结果见[版本记录](history/release-notes.md)。所有命令均从仓库根目录运行。

## Filament 实验分支

本分支支持 Windows x64、ARM64 打包，默认 Vulkan，在“3D模型渲染API”下拉框选择 OpenGL 可切换后端。“3D模型渲染帧率”提供 30帧、60帧、无限制三档吸附滑块，默认无限制。原生窗口验收使用 Windows x64；ARM64 为交叉构建与静态校验，硬件运行尚未验证。三维层的 macOS Metal、Linux Vulkan／EGLHeadless、ABI 2 和探测程序见 [Filament 构建与接口](filament-renderer.md)。托管构建不要求 APK 或固定 research 路径，真实三维需对应 RID 的完整原生产物；Vulkan 失败使用静态外观并提示手动选择 OpenGL。

## 环境与模块

日常客户端使用 Windows 与 .NET SDK 10，通过仓库 `NuGet.Config` 中的官方 NuGet 源及已签入的局部补丁包恢复依赖。GUI 使用 Avalonia、FluentAvalonia；Windows 平台层目标为 `net10.0-windows`。发布包是自包含应用，终端用户无需安装 SDK。

| 入口或模块 | 职责 |
| --- | --- |
| `src/HeyMelody.Host` | 托盘后台、共享蓝牙连接及状态推送；主窗口关闭后继续运行 |
| `src/HeyMelody.App` | 主窗口、设备控制与外观展示 |
| `src/HeyMelody.DeviceManager` | 独立设备管理窗口 |
| `src/HeyMelody.FitTest`、`src/HeyMelody.HearingSetup` | 独立贴合度测试与黄金听感窗口，共用后台连接 |
| `src/HeyMelody.Calibration`、`src/HeyMelody.Calibration.UI` | 校准流程及界面；输入绑定设备版本，操作受每设备租约约束 |
| `src/HeyMelody.Protocol`、`src/HeyMelody.Control`、`src/HeyMelody.Daily`、`src/HeyMelody.Probe` | 协议、设置工作流、日常状态与只读探测 |
| `src/HeyMelody.Contracts`、`src/HeyMelody.Client` | 跨进程契约与客户端通信 |
| `src/HeyMelody.Diagnostics` | 一次后台周期的统一 Debug JSONL、会话引导、故障状态与原始协议记录，见[诊断日志](diagnostics.md) |
| `src/HeyMelody.Resources`、`src/HeyMelody.Devices`、`src/HeyMelody.Artwork` | 资源解析、设备能力与外观资源 |
| `src/HeyMelody.Windows` | Windows 平台接口；`OfflineChecks` 是独立检查入口 |
| `src/HeyMelody.Audio`、`src/HeyMelody.Experiments` | 早期驱动与音频实验，另有依赖和历史验证范围 |

架构与连接流程见[后台架构](backend-architecture.md)，保留的研究工具见 [CLI 说明](cli-tools.md)。日常客户端、协议、设备配置和控制模块可从本仓库构建；早期驱动/音频实验使用相邻目录 `../manager/src/LhdcManager.Core`，对应私有伴随仓库为 `https://github.com/CberYellowstone/lhdc-manager`。该依赖属于可选实验，不是普通 GUI 构建的前置步骤。

## 源码构建与运行

按修改涉及的入口构建，例如：

```powershell
dotnet build src/HeyMelody.App/HeyMelody.App.csproj -c Release
dotnet build src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
```

设备管理、贴合度和黄金听感的独立入口分别为表中的 `DeviceManager`、`FitTest` 和 `HearingSetup` 项目，使用同样的 `dotnet build ... -c Release` 形式。

在 Windows 上运行普通客户端，先在一个终端启动指定配置的后台：

```powershell
dotnet run --project src/HeyMelody.Host/HeyMelody.Host.csproj -c Release
```

再在另一个终端启动主窗口：

```powershell
dotnet run --project src/HeyMelody.App/HeyMelody.App.csproj -c Release
```

App 也能自动启动后台；源码运行时，若输出目录没有 Host 可执行文件，会依次查找 Host 的 Debug、Release DLL。开发验证使用上面的显式后台命令确定配置；切换配置前通过托盘退出已有后台。关闭主窗口会保留后台。

`Run-3D-Demo.ps1` / `Run-3D-Demo.cmd` 提供不连接耳机的布局演示，预装模型与演示范围见[用户说明](usage.md)及[模型验证记录](research/enco-free4-dynaudio-model.md)。`Run-ReadOnly.ps1` 和 `Run-HiRes.ps1` 是保留的研究入口，运行范围见 [CLI 说明](cli-tools.md)；涉及蓝牙、设置写入或播放的操作须依实际任务授权与对应硬件条件执行。

## 界面渲染与字体

三个 GUI 入口在 AppBuilder 阶段调用 `src/HeyMelody.App/RenderingSetup.cs` 与 `Typography.cs`（以源码链接编入各程序集，不新增 DLL）：`RenderingSetup` 固定 Win32 的 AngleEgl 与 WinUI／DirectComposition 合成（Mica 依赖前者），Skia GPU 资源上限 256 MiB、字形缓存 64 MiB。合成脏矩形使用区域跟踪（`UseRegionDirtyRectClipping`、最多 8 块），左侧英雄动画与右侧设置变化不再合并成整窗重绘；Avalonia 从不回收 GPU 纹理，`GpuJanitor` 在动画让路结束、窗口失活等事件后最多每 10 秒调用一次 `PurgeUnusedResources(30 s)`，不使用空闲定时器。Host 托盘只链接 `Typography.cs`，使用 Win32 软件渲染与 RedirectionSurface，不初始化 GPU 与合成器，也不设置 Skia 缓存上限（见[后台架构](backend-architecture.md#资源占用)）。字体部分（`Typography.cs`）GUI 与托盘相同：界面区域性固定为 zh-CN，默认字体为系统 Microsoft YaHei UI，汉字回退先于按区域打分固定到简体中文字体，550–699 字重使用真实粗体字面。没有雅黑的系统使用随附的 Noto Sans SC（`fonts/`，OFL-1.1）；Linux 无头预览与测试把该目录登记到 fontconfig，以路径方式打开字体，避免 Avalonia 12.1 从流加载字体时随窗口累积原生内存。共享样式与间距令牌在 `src/HeyMelody.App/Styles/HeyMelodyTheme.axaml`，代码构建的界面使用 `UiMetrics.cs` 中的同名数值。

设 `HEYMELODY_RENDER_DIAGNOSTICS=1` 或在窗口中按 Ctrl+Shift+F12 显示 Avalonia 的帧率、渲染与布局耗时叠加层，三维模型另显示 Filament 帧统计。验收 120 Hz 时在 120 Hz 显示器上展开／折叠设置项、拖动模型，并关注叠加层而不是空闲窗口的数值。

所有 Windows GUI 显式选择 DXGI 枚举首适配器，绕过 Avalonia 的 ARM64 Adreno 默认过滤；不按 GPU 品牌或架构选卡，也不新增独显优先策略。应用设置的“使用 Basic Render 兼容模式”保存为 `%LOCALAPPDATA%\HeyMelody\preferences.json` 中的 `uiBasicRenderMode` 布尔值，只有 `true` 启用，缺失或无效值使用默认 GPU。各 GUI 在渲染初始化前独立读取同一设置；保存后重新打开主窗口、设备管理器及校准窗口生效，不必退出托盘后台。此选项不改变 Filament 后端、画质或动画。

Basic 模式通过 DXGI `EnumWarpAdapter` 查询 WARP LUID，再匹配 Avalonia 的适配器标识，不依赖显示名称。ANGLE 固定 D3D11；WARP 查询、匹配或初始化失败时退到 Avalonia Software 并记录原因，不改回硬件。Software 与 WARP 是不同路径，不能把后备写成已启用 WARP；运行中设备丢失不承诺自动切换后端。旧体验版的 `HEYMELODY_UI_ADAPTER` 不再参与选卡。背景见[本机诊断](research/ui-lag-arm64-2026-10-23.md)和[上游历史](research/avalonia-adreno-history-2026-10-23.md)。定向策略检查使用 App.Tests 的 `--rendering-policy-tests`，设置检查使用 `--rendering-settings-tests`；原生检查使用模拟服务，不连接耳机。没有新增应急启动参数，无法显示时可手动修改上述配置字段为 `true` 后重开界面。

## 按改动选择验证

`tests/` 下的检查是可执行项目，使用 `dotnet run`，不是 `dotnet test`。Linux 上可以运行无头的 App、DeviceManager、Calibration.UI、Client、Calibration、Protocol、Resources、Devices、Control 与 Daily 检查；ModelPreview 的默认运动检查已在 Linux x64 使用 `dotnet run --project tests/HeyMelody.ModelPreview.Tests -c Release` 通过，无需指定 RID。`--native*` 模式、Probe／Audio 检查与 `Windows.OfflineChecks` 只在 Windows 运行。Linux 可用 `-p:EnableWindowsTargeting=true` 编译 Host 做静态检查；Host.Tests 现可在 Linux 完整运行（含能耗策略、状态写盘与 IPC 检查），2026-10-22 日志有 110 个 PASS 行，其中 2 项只在 Windows 执行的检查（真实 WASAPI 播放器的致命初始化；试听线程空闲释放）在 Linux 上提前返回，先打印 SKIP 再计为 PASS，实际执行 108 项；这 2 项须在 Windows 另行运行，不能算作已验证。可用内存较少时，构建加 `-nodeReuse:false -p:UseSharedCompilation=false` 并在测试前执行 `dotnet build-server shutdown`，以免常驻编译进程挤占测试内存。先选择能覆盖改动的检查，例如协议或界面：

```powershell
dotnet run --project tests/HeyMelody.Protocol.Tests/HeyMelody.Protocol.Tests.csproj -c Release
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release
```

平滑滚动及设备栏可分别运行定向检查（无真实设备操作），以覆盖滚轮动画、触摸板连续输入和末端惯性、有界回弹、标题触摸模拟、嵌套边界、直接定位、内容收缩后的布局和横向居中：

```powershell
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release -- --smooth-scroll-tests
dotnet run --project tests/HeyMelody.App.Tests/HeyMelody.App.Tests.csproj -c Release -- --device-bar-tests
```

标题触摸回归应区分轻触展开与越过拖动阈值后的滚动，并检查 Footer 编辑器及嵌套滚动页不会被外层接管。触摸板需区分突然结束与系统递减尾包，并验证非零惯性撞边、反向、程序接管及卸载；边界除 Offset 外还须核对实际绘制位置。当前专项未覆盖笔输入模拟；模拟指针和合成渲染结果不证明真实触屏、笔或触控板手感，也不证明 ARM64 硬件运行。

本地 `SmoothScroll.Avalonia.Interaction` 补丁包由精确源提交构建，独立版本只从 `vendor/nuget` 解析；官方 Controls 包继续使用 12.0.0.14。普通构建无需下载研究源件或应用补丁。来源、修改清单及可选手动重建步骤见 [vendor 说明](../vendor/smooth-scroll/README.md)。

完整默认检查入口为：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Client.ps1 -TestsOnly
```

`Build-Client.ps1` 保留原十三个默认 runner，并加入独立 Filament 便携检查，包含资源、校准、GUI 和 `Windows.OfflineChecks`。默认检查通过模拟、资源与静态断言验证实现；应按改动选取验证，完整发布不是每次编辑的默认步骤。`HeyMelody.ModelPreview.Tests` 是独立模型检查；App、DeviceManager 和模型相关的 Windows 原生窗口检查也需单独运行。具体命令和适用条件见[三维模型渲染](model-rendering.md)及[研究文档索引](research/README.md)。

欢律单源改造的 UI 检查限于资源设置区、变更的电量／标题图标和校准步骤，其他默认 runner 照常执行。测试生成的完整 PNG 保留本地，不向会话载入整套截图；局部原生窗口检查可使用 App.Tests 的 `--native-smoke`，该模式不连接耳机或打开系统设置。

验证记录须说明对应层级与条件：

- 协议模拟、资源解析和无头 UI 检查证明对应代码或布局行为。
- Windows 原生窗口检查证明所用架构、驱动和渲染后端下的结果。
- 静态发布校验检查包结构、版本、运行时、资源及许可材料，不连接蓝牙。
- 真机记录须注明型号、固件、应用版本、命令范围与环境；旧 X4 记录不能证明所有目录型号、后续版本或 ARM64 实机行为。

后台内存与能耗的 Windows 测量使用只读脚本 `tools/Measure-HostMemory.ps1`（Windows PowerShell 5.1 或 PowerShell 7，加 `-NoProfile`）：按命令行模式区分托盘、`--worker`、`--catalog-worker` 与 `HeyMelody.exe`，列出各进程的私有工作集、工作集、私有字节、线程和句柄数，另有 CPU 用户／内核／合计毫秒（cpuUserMs、cpuKernelMs、cpuTotalMs）、相邻采样间的 CPU 百分比（cpuPercent，按逻辑处理器数归一，口径同任务管理器“详细信息”）、每秒上下文切换数（contextSwitchesPerSec）、priorityClass、powerThrottling（eco、default、custom 或 unknown）和 ignoreTimerResolution，并按角色汇总；读取电源节流状态时 Add-Type 会编译一个小的 P/Invoke 辅助类型（Windows PowerShell 5.1 在 `%TEMP%` 写临时编译文件，编译失败时 powerThrottling 为 unknown）；可用 `-Samples`、`-IntervalSeconds` 采样，用 `-OutputPath` 另存新的 JSON（拒绝覆盖已有文件，只含系统、CPU 与 Host 版本和计数，不含命令行或蓝牙地址）。脚本不做任何设备操作。改前改后须在同一台机器、同一连接状态下比较；比较效率模式时，先在所有窗口关闭至少 15 秒后测量，再打开主窗口测量；Linux 探针数字不能代替它，范围与未验证项见[后台内存审计](research/backend-memory-audit.md)与[后台能耗](research/backend-energy.md)，其他方向的待办见[优化待办](optimization-backlog.md)。

内置资源仅包含欢律的 85 个耳机产品 ID。这是目录覆盖数量；未知 codec、缺少 SPP 或仅外部控制路由的功能会被禁用，目录条目本身不是硬件验证结论。跨窗口并发、校准流程、手机同时操作、休眠恢复及特定硬件的渲染仍需相应范围的实测证据。

## 发布与包校验

仅在任务涉及构建发布包时执行。脚本默认版本为 0.9.2，`-Version` 同时选择程序集与包版本，本 Filament 试验默认生成自包含 Windows x64 目录和 ZIP；本地生成产物不等于向 GitHub 发布：

```powershell
pwsh -NoProfile -File .\Build-Client.ps1 -Version 0.9.2
```

已有客户端目录可通过独立的 `Build-Installer.ps1` 封装为 Inno Setup 安装器；脚本读取包内 `manifest.json` 的版本，不重新编译客户端。需要本机安装 Inno Setup，默认包装 x64，可用 `-Architectures x64,arm64` 选择包内已有的架构，`-OutputRoot` 默认指向 `release/installers`，`-InnoCompilerPath` 可指定 `ISCC.exe`。例如在 PowerShell 7 中运行：

```powershell
.\Build-Installer.ps1 -PackageRoot .\release\HeyMelody_Windows_0.9.2 -Architectures x64,arm64
```

安装器面向当前用户，支持自定义安装目录；开始菜单仅写入当前用户 Programs 根目录的 `HeyMelody.lnk`，卸载登记仍保留在 HKCU。安装器为特定旧 Qualcomm GPU 设置 Basic Render 兼容模式；检测范围、桌面快捷方式选择的保留方式、设置保全和验证边界见[安装器说明](installer.md)。该脚本不接入 `Build-Client.ps1` 或 GitHub Actions，也不创建 GitHub Release；CI 继续构建、校验目录及 ZIP。

GitHub Actions 的 Windows x64、ARM64 应用包由 push 和手动运行发布：实际分支为 `refs/heads/main` 时保留 7 天，其他分支或 tag 保留 1 天。PR 仍编译并验包，但不重复发布应用包；校验回执保留 1 天。PR 的目标分支是 main 不会改变这个规则，工作流运行记录和日志继续按既有策略保留。

Windows 原生 Filament 运行时另存为 30 天 artifact 缓存，应用构建和 portability 的 Windows job 共用恢复入口。命中后校验并跳过原生编译，缓存不可用时在当前 job 重建；可信 push／手动运行会重新上传已验证运行时，滚动续存 30 天。手动运行可勾选 `force_native_rebuild` 强制重建。缓存依据、错误回退和离线检查见[Filament 构建说明](filament-renderer.md#github-actions-原生缓存)。

自 0.9.0 起所有 `dotnet publish` 统一启用 ReadyToRun（`-p:PublishReadyToRun=true`），预编译只含 IL 的界面库，避免首次展开设置、拖动模型或打开向导时的 JIT 停顿；发布包因此变大，x64 机器可交叉编译 ARM64。`-Architectures x64` 或 `-Architectures arm64` 可选择单架构；`-SkipTests` 跳过默认 runner。普通构建拒绝已有目标目录或 ZIP；可用 `-OutputRoot release/arm64` 将另一架构构建放在独立目录，校验时传入同一参数。`-FinalizeOnly` 要求对应目录已存在且 ZIP 尚不存在，并跳过默认测试与 `dotnet publish`，封装已有输出。调整参数前先阅读脚本入口与条件，避免把跳过测试的产物称为完整验证。

App、DeviceManager、FitTest、HearingSetup 共用每个 GUI 目录的运行时与依赖，Host 位于 `backend`。离线资源 `ResourceBundles`（约 34 MB）只随 GUI 目录发布一份：Host 自身目录没有该目录时读取 `..\ResourceBundles`，并把其中的路径交给界面；从构建输出直接运行的 Host 仍使用自己旁边的副本。`Build-Client.ps1` 删除 `backend/ResourceBundles` 并在清单写入 `hostResourceBundles: gui`，两个发布校验脚本据此要求后台不含第二份，旧的双副本包仍按原规则校验。GUI 和后台分别携带 `HeyMelody.Resources.dll` 及第三方声明；提取资源、Filament 和 Skottie 的声明必须随包保留。脚本从 `packaging/README.md` 复制用户说明，从 `packaging/SOURCE-NOTES.md` 复制来源与验证说明，两者是发布素材，更新时需核对发布消费关系。

PowerShell 7 的 `tools/Verify-Release.ps1` 默认检查三架构目录和 `_AllArchitectures.zip`，本分支须加 `-Architectures x64` 或 `-Architectures arm64` 选择对应包。收据默认写入输出根目录的 `HeyMelody_Windows_<版本>_STATIC-VALIDATION.json`；已有收据会被拒绝，可用 `-OutputPath` 另存新文件：

```powershell
$staticReceiptPath = 'release/HeyMelody_Windows_0.9.2_STATIC-VALIDATION.json'
if (Test-Path -LiteralPath $staticReceiptPath) { throw '已有收据，请使用 Python 校验器另存新文件。' }
pwsh -NoProfile -File .\tools\Verify-Release.ps1 -Version 0.9.2 -Architectures x64
```

可选 Python 3 校验器通过 `--package` 选择发布目录，通过 `--output` 选择新收据文件；已有输出文件会被拒绝。它检查目录中的程序集、运行时、资源与许可，不读取 ZIP；压缩包与目录的一致性须另外核对。例如：

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
python tools/verify_release.py --package release/HeyMelody_Windows_0.9.2 --architectures x64 --output artifacts/release-0.9.2-static-new.json | Out-Null
```

Python 默认检查三架构；上述目录由单架构构建生成时，添加对应的 `--architectures x64` 参数。输出文件名按本次验证另选；上例将正文输出留在收据中。两套校验均不执行应用，需保留准确的版本、架构与摘要。真实执行收据、原生窗口收据和历史只读蓝牙证据是额外证据，不能由静态检查替代。

## 来源与研究边界

研究检查若显式比较 APK 或原始归档，需原始本地输入；普通设备配置检查使用已跟踪 JSON 和内嵌资源，不要求获取完整研究原件。生成证据和二进制发布包通常不随 Git 源码提供。完整研究对象、失败分支和验证产物归档到 `research/`，公开整理与复现工具分别遵循[研究资料规范](research/organization.md)与 [tools/research 说明](../tools/research/README.md)。

Surface App 是布局参考；项目不分发其安装包、美术或字体资源。高码率/无损音频协商与耳机控制协议是独立问题，GUI 迁移或控件存在并不证明对应音频链路已验证。
