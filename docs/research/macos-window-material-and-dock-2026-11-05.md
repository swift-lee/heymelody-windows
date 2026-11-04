# macOS 窗口材质分层与 Dock 激活策略（2026-11-05）

## 范围与环境

- 目标：修正 macOS 主窗口整窗透明、独立组件窗口过透，以及托盘运行时的多余 Dock 图标。
- 验证机：macOS 27.0.1（26A434），arm64；Avalonia 12.1.4、FluentAvalonia 3.1.0、.NET 10。
- 本记录只涉及窗口背景、原生材质与激活策略；蓝牙/协议路径未改动。
- 相关实现：`src/HeyMelody.App/MainView.axaml.cs`、`src/HeyMelody.App/MainWindow.axaml.cs`、`src/HeyMelody.Desktop/DesktopWindowBackdrop.cs`、`src/HeyMelody.Desktop/Platforms/MacOS/MacApplicationPolicy.cs`、`src/HeyMelody.Diagnostics/DiagnosticBackendLauncher.cs`、`src/HeyMelody.Host/Program.cs`。

## 发现

### 1. 主窗口整窗透明来自应用自己隐藏了页面层

`MainView.axaml` 的 `Grid#MainLayout` 使用 `RowDefinitions="*,Auto"`；`Border#PageSurface` 位于第 0 行并绘制 `ApplicationPageBackgroundThemeBrush`，`Border#DeviceBar` 位于第 1 行且本来就是透明背景。`SetNativeWindowBackdrop` 在 macOS 协商到原生模糊后执行 `PageSurface.IsVisible = !applied`，把上半部分唯一的实色层隐藏了，才使整窗露出底层材质。

这与 Windows 版设计相反：Windows 主窗口只在底部设备栏露出 Mica，上半部分始终由 `PageSurface` 覆盖。

### 2. Avalonia.Native 只提供一个整窗效果视图

Avalonia.Native 的 `AutoFitContentView.mm`（12.1.4 对应上游 `native/Avalonia.Native/src/OSX/AutoFitContentView.mm`）为每个窗口创建一个 `NSVisualEffectView`：

- `blendingMode = NSVisualEffectBlendingModeBehindWindow`
- 材质固定为 `NSVisualEffectMaterialHUDWindow`（macOS 10.14+）
- 与标题栏的 within-window 材质分开，但内容区只有一个整窗效果

原生层没有按区域或按窗口选择材质的公开 API。因此“只在设备栏透材质”只能通过不透明内容遮挡实现，不能通过局部 `NSVisualEffectView` 实现。

### 3. macOS 材质与 Mica 的用途不同

`NSVisualEffectView` 的常见材质面向标题栏、侧栏、菜单、弹出层和 HUD 等 vibrancy 场景；Mica/MicaAlt 是面向任意内容的窗口背景。把 HUD/侧栏材质直接铺在设置类窗口的整页文字后面，不符合 macOS 的用法，也会让文字背景随桌面内容变化。

结论：不移植 Mica/MicaAlt；主窗口只在底部设备栏保留材质，组件窗口使用高不透明度主题色，让材质退为轻微底感。

### 4. Host 的 Dock 图标来自 Avalonia 默认激活策略

打包器已为 Host 写入 `LSUIElement=true`，但 `src/HeyMelody.Host/Program.cs` 的 `RunTray` 没有 `MacOSPlatformOptions`。Avalonia 12.1.4 默认 `ShowInDock=true`，在运行时把进程设为 `NSApplicationActivationPolicyRegular`，覆盖了 Info.plist 的 agent 声明。

实机查询（修复前）：

```text
NSRunningApplication(bundle id ...host).activationPolicy = 0   # Regular
lsappinfo: HeyMelody.Host type="Foreground"
```

Dock 同时出现 3 个 `HeyMelody` 图标；其中只有 Host 进程真实存活。`lsappinfo` 显示主应用记录已 `(exited-with-subordinates)`，其 coalition 仍包含 Host/worker；其余主 bundle 图标是重复启动/历史构建留下的 Dock 幽灵项。点击主 bundle 图标会重新启动主程序并走单实例激活，Host 图标则没有 reopen 处理器。

### 5. 主应用拉起 Host 时仍会多出一个弹跳中的 Dock 图标

修复激活策略并清理 Dock 历史状态后，冷启动安装包仍然稳定出现两个 `HeyMelody` 图标：一个对应主窗口，另一个持续弹跳（未完成注册的 launch 状态）。此时主应用 `policy=0`，Host 进程 `policy=1`，系统中只有一个主进程；弹跳图标不是已有进程，而是 Host 通过 `Process.Start` 直接作为子进程启动时，LaunchServices 在主应用的前台/Dock 分组中为它建立的一条临时 Dock 记录。等 Avalonia 在 Host 进程内应用 `ShowInDock=false` 时，图标已经出现并开始弹跳；强制退出该图标会连带结束 Host，主界面随后再次拉起 Host，这一次进程以已有启动上下文恢复，才不再出现多余图标。

Host 的 Info.plist 已写 `LSUIElement=true`，但直接 `fork/exec` 子进程不会让 LaunchServices 读取该 bundle 声明；五个 helper bundle 的 `CFBundleName`/`CFBundleDisplayName` 又都是 `HeyMelody`，因此临时图标与主应用同名，容易被误认为重复启动。

结论：Host 不应由主应用直接以子进程方式启动，而应通过 LaunchServices 以独立应用请求启动，让 `LSUIElement` 在进程创建前生效。

## 实现

1. **主窗口**：`SetNativeWindowBackdrop` 只在 Linux 保留“协商到整窗效果时隐藏”的行为；macOS/Windows 始终显示 `PageSurface`。底栏继续透明，因此原生材质只从底栏露出。
2. **组件窗口**：`DesktopWindowBackdrop` 增加 `MacBackdropTint { Standard, Dense }`。主窗口用 Standard（深色 alpha 64、浅色 40）；设备管理、贴合度和黄金听感用 Dense（深色 alpha 240、浅色 244，RGB 对齐页面底色）。初版使用 224/232，截图复核后提高，避免深色壁纸下出现明显色块。
3. **激活策略**：新增 `MacApplicationPolicy`（libobjc `NSApplication.setActivationPolicy:`；Regular=0、Accessory=1）。Host 在 `RunTray` 中显式 `ShowInDock=false`；主窗口在异步清理期间保持 Regular（窗口仍可见、Dock 仍有图标），只在最终 `Close()` 提交前切到 Accessory，使图标随窗口消失；若关闭被意外取消且窗口仍可见，则恢复 Regular。设备管理、贴合度和黄金听感原有的 `ShowInDock=false` 不变。
4. **Host 启动方式**：`DiagnosticBackendLauncher.Start` 在 macOS 上先尝试 `TryStartMacBundle`。当解析到的启动路径位于 `*.app/Contents/MacOS/` 时，改用 LaunchServices 请求启动：`/usr/bin/open -g -j [--env NAME=VALUE ...] <bundle>`。`-g` 不把目标带到前台，`-j` 以隐藏方式启动，`--env` 传递 `HEYMELODY_*`/`DOTNET_*` 环境变量（选项必须放在 bundle 路径之前）。`open` 在 5 秒内以 0 退出视为请求成功并直接返回；非 bundle 运行（开发树、`dotnet` 启动）或 `open` 失败时回退到原有的 `Process.Start`。这样 `LSUIElement` 在进程创建前生效，Host 不会注册 Dock 图标；主应用也不再是 Host 的父进程，Host 在启动器退出后由 launchd 接管。

## 验证

- `tests/HeyMelody.App.Tests --native-exit-smoke`：13 项通过，包含主页面层可见、Standard 色调、打开时 Regular、否决后仍 Regular、批准关闭后 Accessory。
- `tests/HeyMelody.DeviceManager.Tests --native-exit-smoke`：11 项通过，包含 Dense alpha 下限（≥236）与 Accessory 策略。
- `tests/HeyMelody.Calibration.UI.Tests --native-exit-smoke`：16 项通过，包含 Dense 色调、Accessory 策略及原生菜单关闭。
- `HEYMELODY_CAPTURE_BACKDROP=1` 生成主窗口与黄金听感的浅/深色截图及合成屏幕区域：主窗口上半部分完全遮住背后的红蓝测试图案，底栏保留图案的模糊；黄金听感在 Dense 色调下只剩轻微底色，文字与卡片对比度正常。截图保存在本地 `artifacts/macos/backdrop/`，不随源码提交。
- 单独运行重建后的 Host：`NSRunningApplication.activationPolicy = 1`（Accessory），`lsappinfo type="UIElement"`；菜单栏出现状态项，展开后包含“打开主界面”和“退出”；重启 Dock 清理历史幽灵项后，Dock 中没有任何 HeyMelody 图标。
- 裸可执行文件 `open -g -j --env HEYMELODY_TEST_LAUNCH=1 <Host.app>`：Host 启动后无 Dock 图标，说明 LaunchServices 在进程创建前读取了 `LSUIElement`，且 `--env` 放在 bundle 之前能被正确解析。
- staged 完整应用（`artifacts/macos/build-*/HeyMelody.app`）冷启动：Dock 中始终只有一个 `HeyMelody` 图标（主窗口）；Host 由主应用通过 LaunchServices 拉起，`ppid=1`（launchd）、`activationPolicy=1`、菜单栏状态项存在、worker 正常运行，没有弹跳或临时 helper 图标。随后验证 ⌘W 只关闭主窗口（Dock 图标消失、Host 仍在菜单栏）、托盘“打开主界面”重新出现一个图标、托盘退出同时结束主界面与 Host，结束后 Dock 无残留图标。
- 启动顺序与固定图标语义（重建后的裸可执行文件）：
  - Host 先启动：Host `policy=1`、无 Dock 图标；再启动主界面，主应用 `policy=0`，Dock 出现一个 `HeyMelody` 图标；关闭主窗口后主进程退出、图标消失，Host 仍在菜单栏运行。
  - 主界面先启动（Host 未运行）：主应用自动拉起 Host（Host `policy=1`，菜单栏状态项可用），主应用 `policy=0`、Dock 有一个图标；关闭主窗口后图标消失，Host 继续以 Accessory 运行。
  - Host 先启动后从托盘“打开主界面”：主应用以 `policy=0` 出现 Dock 图标；关闭主窗口后图标消失，Host 仍在菜单栏。
  - 固定在 Dock 的主应用图标与从 Finder 启动同一 bundle 等价，因此两种情况都能同时拉起主界面和 Host；Host 自身不再产生可固定的 Dock 图标。开发树中用 `dotnet HeyMelody.dll` 启动时 Dock 名称显示为 `dotnet`，打包 bundle 使用 `Contents/MacOS/HeyMelody` 时显示为应用名，这是启动方式的差异，不影响激活策略。
- 重复运行中多次出现字体窗口回收检查 23/24（`Closed font fixture windows must be collectible`），重跑即通过；本次重建中 Calibration 连续两次 23/24、第三次 24/24 通过。失败发生在 macOS 专属检查之前，与激活策略/材质/启动方式改动无直接关联，但说明该 GC 断言存在偶发波动。
- 重新构建 0.9.2 DMG（`release/macos/HeyMelody-0.9.2-macos-arm64.dmg`，SHA-256 `d7d024184b4e64c2bf4c18dc2ff58b452238c70b4c94f8400a0e7c656fdd2506`）：离线测试套件全部通过，打包器的 5 个入口 runtime check 全部成功；DMG 由上述 staged bundle 直接生成，本轮 Dock 验收在该 staged bundle 上完成。
- 原生测试宿主偶尔在 `desktop.Shutdown` 后仍存活，并在 Dock 留下“HeyMelody … Tests”图标；测试 harness 现在在写入回执后最多等 2 秒并以 `Environment.Exit` 兜底退出，最终复跑三个原生入口均未留下测试进程或 Dock 图标。

## 未验证与边界

- Avalonia.Native 固定 `HUDWindow` 材质与整窗 `behindWindow` 混合，本轮不替换私有视图材质，也不改变原生标题栏材质；组件窗口标题栏仍由系统绘制。
- 历史构建或强制退出留下的 Dock 幽灵图标不属于应用可清理的状态；需要重启 Dock 或重新登录。应用只能保证自己运行期间的激活策略正确。
- Dense 色调的最终观感与壁纸、显示器配置和辅助功能对比度设置有关；本轮只复核了本机浅深主题和红蓝测试图案。
- 主应用已在 staged 完整 bundle 中重新走一遍 Dock 开关窗口流程；最终 DMG 由同一构建产物打包，Dock 行为不因打包容器改变。开发树用 `dotnet` 启动时 `TryStartMacBundle` 找不到 `.app` 祖先目录，会回退到 `Process.Start`，可能仍出现 helper 临时图标；这是开发运行方式的边界，打包应用不经过该分支。
- 未复跑 Windows 构建或 Windows 原生窗口检查；Windows 路径由平台守卫隔离，XAML 未改。
