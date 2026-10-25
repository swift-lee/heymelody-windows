# 共享后台架构

本文说明当前客户端沿用的共享后台设计，保留 0.7.0 起的架构演进信息。用户操作见[使用说明](usage.md)，开发与验证入口见[开发指南](development.md)，协作规则见[AGENTS.md](../AGENTS.md)。

`HeyMelody.Host.exe` 默认运行 Avalonia 托盘应用，不创建主窗口。当前用户、当前 Windows 登录会话只保留一份后台和主界面。右键菜单使用 Avalonia 的 NativeMenu，只有“打开主界面”和“退出”。托盘进程只绘制这一个小菜单，使用 Avalonia 的 Win32 软件渲染与 RedirectionSurface，不初始化 GPU 与合成器；菜单外观和 Microsoft YaHei UI 字体不变。正常启动 `HeyMelody.exe` 会确保后台可用并显示界面；重复启动请求激活已有界面。直接启动 Host 只显示托盘。本版本不注册登录自启。

## 进程与通信

2026-10-25 macOS 适配补充：Host 的业务、worker 监督及 IPC 复用，枚举、SDP/RFCOMM、系统事件和音频由中立平台接口提供。Windows 保留现有管道名称及登录会话隔离；Unix 按 UID 使用跨 shell 会话的单例与短绝对套接字路径。macOS 路径位于 `/private/tmp/heymelody-<uid>`；Linux 优先使用可信的 `XDG_RUNTIME_DIR`，超出 100 个 UTF-8 字节预算则使用 `/tmp` 的用户私有目录。目录必须由当前用户拥有、权限为 `0700` 且非符号链接，双向管道验证对端用户。

macOS 主应用和四个 helper 共用同一个后台；辅助 bundle 不显示额外 Dock 图标。所有窗口的 ⌘Q 通过新增退出请求 RPC 发起下述既有投票流程，请求先应答再开始投票；⌘W 和关闭按钮保留本地组件关闭行为。Mac 原生蓝牙、授权与音频路由的实际耳机验证边界见 [macOS 说明](macos.md)，下文明确写 Windows 的原生 API 行为仍仅指 Windows。

- App 与 DeviceManager 使用 schema-4 双向命名管道（历史版本为 schema 2／3）。端点按用户和 Windows Session ID 区分，服务端限制当前用户并检查客户端进程所在会话。公共契约只携带内存中的 opaque Identity，不携带蓝牙地址。
- Host supervisor 管理白名单、设备目录、每台设备的控制状态和观察缓存。设备的产品 ID 与协议颜色按白名单键持久化在 `device-appearance.json`，只作为外观提示随设备目录发布，不授予控制（见 [外观资源持久化](artwork-persistence.md)）。独立的 `--catalog-worker` 执行同步原生枚举；每台耳机使用独立的 `--worker` 执行 SDP/RFCOMM。原生调用卡住时可以结束对应 worker，托盘和其他耳机保持响应。
- 主界面启动时在 Avalonia 初始化前于线程池建立管道（只连接、不订阅）；托盘单实例互斥体不存在时直接启动后台，不再先等 300 ms。`enumerate`／`enumerateCandidates` 在托盘首次初始化进行中时等待该次初始化，不另起 catalog-worker；其余时间每次查询仍新建刷新。主界面先用 subscribe 返回的有效名单进入页面，`enumerate` 在后台核对，结果经 catalog 事件更新。见[主界面启动](research/main-window-startup-0.9.9.md)。
- worker 地址通过私有标准输入发送，不放在进程命令行。worker 与 Host 的 stdio 通信只在内部使用。历史 schema-1 RPC 适配器仅位于 Client.Tests，生产 ClientBridge 只使用共享管道。
- subscribe 在后台锁内加入订阅并排入目录和缓存基线。后续事件与响应共用一个接收通道，以 ID 匹配请求。每个客户端拥有独立、有界的输出队列；普通状态更新可以合并，退出、激活和响应保留。慢客户端不会阻塞蓝牙接收。线格式省略值为 null 的成员，因此请求参数中的可空项（如关闭黄金听感时的 `uid`、没有颜色提示时的 `color`）缺失即按 null 处理；必需参数缺失或类型错误时返回通用失败，不转发运行时异常文字。
- Snapshot 包含 BackendInstanceId、DeviceGeneration 和单调 Revision。前端只采纳当前后台、当前连接代次的新快照；迟到的手动刷新或 Apply.After 不会覆盖较新手机通知。后台重启后的旧设置不会迁移或重放。schema 版本不匹配时停止自动重连并提示退出旧后台、重新打开完整客户端，不擅自结束仍在运行的共享后台。

## 蓝牙状态

Windows DeviceWatcher 提供已配对设备的连接和解除配对事件，Radio 事件提供蓝牙开关变化。后台自动控制白名单中由 Windows 连接且已核实支持的耳机。选择设备仅改变界面显示和设置目标，不关闭其他设备的控制会话。

每条 RFCOMM 连接只有一个持续接收通道，同时分发 ACK、GET 响应和耳机通知；同一接收批次中的其他包仍会被处理。每台设备的命令串行执行，通知接收不等待命令锁。WSAEventSelect 的原生就绪事件代替 100 毫秒 select 轮询。初始化和连接轮换会重新核验型号、能力与通知订阅。未支持或未证实的能力保持未知。

建立控制连接时，SDP 先查欢律配置的服务 UUID 缓存；缓存缺失、不唯一或缓存通道建连失败时，实际查询远端 SDP，仍要求唯一服务与有效 RFCOMM 通道。每次打开会话至多两次建连，各限五秒，用户取消时停止；同步 SDP 由 worker 总期限兜底。普通设备列表扫描不进行远端 SDP。失败提示保留服务发现类别、查询/通知订阅命令号或 Windows 数字错误码，不包含蓝牙地址或原始异常消息；依据与验证边界见 [Air4 Pro 控制核查](research/enco-air4-pro-control.md)。

普通界面状态不再每八秒读取。初始化、重新连接、手动刷新、设置前后确认仍执行 GET；只包含部分信息的通知可以合并为对应 scope 的补充读取。设置推送不会触发绑定控件提交 SET，未提交选项和 EQ 草稿继续按编辑版本保护。

BLE 使用两个 Active AdvertisementWatcher，各带一个字节模式过滤器（厂商数据段 0xFF、偏移 0、小端公司 ID），分别只接收含 OPPO 0x079A、OnePlus 0x072F manufacturer record 的广播包和 scan response；过滤只减少无关广播进入托盘，处理代码仍自行核对公司 ID，单包语义不变，尚待真机验证。两个 watcher 只在至少存在一副已配对（Present）且在白名单（Allowed）的耳机时运行，没有时停止；Present 指耳机仍在 Windows 已配对音频设备列表中，与是否连接无关，扫描参数（Active 模式、字节模式过滤器、默认间隔）不变，见[资源占用](#资源占用)。广播须完整通过字段长度、公司 ID、产品 ID 和可证实的地址关联检查。仅随机地址且无已证实关联的广播不归到某副同名耳机。广播不会授予 ControlReady、能力或设置权限。

SPP 与 BLE 各自保留观测时间，按耳机部件融合电量、充电盒盖及佩戴状态；同一时间优先采用已核验 SPP。其他设置读取不会刷新旧盒盖时间。BLE 字段在 30 秒后失去新鲜性，未观测到的状态保持 Unknown。开盖去重独立于显示过期：首次观测到盒盖状态时即为打开，标为 FirstObservation；重复开盖不重复触发，只有较新的明确合盖能重新允许下一次开盖。暂停、无广播和休眠不等同于合盖。

系统挂起停止 watcher 和控制会话；恢复后仅在仍有扫描需求时重建 watcher，并丢弃旧 watcher 代次或早于本次扫描的回调。Modern Standby 使用注册式 suspend/resume 通知。广播或监听 API 不可用时会发布诊断，现有可用 RFCOMM 仍可工作；确证蓝牙关闭时才停控制会话。

详细 APK 依据和广播编码见 [bluetooth-events.md](research/bluetooth-events.md)。当前按欢律配置、App 条件、已实现的协议和设备能力开放设置；固件条件只产生提醒，适配范围见[设备支持说明](research/oppo-device-support.md)。客户端没有改变 Windows 音频驱动或 LE Audio 管线；后台提供 caseOpen 事件，尚未实现独立开盖弹窗；后台开盖检测保持运行，与将来弹窗的兼容安排见[资源占用](#资源占用)。

## 暂停、写入与退出

现有连接/断开按钮恢复或暂停所选耳机的 RFCOMM 控制监听。暂停跨主界面退出和 Windows 重新连接保留，手动恢复或 Host 重启才解除。OS 和 BLE 观测继续。

每次 apply 携带明确设备 Identity、后台实例和连接代次，并重新核对 ControlReady 及能力。用户界面断开时，尚未开始的该客户端写入取消；已经开始的 setter 在其后台设备生命周期仍有效时继续 ACK 和回读；显式暂停、OS 断连或休眠会结束该生命周期。客户端 EOF、超时或后台重启后不自动重发 SET，缺失回执显示设置可能已生效。

关闭主界面先完成既有 EQ 草稿确认，再结束主界面进程；后台继续监听。托盘退出执行 requestExit → exitVote → stopping → stoppingAck：主界面、设备管理器及两个校准窗口共同参与，任一否决或投票超时发送同轮 exitCancelled，恢复界面操作。准备期间拒绝新业务，仅允许订阅、投票及原连接的校准取消／STOP；本地同意只投票，收到正式 stopping 后才关闭窗口。

退出准备通知也发送给尚未订阅的暖连接；其先暂停自动拉起，订阅完成后再参与投票。退出期间新接受的连接同样加入当前阶段。正式 stopping 对全部连接发送，等待写出及原管道确认或断开（有界 3 秒）；最终停止 accept 后再核对一次接入集合，避免通知仅入队就关闭管道。之后等待已开始的设置收尾（共享等待上限 125 秒），清理 worker、watcher 和托盘。进入退出回调（关闭观察器、管道与托盘并请求桌面关闭）后，Host 另起 15 秒看门狗；仍未结束时记录 Critical `forcedExit`、刷新日志并强制结束进程，不让挂起的 UI 线程或清理步骤留下残留后台。投票收集意外失败时，阶段回到空闲并尽力向各连接发送 exitCancelled，不会停在“正在确认退出”。

客户端三种退出生命周期消息使用独立可靠队列，普通状态积压不能挤掉关窗通知；早于窗口事件订阅的终态会回放。收到 stopping 后，同进程其它 Bridge 及后建连接也禁止自动拉起同一后台；投票和终态确认只使用原管道及后台实例。取消准备可恢复正常通信，未收到退出决定的意外断线仍按原策略恢复。窗口准备投票失败（设备管理器收尾或校准取消抛出异常）时改投否决票，Host 不必等满 60 秒；同意票未送达且非断管时撤销本地决定，后续重试从干净状态开始。终态关闭不再执行恢复监听等新 IPC；清理失败记日志并完成已批准的关窗，不留下半关闭的失活窗口。

主窗口启动的设备管理器和校准组件还通过子进程环境绑定原 Host 的 PID、启动时间及 Windows 会话。该绑定包含关闭日志的周期，不以 Debug 开关为条件；原 owner 已结束、被替换或无法确认时，尚在冷启动的组件直接正常退出。绑定组件的日志引导和 ClientBridge 均不自动创建新 Host；原 owner 退出事件也能终结尚未订阅的窗口。独立主动启动的组件不继承这种父级绑定。校准准备阶段可能已经完成安全 STOP，取消整个退出不会自动重启设备测试。

## 资源占用

托盘与所有 worker 共用同一份 runtimeconfig：`System.GC.Gen0MaxBudget=4194304`、`System.GC.ConserveMemory=5`，并关闭 TieredPGO。未配置时工作站 GC 的 gen0 预算由 L3 缓存大小推得（32 MB L3 约 16 MiB，大缓存 CPU 更高），每个进程各占一份；Linux 探针测得每次 gen0 约 3.97 MiB，默认为 15.93 MiB。用环境变量覆盖须写 `DOTNET_GCGen0MaxBudget`（十六进制值），拼写区分大小写。`--worker` 与 `--catalog-worker` 的入口不加载 Avalonia，只有托盘进程加载。

美术包的校验与下载经 64 KiB 池化缓冲流式处理：下载写入磁盘 scratch（`.download-*`），解压读取已校验的文件，不再把整包读进内存；美术安装后约 5 秒执行一次压缩 GC。worker 的目录载荷（gzip 加 base64）按目录只计算一次。IPC 的 JSON 使用源生成并保留反射回退；Linux 探针中 GUI 首次往返快 33%，RssAnon 在 GUI 场景少 1.7 MiB、在 host 场景少 5.2 MiB，已保留。

这些内存数字来自 Linux 合成探针，Windows 内存没有测量；Windows 待验证项、测量脚本 [Measure-HostMemory.ps1](../tools/Measure-HostMemory.ps1) 的用途和完整依据见[后台内存审计](research/backend-memory-audit.md)，其他方向的待办见[优化待办](optimization-backlog.md)。

### 效率模式（能耗）

没有任何界面连接时，托盘进程与全部 `--worker`、`--catalog-worker` 子进程进入 Windows 任务管理器所称的“效率模式”：进程级 EcoQoS（`SetProcessInformation` 的 ProcessPowerThrottling，EXECUTION_SPEED；Windows 11 22000 及以上另加 IGNORE_TIMER_RESOLUTION，被拒绝时回退为只设 EXECUTION_SPEED）加 IDLE 优先级类。`EnergyPolicy` 管理状态与计时，`EnergyController` 在线程池上对托盘和已挂接的子进程应用，不占用界面线程或后台锁，`WindowsProcessEnergy` 执行 Win32 调用。

- 计入“界面”的订阅角色为 main、manager、calibration，preview 同样接受，observer 不计；已连接但尚未订阅（角色未知）的客户端按界面计，使启动中的窗口不会遇到被限速的后台；连接 60 秒（`SubscribeGrace`）后仍未订阅即不再计入，之后订阅则按角色重新计入。正常窗口在打开连接的同一请求中发送 subscribe，60 秒只约束沉默或挂起的客户端，长于启动窗口的 30 秒保持，短于客户端 90 秒的订阅期限。
- 进入：没有界面连接，且已过启动后 15 秒宽限，并在最后一个界面断开后再等 10 秒；延迟期内有界面重连则不进入。任何界面连接立即恢复默认：Normal 优先级，节流状态交还系统。
- 启动窗口：托盘打开主窗口前先恢复默认并保持 30 秒，再把新窗口进程设为 Normal 优先级；子进程会继承托盘的 IDLE 优先级类，不这样处理会让窗口以 IDLE 运行。
- 试听的 WASAPI 泵线程始终豁免：线程级关闭 EcoQoS（高 QoS），Eco 期间播放不被拖慢。不使用 MMCSS。
- 设置失败按进程和能力只向 Trace 记录一次，不抛出、不显示给用户，也不计入后台健康。

### 按需 BLE 扫描与开盖弹窗

两个过滤的 Active watcher 只在至少存在一副已配对（Present）且在白名单（Allowed）的耳机时运行。Present 只看耳机是否仍在 Windows 已配对音频设备列表中，不要求连接，因此已配对但未连接的白名单耳机仍会扫描，后台开盖检测和 caseOpen 事件不变。没有符合条件的耳机（全部解除配对或移出白名单）时停止扫描；之后允许或配对一副耳机会重新启动，电量与开盖广播应在数秒内恢复（Windows 待验证）。扫描参数、过滤器和单包语义不变。

开盖弹窗兼容：满载 CPU 时，IDLE 优先级可能让广播回调延迟数秒。用户 2026-10-22 决定现在保持 IDLE，实现弹窗时再复核：弹窗显示前像启动主窗口那样先恢复默认；若延迟仍不可接受，改用 BELOW_NORMAL 并保留 EcoQoS。登记为[优化待办](optimization-backlog.md) PWR-01。

### 空闲唤醒、写盘与 IPC

- 试听泵线程不播放时阻塞等待，不再在首次预览后于 worker 的整个生命周期内以 20 Hz 唤醒。
- 资源协调器睡到下一次检查到期，不再每 30 秒空转：关闭在线资源时没有任何计划；偏好变化、重新下载、新美术目标和释放会唤醒它；电脑从休眠恢复时 Host 也会唤醒它（`Reschedule`），因为这一个长计时器未必计入休眠时间，休眠期间到期的检查随即按墙钟重新评估；两轮之间至少间隔 30 秒，10 分钟与 30 秒的检查节奏不变。一轮中出现取消以外的意外异常时写入 Trace 并设置 `LastError`，循环不再静默结束，按 30 秒下限重试。
- BLE 广播过期改为每个 peer 一个可复用计时器，取代每条广播一个 CTS 加延迟任务。
- 设备状态缓存：内容除 SampleTime 外未变、且样本时间前进不足 60 秒时不写盘，内存保持最新；关闭时 `FlushAsync` 写入最新样本；崩溃后磁盘上的时间最多晚 60 秒（界面只显示 HH:mm）。Host 启动时按文件内容预置每个条目的节流状态（仅 schema 2 且载入时未经修复的条目），重启后 60 秒内内容相同的首次记录不再重写文件；超过 60 秒仍照常写入。
- IPC：每条连接一个带缓冲的 `RpcLineReader`，直接读取管道或 worker 标准流的字节并增量解码 UTF-8（无 BOM；客户端严格解码），不再叠 `StreamReader`；`Stream.ReadAsync` 一有数据即返回，因此整行已到达时不会因 `StreamReader` 在缓冲读满时继续等待而被扣住。行长上限、EOF 与 '\r' 语义不变，该流不得再被其他读取者使用；旧的静态 `ReadLineAsync` 已删除；每个管道事件只序列化一次并供所有客户端共用，按客户端的合并与限额不变。线格式字节和 schema 4 不变。

### 能耗轮验证边界

策略时序、控制器、结构体布局、角色计数、序列化与写盘规则由静态分析和 Linux 上的模拟或离线测试覆盖（Host.Tests 现在可在 Linux 完整运行：110 个 PASS 行，其中 2 项只在 Windows 执行的检查提前返回并显示 SKIP，实际执行 108 项）。Linux 没有 Windows 的进程 QoS、优先级与 BLE API，**效率模式在任务管理器中的显示、IDLE 下的响应、BLE 按需启停和试听豁免均未在 Windows 验证**。依据、测试数量与 Windows 待验证清单见[后台能耗](research/backend-energy.md)。

## 验证边界

离线测试覆盖真实命名管道和子进程隔离，协议测试使用合成通知/广播。Avalonia headless 与 Win32 模拟窗口验证推送、设置绑定和退出确认。三架构发布校验 Host 的 WinExe 子系统、新 Windows TFM、托盘图标、原生图形依赖与独立许可目录。以上不证明实际耳机广播、手机并发操作、休眠恢复或 ARM64 真机蓝牙行为，也不证明效率模式在 Windows 上的实际表现。
