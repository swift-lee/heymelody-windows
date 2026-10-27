# macOS Apple Silicon（0.9.2）

本分支将桌面客户端移植到 macOS 15+ 的 Apple Silicon。版本保持 0.9.2，不提供 Intel Mac 或 Linux 完整客户端。DMG 内自带 .NET 10；本轮使用 ad-hoc 签名，尚未公证。原生蓝牙后端已实现，实际耳机通信、双连、断连恢复和校准效果留待真机验证。

## 使用

将 DMG 内完整的 `HeyMelody.app` 复制到“应用程序”再启动。先在系统设置中配对并连接耳机，再通过设备管理器保存名单。后台在菜单栏继续运行；红色关闭按钮和 ⌘W 关闭当前组件，⌘Q 发起全体组件的确认退出。

主界面“+”和设备管理器的蓝牙设置按钮打开 macOS 系统蓝牙面板；“应用设置 → 管理设备”打开设备管理器。普通界面优先 Metal，开启 Basic Render 后重新打开界面使用软件绘制；Filament 三维视图固定使用 Metal。内容字体优先系统 PingFang SC，并使用系统提供的字重。

黄金听感测听仍使用耳机生成的测试音。结果页音乐试听只列蓝牙音频输出，需用户明确选择，不按名称或 UID 猜测耳机身份；选择仅在当前校准会话有效。AudioQueue 准备并核验所选输出后才启用 DSP 试听，设备消失时停止，不更改系统默认输出。

用户数据、名单、偏好、资源缓存和日志位于 `~/Library/Application Support/HeyMelody`。应用 bundle 保持完整，内含主界面、Host、设备管理、贴合度和黄金听感 helper；不应单独移动 helper 或资源。

## 构建

需要 Xcode / Apple Clang、.NET 10、PowerShell 7、CMake 3.31 和 Ninja。工具可安装到忽略的本地目录，不修改全局配置：

```powershell
pwsh -NoProfile -File tools/Prepare-MacOSBuildTools.ps1
pwsh -NoProfile -File Build-MacOS.ps1
```

网络明确失败后可使用脚本的 `-Proxy http://127.0.0.1:59991` 参数；指定代理不可用时检查本机 zshrc 的代理配置，不将凭据写入仓库。工具及 Filament 固定版本、来源与摘要由构建记录保留。

完整包包含真实 Metal 渲染库、ABI 2 探测程序、材质和许可。内嵌 Mach-O 签名后重新计算 staged 原生清单，保留原构建产物清单，最后签 helper 和外层应用；JIT 使用最小必要 entitlement。包校验检查架构、版本、依赖、资源、签名及文件摘要。

遵循 [Avalonia macOS 打包说明](https://docs.avaloniaui.net/docs/deployment/macos/)，还需签署各 `Contents/MacOS` 目录中的托管文件和配置。此类通用签名保存在扩展属性中，复制时须保留；用户通过 Finder 安装完整 App，开发验收使用 `ditto` 并在 DMG 挂载、复制后重新检查签名。不要使用会丢弃扩展属性的文件复制方式处理已签名 App。

## GitHub Actions 构建

`filament-build.yml` 的 `macos-package` job 在原生构建矩阵成功后打包，原生和应用构建均使用 `macos-15` Apple Silicon runner。应用复用同一次运行、同一提交的 `filament-osx-arm64` artifact；下载后先验证 RID、ABI、清单及文件摘要，缺少产物时直接失败。SDK 使用 .NET 10，版本输入留空时使用 `Build-MacOS.ps1` 的默认版本。

push 和手动运行成功后，在该次 Actions 的 Artifacts 下载 macOS 包，解压后使用其中的 DMG；签名仍为 ad-hoc，未公证。main 分支应用包保留 7 天，其他分支 1 天；PR 完成构建和校验但不发布应用包。限定范围的构建日志、工具链和验证收据始终保留 1 天。DMG 保留完整应用所需的签名与扩展属性，不将裸 `.app` 目录作为 artifact 分发。

发布任务沿用 Windows CI 的 `-SkipTests` 策略，另外执行 Platform、MacOS 离线检查和打包专项测试；Filament job 保留已有的渲染离线验收。它们不代表完整应用测试集或真实耳机通信通过。

跨会话单例测试让父子进程继承同一运行时用户同步存储根，同时保留不同 Unix session 和工作目录。自 .NET 10.0.13 起，macOS 的用户私有 `TMPDIR` 会影响用户域 mutex 的存储位置，不能在该测试中无意更换；绝对管道测试仍单独覆盖不同 `TMPDIR`，见 [.NET 目录规则说明](https://github.com/dotnet/docs/issues/56396)。

无人值守打包使用 `-SkipMetalCheck`，仅跳过真实 Metal probe，仍执行五入口 `--verify-installation`、DMG 挂载、`ditto` 安装副本及副本完整复验。收据中 `runtimeChecksExecuted=true`、`metalProbeExecuted=false`，并说明 Metal 显式跳过；真实蓝牙保持未验证。普通本地构建不传此参数时继续执行 Metal 检查。原有 `-SkipRuntimeChecks` 仍跳过全部运行和安装检查，两者同时指定时以后者为准。

打包器在签名前恢复 bundle 内 probe 的执行位，校验器要求 probe 可执行；下载的原生来源文件及其清单保持原样。签后 bundle 清单描述签后文件，来源清单 SHA 仍指向原始下载产物。

## 平台边界

窗口背景在 macOS 通过 Avalonia 的 `AcrylicBlur`／`Blur` 请求 NSVisualEffectView 系统材料。确认实际透明级别后使用浅色或深色色调，移除遮住效果的主页面不透明层；无法协商时使用不透明背景，保持文字可读。Windows 原有 Mica/Mica Alt 路径保留。

Linux 预留 UI 层的动态背景能力提供方：只有提供方和窗口后端均确认支持时才启用效果，能力撤销后恢复不透明背景。2026-10-25 读取的官方 [ext-background-effect-v1 XML](https://gitlab.freedesktop.org/api/v4/projects/wayland%2Fwayland-protocols/repository/files/staging%2Fext-background-effect%2Fext-background-effect-v1.xml/raw?ref=main) 支持动态 `blur` 能力，仍属 staging/testing；不能据此宣称所有合成器可用。本轮不实现 Linux 窗口后端或协议绑定。

`HeyMelody.Platform` 仅依赖 BCL，提供设备、授权、事件、RFCOMM、音频与进程路径边界。Windows 实现在 Windows 适配层；macOS 使用 IOBluetooth 经典 RFCOMM、CoreBluetooth 广播及状态、CoreAudio/AudioQueue。Host 继续拥有单接收通道、能力门控、连接代次、校准租约和禁止重放 SET 的规则。

Unix 使用稳定的用户标识、跨 shell 会话单例及长度不超过 100 个 UTF-8 字节的绝对管道路径；父目录检查当前用户、非符号链接及 `0700` 权限。Linux 复用该基础设施，蓝牙适配器仍待实现。Windows 的命名管道名称及登录会话隔离保持兼容。

## 初始移植阶段的本地验收边界

本机为 M1 MacBook Air。构建日志、离线测试、原生窗口和包收据保存在忽略的 `artifacts/` 与 `release/` 中；这些本地证据不随普通 Git clone 提供。

前阶段已完成真实 M1 Metal 引擎、模型加载、摘要错误拒绝、640×580 像素读回及 320×240 缩放检查；平台集成测试覆盖长路径、权限、符号链接、跨会话单例、不同 TMPDIR 的管道及嵌套 bundle 定位。各记录的源码和产物范围以对应收据为准。

最终字体、设备入口和显式 Metal／软件渲染选择修正前，四类界面的原生窗口检查通过 34 项，使用模拟服务覆盖全局退出、否决恢复、当前窗口关闭及试听输出选择。窗口实际协商为 `AcrylicBlur`，并检查到 NSVisualEffectView；浅深色主题、效果撤销后的不透明回退及变更区域的文字可读性已检查。快捷键检查覆盖菜单手势与原生菜单回调，未注入物理键盘事件。

收尾阶段停止继续测试，优先完成最新代码的 macOS 构建与 DMG 打包。最终修正后未复跑全部离线和原生窗口检查，也不再执行安装后应用入口及 Metal 运行探针；打包检查保留静态结构、签名与镜像完整性验证。此前结果不作为最终包的完整运行验收。

不以模拟服务证明蓝牙真实收发、授权提示归属、耳机输出路由、测听效果或睡眠后重连。本轮按范围只执行 macOS 构建，未执行 Windows 交叉编译或原生运行。截图保留本地，仅查看本轮变更区域，不执行完整截图审计。
