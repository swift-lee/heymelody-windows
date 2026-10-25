# macOS / Linux 控制与音频可行性

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本文为公开整理稿；[整理前完整原文](../../research/migrations/organization-2026-10-09/originals/docs/research/cross-platform.md)保存在本地研究档案，不随普通Git clone提供。历史版本、来源及验证范围保留，完整研究对象与产物由 `research/` 保存。

2026-10-25 更新：本分支已开展并实现 macOS Apple Silicon 平台适配，当前范围与验收见 [macOS 说明](../macos.md)。下文的暂缓状态、源码结构与调研结论保留其历史语义，不能作为当前实现或真机验证结论。

**状态：后续暂缓。** 当前实现范围为 Windows 上现有 Enco X4 功能、Avalonia 界面及共享托盘后台。以下保留已完成的调研记录，不继续推进新平台、其他型号或高码率音频实现，也不构成本轮支持承诺。

调研日期：2026-10-06，源码结构描述对应 0.6.x 界面迁移时版本；0.7.0 新增共享 supervisor、WinRT watcher/BLE 和托盘，现结构见 [后台架构](../backend-architecture.md)。结论基于调研时仓库源码及下方的一手 API/源码资料；本次未在 macOS 或 Linux 上连接真实耳机。Avalonia UI 可以跨平台构建，Windows 以外的蓝牙控制后端仍需实现与真机验收。

## 当前可复用层与 Windows 边界

| 层 | 当前技术 | 跨平台处理 |
|---|---|---|
| `HeyMelody.Protocol` | `net10.0`，编码、分片、增量解析、序号匹配 | 直接复用，保留未知字段与原始枚举 |
| `HeyMelody.Devices` | `net10.0`，固定来源哈希、Product ID、静态能力、版本条件 | 直接复用；其他型号需独立证据和能力配置 |
| `HeyMelody.Control` / `Daily` | `net10.0`，`IControlConnector` / `IControlTransport`、串行设置、ACK 与回读 | 通过新的平台 transport 复用工作流 |
| `HeyMelody.Contracts` / `Client` | 纯 .NET DTO、服务接口、进程通信和界面状态 | UI 与后台共用契约；不引用 Avalonia 或原生蓝牙 API |
| `HeyMelody.App` | Avalonia + FluentAvalonia | 复用窗口、布局和预览；平台外观与权限提示需单独验收 |
| `HeyMelody.Host` | Windows 设备枚举、SDP、RFCOMM，独立工作进程 | 后续注入设备目录和连接器，保持 UI 不直接执行原生调用 |
| `HeyMelody.Windows` | `bthprops.cpl`、`ws2_32.dll`、`AF_BTH` | 保留 Windows 实现，不能仅修改 TFM 移植 |
| `HeyMelody.Audio` | WASAPI/COM、`cfgmgr32`、外部 Windows LHDC 驱动项目 | 保持独立的 Windows 音频实验模块 |

具体耦合集中于 `NativeHeadsetService.EnumerateAsync` 及其 `PeerConnector`，后者重新核对已配对设备并通过 SDP 获得服务通道。`ClientBridge` 的生产启动路径原本固定 Windows `.exe` 和 Host 的 `net10.0-windows` 输出；UI 改成 `net10.0` 后仍应明确解析 Windows Host，不能因为 App 的目标框架变化而假定 Host 也跨平台。

新分支只迁移界面、整理现有接口及提交调研，不新增 macOS/Linux 原生后台。普通非 Windows 启动说明后台尚未实现；模拟 `--ui-preview` 不启动后台，`--ui-live-preview` 仅在 Windows 使用 GET-only 服务。不能将缺少实现表述为平台技术上不能支持。

## Linux：优先采用 BlueZ Profile API

BlueZ 为外部 profile 提供稳定的系统 D-Bus 接口。建议原型按下列顺序进行：

1. 使用 ObjectManager 枚举 `org.bluez.Device1`，读取配对状态、地址、Alias、连接状态与服务 UUID；不通过扫描设备名称判断型号。
2. 在 `ProfileManager1.RegisterProfile` 注册 Melody client profile，并设置需要的 vendor profile options。已知 X4 服务 UUID 为 `0000079A-D102-11E1-9B23-00025B00A5A5`；避免固定 RFCOMM 通道，使用服务发现结果。
3. 对所选设备执行 `ConnectProfile(UUID)`，在 `Profile1.NewConnection` 接收 BlueZ 传来的 Unix 文件描述符，并包装为 `IControlTransport`。
4. 先查询 Product ID，再读取命令能力和状态。设置仅由明确用户操作触发；超时或断连后，不重发可能已经到达耳机的 SET。
5. 断开本应用的控制 profile 并关闭拥有的文件描述符，不调用断开全部 profile/ACL 的 `Device1.Disconnect` 来替代控制断开，以免同时打断系统音乐播放。

这些接口及行为来自 [BlueZ ProfileManager](https://github.com/bluez/bluez/blob/master/doc/org.bluez.ProfileManager.rst)、[Profile](https://github.com/bluez/bluez/blob/master/doc/org.bluez.Profile.rst) 和 [Device](https://github.com/bluez/bluez/blob/master/doc/org.bluez.Device.rst) 官方文档。`GetServiceRecords` 是实验性的兼容层接口，官方建议一般应用使用 Profile API，因此不将它作为产品前提。

原型必须验证 .NET D-Bus 库的 Unix FD 传递、连接/取消竞争、文件描述符所有权、BlueZ 重启、普通用户权限、多个适配器、睡眠恢复以及控制连接与 A2DP 并存。Flatpak/Snap 沙盒的系统总线授权属于后续打包事项；不能以宿主机原型成功替代沙盒验收。若目标 BlueZ 版本无法可靠使用 vendor profile，再比较受限的 native `libbluetooth` SDP/RFCOMM 适配；不把 shell 调用旧 `rfcomm` 工具和裸通道扫描作为默认方案。

## macOS：IOBluetooth 的经典蓝牙接口

已知控制通道是经典蓝牙 RFCOMM。建议采用小型 Objective-C 原生桥接库与 C ABI，由 C# 包装为现有 transport；原生代码处理 IOBluetooth 对象生命周期、运行循环和异步回调。

候选流程为 `pairedDevices()` → 限定 UUID 的 `performSDPQuery(_:uuids:)` → `getServiceRecord(for:)` / `getRFCOMMChannelID` → `openRFCOMMChannelAsync` → delegate 接收/写入/关闭。Apple 官方说明 SDP 是异步操作，RFCOMM 打开返回成功表示打开流程已开始；打开完成前需要注册接收监听，不能把返回值当作已建立控制连接。[SDP 查询](https://developer.apple.com/documentation/iobluetooth/iobluetoothdevice/performsdpquery(_:uuids:))、[RFCOMM 通道发现](https://developer.apple.com/documentation/iobluetooth/iobluetoothsdpservicerecord/getrfcommchannelid(_:))、[RFCOMM 打开](https://developer.apple.com/documentation/iobluetooth/iobluetoothdevice/openrfcommchannelasync(_:withchannelid:delegate:))

本次读取的 Apple 官方 DocC 元数据未将 `IOBluetoothDevice`、限定 UUID 的 SDP 查询、上述异步 RFCOMM 打开标为弃用。这支持开展原型，不证明目标 macOS 和 X4 的实际链路稳定性；仍需真实 Mac、正确运行循环及完整打开/数据/关闭回调验证。也不能把任意 BLE 库当作已知 RFCOMM 服务的替代实现。

应用包需要 `NSBluetoothAlwaysUsageDescription`；采用 App Sandbox 时还需 `com.apple.security.device.bluetooth` entitlement。[Bluetooth 用途说明](https://developer.apple.com/documentation/bundleresources/information-property-list/nsbluetoothalwaysusagedescription)、[沙盒 Bluetooth 授权](https://developer.apple.com/documentation/bundleresources/entitlements/com.apple.security.device.bluetooth) 真机验收应覆盖授权允许/拒绝、后台辅助进程的权限归属、断连、睡眠和应用退出后的资源清理。`.app` 布局、签名与 notarization 在后台原型通过后单独处理。

## 耳机控制与 LHDC 音频分别推进

耳机的 Hi-Res 开关、空间互斥与 codec 编号回读属于控制面；实际编码器、AVDTP 协商、媒体流发射码率、射频扩展和无损比例属于音频数据面。启用耳机开关不能证明主机在发送 LHDC，更不能证明 OPPO 手机的最高 2.3 Mbps 无损链路已移植到桌面系统。

Linux 有值得继续验证的候选：当前 PipeWire `master/devel` 文档为 **1.7.0**，已包含 LHDC v5 插件、`lhdcv5` 构建依赖和 `bluez5.a2dp.lhdc.quality`。插件源码公开的能力覆盖 16/24bit、44.1/48/96/192 kHz，其本地最大码率声明为 **1000k**；质量配置列出 auto/400k/500k/900k/1000k。这是开发分支代码证据，未证明任何指定发行版已启用该依赖或 X4 实机已成功协商，亦不能据此承诺 2.3 Mbps 无损。[PipeWire 插件](https://github.com/PipeWire/pipewire/blob/master/spa/plugins/bluez5/a2dp-codec-lhdc.c)、[构建依赖](https://github.com/PipeWire/pipewire/blob/master/spa/meson.build)、[开发版配置](https://docs.pipewire.org/devel/page_man_pipewire-props_7.html)

macOS 本次未找到允许普通应用为系统蓝牙输出增加 LHDC 编码器的公开接口；这是本次调研的边界，不是“绝对不可实现”的证明。先实现 RFCOMM 耳机控制，音频观察、外部发射器或独立音频链路作为不同课题。当前 Windows 的 `HeyMelody.Audio` 依赖已有驱动工程，也不会因 Avalonia 迁移自动成为跨平台模块。

Find X10 无线耳机组件中的 codec/射频声明需要结合系统蓝牙栈、SoC/控制器能力、编码器及真机抓包分析。手机组件中出现某一最大码率或开关，不足以证明通用第三方手机、Windows、macOS 或 Linux 可以协商相同链路；具体 APK 证据见同目录的组件调研。

## 建议里程碑与验收

| 顺序 | 交付 | 验收证据 |
|---|---|---|
| 1 | Windows Avalonia 客户端 | 原有离线协议/状态测试、进程故障不重发、设置只由用户触发、UI 预览、三 RID 静态包检查 |
| 2 | Linux/macOS 只读原型 | 普通用户枚举、服务发现、Product ID、能力与电量，记录操作系统/蓝牙栈版本和拒绝权限行为 |
| 3 | 平台控制适配 | 明确用户操作、匹配 ACK、新会话回读、互斥确认、双连、断连/睡眠恢复、A2DP 并存 |
| 4 | 平台原生发布 | `.app`/Linux 包中的依赖、签名/沙盒权限、实际运行和原生架构验证 |
| 5 | 音频链路专项 | 独立记录编码器、协商配置、媒体流/码率证据；不以采样率或 Hi-Res 状态替代无损/2.3 Mbps 测量 |

完成编译、模拟 UI 或架构交叉发布，只能证明对应的构建/界面属性；型号支持和平台控制能力应由真实设备证据升级。
