# 诊断日志

诊断日志用于复现连接、协议、校准、资源和渲染问题。Release 安装包也支持 Debug 级别，默认关闭，按托盘后台的一次运行周期启用。

## 收集一次故障

1. 打开主界面的“应用设置 → 故障诊断 → 诊断日志”。默认选中全部五个组件；排查蓝牙、查询或校准问题时保留 `HeyMelody.Host`。
2. 打开“在下次启动程序时记录日志”并保存。仅勾选组件不会开启记录；取消设置不提交草稿。
3. 正常结束校准和设置操作，从托盘菜单退出后台，再重新启动 HeyMelody。仅关闭或重新打开主窗口不会开始新一轮。
4. 复现问题。需要时打开设备管理器、贴合度测试或黄金听感，它们加入同一轮日志。
5. 从设置点击“打开日志目录”，找到本轮文件夹。正常退出相关窗口和托盘后台，待日志收尾后再打包该文件夹。

后台开始本轮时即消费待记录请求，因此开关会恢复关闭；这不表示本轮已经停止。当前状态单独显示。本轮后台退出、崩溃或被结束后停止记录，下一次启动默认不记录。记录期间可以明确安排下一轮，但不会自动延续。

“打开日志目录”始终可用。目录不存在时，点击创建并打开；该按钮独立于保存／取消，不开启记录或启动后台。

日志包含完整协议收发、设备标识、本地路径、异常和其他运行信息，不额外脱敏。分享前自行检查，避免将含个人信息的完整日志公开贴到 Issues。日志不自动清理，用完后可手动删除已结束的周期目录。

## 组件与文件

| 设置中的名称 | 记录范围 |
| --- | --- |
| `HeyMelody` | 主界面、操作、框架、资源和模型渲染；原生探测输出带子进程来源 |
| `HeyMelody.Host` | 托盘后台、目录和设备 worker、IPC、Windows 蓝牙、协议和后台业务 |
| `HeyMelody.DeviceManager` | 独立设备管理窗口 |
| `HeyMelody.FitTest` | 贴合度窗口及客户端流程 |
| `HeyMelody.HearingSetup` | 黄金听感窗口及客户端流程 |

共享库继承所在进程的组件归属，不单独列为组件。未启动的所选组件没有进程日志。源码研究 CLI、检查程序和安装器辅助工具不在此设置列表中。

日志根目录为 `%LOCALAPPDATA%\HeyMelody\logs`，每次收集建立“UTC 时间－会话 ID”目录。`session.json` 描述本轮 owner 和选择；每个托管进程独立输出 UTF-8 JSONL，32 MiB 分卷，避免跨进程竞争同一文件。单条事件不因分卷被截断。

每条 JSONL 包含 `schemaVersion`、`timeUtc`、`sequence`、`level`、`component`、`role`、`pid`、`processInstanceId`、`sessionId`、`ownerEpoch`、`source`、`eventName`、`message`、`correlationId`、`threadId`、`data` 和 `exception`。`data` 为事件专属字段；`exception` 保留原异常、内部异常与堆栈。协议字节以完整十六进制保存。重组 RX 时按来源及 connection/session/decoder ID 分组，再按记录顺序拼接；同一数据会在不同层出现，不能把各层或不同连接混拼。

TX 分别记录提交意图和实际写入：`request.tx` 是待发送整包，`rfcomm.tx` 是底层 send 返回的实际写入块。部分发送后失败不能等同于整包送达，更不能当作设备确认。

`Debug` 是运行期等级，不依赖 DEBUG 编译符号。普通界面最近 80 条操作记录仍保留原来的显示和遮蔽规则，诊断文件另行保存原始事件。

## 排障线索与边界

- `0x0114` 等查询：检查 TX、解码前 RX、命令与序号匹配、兼容布局、帧或字段解析异常、超时及连接结束。帧失败与字段失败分开记录；接收循环保存 pending 上下文，只有确证匹配才关联操作。
- Vulkan／OpenGL：检查实际 DXGI 候选及选择、驱动查询、用户设置、库与 probe 哈希、探测缓存、完整 stdout／stderr、退出码、超时、原生设备和创建返回码，再看界面提示原因。Avalonia 适配器与 Filament 实际设备分别记录；出现 OpenGL 建议不等于已证明该显卡不支持 Vulkan。
- 校准、设置和试听：检查开始、阶段、提交、ACK、回读、取消和清理。日志记录不增加查询、SET、重试或设备接收通道。

后台在 Avalonia 初始化前发布本轮日志决议；组件在渲染前加入，并以 PID、启动时间及 Windows 会话校验 owner。旧组件只绑定原 owner，后台退出后不跟随新进程。待记录设置按用户和 Windows 会话隔离。

写入使用有界异步队列；正常最多每 250 ms 刷新，正常关闭最多等待 2 秒收尾。队列满会记录丢失计数并显示日志不完整；文件创建或写入失败会停止对应 writer 并报告原因，不改变耳机操作结果。独立告警不被设备业务提示覆盖；后台故障随订阅快照保留，本周期后打开的界面也能看到。强制终止只能保留已写入的部分。

## 开发与验证

公共入口为 `HeyMelody.Diagnostics.DiagnosticLog`。常用接口包括 `Debug`、`Info`、`Warning`、`Error`、`Write`、`Bytes` 和 `BeginOperation`。关闭时先判级，避免构造昂贵的诊断数据；`Bytes` 在调用时冻结字节，不能把复用的接收缓冲交给后台异步读取。

GUI、Host 和 worker 使用可空诊断关联 ID 贯通请求，业务响应仍按原 RPC ID 匹配。独立通知不伪造关联。worker stdout 保持 JSON RPC，stderr 用于诊断旁路和故障报告。

原生 Filament 保持 ABI 2，新增必需的日志 setter 导出；托管加载、probe 和静态校验同时检查。DLL、probe、manifest 必须成组更新，不能混用旧桥。

定向检查：

```powershell
dotnet run --project tests/HeyMelody.Diagnostics.Tests -c Release
dotnet run --project tests/HeyMelody.App.Tests -c Release -- --diagnostic-settings-tests
```

测试使用临时配置、模拟协议和隔离子进程；打开目录动作注入替身，不操作 Explorer 或耳机。新增界面只检查设置和告警区域。原始日志可能包含长行，审查时先统计尺寸，再提取事件和字段，不整篇上传。
