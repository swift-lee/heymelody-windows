# macOS RFCOMM 控制连接修复（2026-11-05）

## 范围与环境

- 目标：修复 Apple Silicon macOS 客户端无法连接已配对 OPPO／OnePlus／realme 耳机进行控制的问题。
- 验证机：macOS 27.0.1（26A434），arm64；OPPO Enco X4 已在系统蓝牙中连接（HFP／AVRCP／A2DP／ACL）。
- 构建：0.9.2，macOS 原生桥 ABI 2，ad-hoc 签名，未公证。
- 文件：`src/HeyMelody.MacOS/native/bluetooth.mm`、`src/HeyMelody.MacOS/MacBluetoothPlatform.cs`、`src/HeyMelody.MacOS/HeyMelody.MacOS.csproj`、`tests/HeyMelody.MacOS.Tests/Program.cs`。

## 根因

### 1. 带 UUID 过滤的 SDP 查询在 macOS 12+ 为空操作

原实现调用 `-[IOBluetoothDevice performSDPQuery:target uuids:uuidArray]`，随后读取 `device.services`。macOS 12（Monterey）起，该带过滤的调用不再执行查询：Qt 的修复提交明确指出 `performSDPQuery:self uuids:filter is simply a noop function on Monterey, we have to filter services manually, after SDP query finished`，并记录无过滤 `performSDPQuery:` 也不再主动建立 baseband 连接。

- Qt qtconnectivity `f00ea686`：<https://invent.kde.org/qt/qt/qtconnectivity/-/commit/f00ea686c841e14a89bd776ac3b86766a4980bb3>
- Apple Developer Forums 722228：<https://developer.apple.com/forums/thread/722228>

因此 `device.services` 保持空数组，端点数为 0，Host 收到 `BluetoothDiscoveryException(ServiceNotFound)` 或平台状态 -2，界面表现为“耳机未公布已配置的控制服务”。这与 Windows 代码中“缓存缺失时刷新 SDP”的路径不同。

### 2. 异步回调可能永不返回

RFCOMM 打开与 SDP 查询的完成回调在设备离线、被其他主机占用或特定 macOS 版本组合下可能不触发：

- Qt `05b8398a`（RFCOMM 打开无回调）：<https://invent.kde.org/qt/qt/qtconnectivity/-/commit/05b8398aa6f1a6e109d92da6a2d6a8b2d9b0e47f>
- Chromium `BluetoothSocketMac` 为 SDP 查询和通道打开增加 10 秒超时：<https://www.mobibrw.com/gitea-mirrors/chromium/src/src/branch/main/device/bluetooth/bluetooth_socket_mac.h>
- macOS 26 实机记录 `openRFCOMMChannelAsync` 可能不回调，并建议用超时与回调竞速：<https://zenn.dev/udon_dev/articles/macos-rfcomm-sony-headphones>

原实现只依赖 Host 的 65／75 秒 worker 预算，用户侧长时间无反馈。

### 3. 首次权限询问会同步阻塞

macOS 14+ 的 IOBluetooth 调用受 TCC 管理。权限未决时 `+[IOBluetoothDevice pairedDevices]`／`registerForConnectNotifications:` 会初始化 `IOBluetoothCoreBluetoothCoordinator` 并同步等待系统授权对话框；Chromium 在 macOS 14+ 测试中也观察到该阻塞。首次运行必须由用户在系统对话框中允许蓝牙；若没有 `NSBluetoothAlwaysUsageDescription`，IOBluetooth 可能静默返回空列表或以 TCC 终止进程。打包器已在主应用和四个 helper 的 Info.plist 中写入该用途说明。

- Chromium 关于 macOS 14+ 同步等待 TCC 的记录：<https://chromium.googlesource.com/chromium/src/+/3fefddfc43edcf3a5d62ed60f15cb592f299ab18>

## 实现

1. **缓存优先**：`HMResolveEndpoints` 对每个请求 UUID 先读 `device.services`，缓存未命中时调用同步的 `-[IOBluetoothDevice getServiceRecordForUUID:]`；用 `matchesUUIDArray:` 与 `getRFCOMMChannelID:` 校验服务，只接受 1..30 通道，按 `(uuid, channel)` 去重。
2. **完整查询回退**：缓存为 0 或多于 1 个端点时，调用无 UUID 过滤的 `performSDPQuery:`，完成后重新解析并仍要求唯一端点；0 → -2，多个 → -3。
3. **5 秒边界**：SDP 查询与 RFCOMM 打开各使用主队列 `dispatch_source` 计时器。SDP 超时 → -9，打开超时 → -10；`kIOReturnExclusiveAccess`／`kIOReturnBusy` → -11。晚到回调、超时与 `close:` 通过 `sdpPending`／`openPending`／`closed` 串行化，不会重新打开已关闭通道。
4. **诊断**：原生回调 kind 11 上报 `cached`／`query`／`resolved`／`opened`／`failed` 阶段、端点数量、通道号、状态和耗时；托管层写入 `macOS.Bluetooth/sdp.*`，不包含蓝牙地址。
5. **ABI**：`hm_bt_bridge_version` 升为 2；离线测试断言 ABI、错误映射和未启动平台的构造／释放。

## 真机结果

Host 诊断日志中的关键事件（2026-11-04T18:25:40Z 至 18:25:42Z，UTC）：

```text
adapter.start      {"bridgeVersion":2}
adapter.status     {"access":"Allowed","radioAvailable":true}
devices.enumerated {"count":4,"connected":1}
control.open.requested {"serviceCount":2}
sdp.cached         {"endpoints":1,"status":0,"elapsedMs":0,"source":"cache"}
sdp.opened         {"endpoints":1,"channel":15,"status":0,"elapsedMs":249,"source":"cache"}
control.opened
host.connection connected {"name":"OPPO Enco X4","ControlReady":true}
```

上述 `channel`／`249 ms` 来自最终 DMG（SHA-256 `6006f7267571f662537325ea0a95e2d23374a539c6cf61ce448ff6ea18edb458`）挂载后的同一 bundle 复验；更早一轮 staged 构建为 550 ms、尚未记录通道号。随后 worker 完成 94 次 `Daily/request.tx`／`request.response` 匹配和 114 次 `Daily/transport.rx`，无 Warning／Error；日志中两次用户发起的 `anc-mode` apply 均为 `SubmissionAttempted=true`、`ResponseMatched=true`、`AckAccepted=true`、`ReadbackMatches=true`、`ReadbackVerified=true`、`Success=true`。这说明本轮不仅建立了 RFCOMM 通道，也完成了真实控制协议收发与设置回读。

打包回执仍标记 `bluetoothHardwareVerified=false`，因为该字段只表示打包脚本没有执行硬件测试；真机结论以 Host 诊断日志和上述 DMG 复验为准。DMG 的 `runtimeChecksExecuted=true`、`metalProbeExecuted=true`、`notarized=false`。

首次启动时系统弹出“Allow 'HeyMelody.app' to use Bluetooth?”，在用户点击 Allow 前 Host 主线程停在 `IOBluetoothCoreBluetoothCoordinator` 的信号量上；允许后自动继续枚举和连接。该对话框归属主应用 bundle，helper 的 Info.plist 权限说明由打包器统一提供。

## 未验证与边界

- 双连、手机同时持有控制通道时的竞争、断连恢复、睡眠／唤醒后重连未在本轮验证。
- 已配对但系统层未连接的耳机仍不会由应用主动连接；需先在系统设置连接。该行为与共享 Host 设计一致，登记在[优化待办](../optimization-backlog.md)。
- 带过滤的 SDP 查询在 macOS 27 仍未恢复；无过滤查询的回调若 5 秒内不返回会以 -9 失败，未观察到成功回退样本（本轮缓存直接命中）。
- ad-hoc 签名每次重新构建可能改变 TCC 身份并要求重新授权；未引入开发者证书或公证。
- 未执行 Windows 交叉编译或实机回归；Windows 逻辑未改动。

## 依据与后续

- 控制服务 UUID：`0000079A-D102-11E1-9B23-00025B00A5A5`（配置单源，见 [欢律单源规则](heymelody-only.md)）。
- 相关历史调研：[跨平台可行性](cross-platform.md)、[蓝牙事件](bluetooth-events.md)。
- 复现：开启 Host 诊断日志，启动已签名的 `HeyMelody.app`，在系统蓝牙中连接 X4；查看 `macOS.Bluetooth/sdp.*` 与 `host.connection/connected`。
