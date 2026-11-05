# macOS 修复的平台边界复查（2026-11-06）

## 范围与依据

本轮复查 `fceb079`（SDP／RFCOMM）、`af1d0cd`（窗口材质与 Dock）、`aa69d15`（LaunchServices 启动 Host），追查它们涉及的共享与 Windows 路径，不扩大到早期移植或其它优化方向。

原有 [RFCOMM 真机记录](macos-rfcomm-connectivity-2026-11-05.md)与[窗口材质及 Dock 记录](macos-window-material-and-dock-2026-11-05.md)保持原日期和验证范围。本次 Windows 上的静态复查及离线测试不追加为 macOS 真机证据。

## 发现与修复

- **启动失败回滚：** 新增的原生版本读取原先位于启动回滚区之外。缺库、架构不符或版本符号缺失时，`_started` 会残留，后续启动可能误报成功，清理再次抛错。版本读取、`GCHandle` 分配与启动现统一受回滚保护，失败只释放已分配句柄并复位状态；公开入口仍限制为 macOS ARM64。
- **共享材质策略：** `MacBackdropTint.Dense` 原先也用于 Linux provider 协商成功后的模糊背景。纯颜色策略现只在 macOS 使用 Dense，Linux 保持原 Standard 色值，未协商效果时保持不透明回退。当前没有默认 Linux provider，本项是扩展契约回归，不是默认 Linux 界面已出现故障。
- **启动逻辑隔离：** LaunchServices 构造与执行移至 Diagnostics 程序集内部的 `Platforms/MacOS/MacBackendLauncher`，不增加 GUI 或 macOS 蓝牙程序集依赖。保留 `open -g -j`、环境白名单、五秒等待与原有回退；非 macOS 在路径、环境和进程访问前返回原直接启动路径。
- **测试退出边界：** `ExitLater` 内的 macOS 判断覆盖成功和 watchdog 错误两处调用。Windows 不再被该新增 helper 强退，退出挂起仍由外层超时暴露。

未发现这三个提交直接改变 Windows 产品路径的确证；Windows 构造早退、Mica、Host 软件渲染和共享控制协议均未因本轮修复改变。没有新增公开 API。

## 已完成的定向验证

本机为 Windows，使用 .NET 10 SDK；以下结果分别记录，不能视为完整发布验收。

| 检查 | 结果 | 范围 |
| --- | --- | --- |
| `HeyMelody.MacOS.Tests` | 58 项通过，较基线新增 20 项 | 原生加载／启动失败、同实例重试、失败后释放及公开平台限制；macOS ARM64 原生 ABI／worker 检查明确跳过 |
| `HeyMelody.Diagnostics.Tests` | 118 项通过，较基线新增 16 项 | 包含启动计划的非 Mac 早退、bundle／loose publish、环境白名单和含空格参数；不启动真实 Host |
| App `--backdrop-policy-tests` | 72 项通过 | 无窗口的 macOS／Linux、深浅主题、效果协商与不透明回退矩阵 |
| App `--rendering-policy-tests` | 21 项通过 | 既有 Windows 渲染适配器及回退策略；不打开窗口或访问耳机 |

完整日志与产物保存在忽略的 `artifacts/review-macos-boundaries/`。构建未禁用 NuGet audit，仍记录到既有 `NU1900` 漏洞数据查询警告。

## Windows 完整回归与原生窗口

完整 Windows profile 已执行 19 个 .NET runner 与 3 个 Python 检查。18 个 .NET runner 和全部 Python 检查通过；Resources 首次为 40/41，故该次统一收据的 `success=false`，不能记为整套通过。App 默认检查为 261 项，包含新增的 72 项材质策略检查。

Resources 第一次独立复验仍为 40/41，但失败用例发生变化；现有 FirstChance 诊断记录到资源目录发布 `Directory.Move(temporary, package)` 附近的 `IOException`／`0x80070005`。第二次独立复验为 41/41。该模块与测试无本轮改动，fixture 使用独立 GUID 目录及 fake HTTP；本轮不修改此范围外路径，不将复验通过等同于访问拒绝已修复，也不推断杀毒软件等具体原因。后续记录为[BUG-14](../optimization-backlog.md#正确性bug)。

三个 Windows x64 原生退出 smoke 均使用软件渲染和模拟服务，主界面 11 项、设备管理器 8 项、校准界面 11 项通过，均自然 `exit 0`，未触发外层超时或强制终止。主界面检查包含新增的 Windows 背景、fallback、透明度与主题哨兵断言。收据均记录 `nativeBluetoothCalls=0`、`hostProcessesLaunched=0`。

完整 profile 收据位于 `artifacts/review-macos-boundaries/windows-final/offline-validation.json`，资源复验日志为同目录树下的 `resource-triage-repeat.log`、`resource-triage-final.log`。原生退出汇总位于 `native-exit/logs-6caa65f3e7a34efa9e877043df7c8a4d/native-validation.json`；原始失败与命令修正前的记录均保留，不随 clone 提供。

## 未验证与后续验收

- 本轮未执行 macOS 原生窗口、LaunchServices／Dock 视觉或 RFCOMM 耳机通信，不操作真实耳机。
- Windows 本机不能直接构建 DMG；后续使用同一提交的现有 macOS GitHub Actions 执行离线、原生 ARM64 ABI 与打包检查。CI 结果不替代真实窗口或耳机验收，构建、下载与摘要核对须按实际结果分别报告。
