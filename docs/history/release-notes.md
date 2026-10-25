# 版本记录

## 0.9.2 macOS Apple Silicon 分支（2026-10-25）

版本号保持 0.9.2；新增 macOS 15+ arm64 构建、IOBluetooth/CoreBluetooth 平台实现、CoreAudio/AudioQueue 试听、原生菜单与窗口、完整 Metal 运行时，以及本地 ad-hoc 签名 DMG。抽离平台接口，统一 Unix 短路径与跨会话单例，Windows 保持既有管道名称及会话隔离。完整实现及本轮验收边界见 [macOS 说明](../macos.md)。蓝牙真机通信、校准效果、正式签名和公证未验证；早期 Windows/X4 证据不扩大到本次 Mac 后端。

## 当前开发分支：欢律单源（2026-10-18，未发布）

当前源码仍为 0.9.2。仅保留欢律配置、资源获取和界面设置，删除另一来源的配置、认证项、独占美术与复制图标；共有模型、欢律扫描图和官方听感试听保留。旧首选项保留有效欢律设置，展示缓存迁移至 schema 2；公共 IPC 升至 schema 4，更新须使用完整客户端并退出旧后台。能力门控、固件仅提醒及连接代次规则继续保留。

当前规则与验证边界见[欢律单源说明](../research/heymelody-only.md)。旧研究和许可按原始范围保全，下列历史数量不作为当前覆盖保证。

2026-10-19：顶部电量区域补回左耳、右耳和耳机仓标识，使用已核实来自欢律 17.6.5 电量控件的矢量形状，沿用当前界面前景与透明度。三种界面几何不计入设备美术的 365 条／635 文件闭包，来源与适配边界见[电量图标来源](../research/battery-device-icons.md)。产品版本不变，尚未发布。

2026-10-19（样式复原）：上述三枚标识进一步恢复为历史 `8b2576d6b` 的圆形 L／R 字母镂空与配套充电盒，替换上一轮 19×16 带柄耳塞方案。重新从欢律 17.6.5 核实三份 14×14 `battery_device` 资源，编译 XML、反编译原件与历史 pathData 一致。继续继承主题前景与透明度，其它系统／Fluent 图标、电量行为和布局不变；版本保持 0.9.2，未发布。旧方案来源记录保留在上述专题中。

2026-10-21（后端内存）：针对 Windows 上 Host 托盘常驻内存偏高的反馈，在 review/0.9.9 分支完成一轮后端内存优化。Host 的 runtimeconfig 设置 gen0 预算与 ConserveMemory 并关闭 TieredPGO；托盘改用 Win32 软件渲染，worker 入口不再加载 Avalonia；美术包的校验、下载与解压改为流式并在安装后压缩回收；快照复制改用 `Clone()`，IPC JSON 改用源生成（保留反射回退）；BLE 改为按 OPPO、OnePlus 公司 ID 过滤的两个 Active watcher；试听 WAV 单拷贝并复用缓冲；目录载荷按目录缓存。产品版本仍为 0.9.2，尚未发布；公共 IPC 仍为 schema 4，线格式经兼容检查逐字节对照，设备能力门控、固件仅提醒和 SET 规则不变。

验证边界：Linux 上 Resources、Daily、Devices、Control、Protocol、Calibration、Client、DeviceManager、Calibration.UI 与 App 十个检查全部通过（Resources 26→34、Client 163→173 为新增检查，其余数量不变）；Host 以 `-p:EnableWindowsTargeting=true` 编译无警告和错误，Host.Tests 的 Linux 子集保留改动前的 42 项并新增 6 项（试听 WAV 解析与 worker 线格式），共 48 项通过，并在同一处需要 COM 的试听测试停止。IPC 源生成与 gen0 预算有 Linux 合成探针的测量。**Windows 内存没有测量**，托盘菜单软件渲染外观、BLE 过滤的真机广播、试听播放、美术下载与完整 Host.Tests 均待 Windows 验证。依据、数字与未验证清单见[后台内存审计](../research/backend-memory-audit.md)，测量脚本为 `tools/Measure-HostMemory.ps1`，本轮未实施的其他方向见[优化待办](../optimization-backlog.md)。

2026-10-22（后端内存轮的后续验证）：上一轮提交 `53a436a` 的 CI 全部成功，包括 Windows x64 与 arm64 应用包（含 Verify-Release）以及 Windows、Ubuntu、macOS 的 Filament 运行时构建。用户随后在 Windows x64 真机上用该 CI 构建试用了大部分功能，结论为“功能上没问题”。这一结论只覆盖该范围，不是逐项功能验证，也不是内存测量；Windows 上的内存对比仍未进行。

2026-10-22（后端能耗）：在 review/0.9.9 分支完成一轮后端能耗与 CPU 优化。没有界面连接时（启动后 15 秒宽限，最后一个界面断开后再等 10 秒），托盘与全部 `--worker`、`--catalog-worker` 子进程进入 EcoQoS 加 IDLE 优先级，即任务管理器的“效率模式”；任何界面连接立即恢复默认。托盘打开主窗口前先恢复默认并保持 30 秒，新窗口设为 Normal 优先级；试听的 WASAPI 线程始终豁免；节流失败只记录到 Trace。BLE 的两个 Active watcher 改为只在存在已配对且在白名单的耳机时运行，扫描参数与后台开盖检测不变。空闲唤醒减少：试听泵线程不播放时阻塞，资源协调器睡到下一次检查到期，BLE 广播过期改为每个 peer 一个可复用计时器。设备状态内容未变且样本前进不足 60 秒时不写盘（崩溃后保存时间最多晚 60 秒）。IPC 改用带缓冲的 `RpcLineReader`，每个管道事件只序列化一次。`tools/Measure-HostMemory.ps1` 增加 CPU、上下文切换、优先级与电源节流字段。产品版本仍为 0.9.2，尚未发布；公共 IPC 仍为 schema 4，线格式字节不变。

验证边界：Linux 上 Resources 34→40、Client 173→181 为新增检查（含审查修复新增的 `RpcLineReader` 字节读取、真实管道与休眠后重新调度检查），Daily 177、Devices 21、Control 19、Protocol 77、Calibration 55、DeviceManager 8、Calibration.UI 13、App 129 改后全量重跑数量不变、全部通过；Host 以 `-p:EnableWindowsTargeting=true` 编译 0 警告 0 错误，Host.Tests 现在可在 Linux 完整运行，日志有 110 个 PASS 行，其中 2 项 Windows 专用检查（真实 WASAPI 播放器的致命初始化；试听线程空闲释放）在 Linux 上提前返回、先打印 SKIP 再计为 PASS，实际执行 108 项；此前为 48 项通过后在 COM 处停止。以上为静态分析与模拟或离线测试，没有 Linux 能耗测量。**Windows 行为均未验证**：任务管理器的效率模式显示、IDLE 下的响应、BLE 按需启停、试听豁免和测量脚本新增字段。依据、清单与顺带发现见[后台能耗](../research/backend-energy.md)和[优化待办](../optimization-backlog.md)。

2026-10-22（界面性能）：在 review/0.9.9 分支完成一轮主界面性能与体验优化。合成器改用区域脏矩形（Avalonia 默认把一帧的脏区并成一个包围矩形，左侧主图动画与右侧任何变化会合成近乎整窗重绘）；展开或收起设置卡、滚动设置区以及主窗口失活时，三维模型的空闲自转与 Lottie 降到 30 fps，拖动与惯性不变；模型帧由渲染器直接写入位图，少一次整帧复制；Lottie 末帧缓存为 GPU 图像；新增事件驱动的 GPU 缓存回收（待办 UI-02）；只读展示改用 `UiSnapshot.Clone()`（UI-01）。EQ 竖直滑块拇指居中，不再被裁。动态设置行（声音、设备、BassWave）与 EQ 预设、自定义条目在写入待确认时不再禁用，改为与降噪相同的“最后目标”排队：前一项确认后才发送，回读已满足则不写，未确认即丢弃且不重放。产品版本仍为 0.9.2，尚未发布；公共 IPC 与设备 SET 规则不变。

验证边界：Linux 上 App 129→136、Client 181→190 为新增检查，其余 DeviceManager 9、Calibration.UI 13、Calibration 55、Protocol 77、Resources 40、Devices 21、Control 20、Daily 177、Host 110 个 PASS 行与 ModelPreview 180 项数量不变、全部通过；App Release 编译 0 警告。以上为反编译核实、静态分析与无头测试。**Windows 未验证**：8cx Gen3 等设备的实际帧率、区域脏矩形在 Win32 交换链上的效果、30 fps 让路观感、GPU 缓存回收与显存、真实耳机上的排队写入。依据与后续项见[界面性能（0.9.9）](../research/ui-performance-0.9.9.md)和[优化待办](../optimization-backlog.md)。

2026-10-23（可靠性小轮）：在 review/0.9.9 分支处理待办 BUG-01、BUG-02、PWR-02、IO-02 与 PKG-01。管道请求中值为 null 而被线格式省略的 `uid`／`color` 不再导致失败，关闭黄金听感与无颜色提示的美术请求恢复正常，必需参数缺失时返回通用失败且不转发运行时异常文字。在线资源自动刷新遇到意外异常时写入 Trace、设置 `LastError` 并按 30 秒下限重试，不再静默停止。已连接但 60 秒仍未订阅的管道客户端不再计为界面，后台可进入效率模式；之后订阅则重新计入。Host 启动时按文件预置设备状态的写入节流，重启后 60 秒内内容相同的首次记录不再重写文件。核实界面与后台各自读取离线资源（Host 把 `backend/ResourceBundles` 中的美术路径交给界面，GUI 直接读取自身目录的降噪图标与听感引导动画）后，发布包只保留 GUI 目录一份 `ResourceBundles`（约 34 MB，635 个文件），Host 缺少自身副本时读取 `..\ResourceBundles`；`Build-Client.ps1` 删除后台副本并在清单写入 `hostResourceBundles`，两个发布校验脚本与 `Verify-ConnectedHeadset.ps1` 同步，旧的双副本包仍按原规则校验。

验证边界：Linux 上 Resources 40→41、Host 110→114 个 PASS 行（4 项新增，仍有 2 项 Windows 专用检查打印 SKIP）全部通过，Host 以 `-p:EnableWindowsTargeting=true` 编译 0 警告 0 错误；修改的三个 PowerShell 脚本经 PowerShell 7 语法解析。其余项目未改动未重跑。发布包结构与体积变化由 Windows CI（Build-Client 加 Verify-Release）检查，本机未构建发布包、未测量体积。**Windows 未验证**：真机上降噪图标、听感引导与设备美术从共享目录加载，效率模式在沉默客户端存在时的实际进入。

2026-10-23（主界面启动）：在 review/0.9.9 分支完成一轮主界面启动与首屏加载优化，未采用常驻界面或预启动。主程序取得单实例后，在 Avalonia 初始化之前于线程池建立后台管道，冷启动时托盘与界面并行启动；托盘单实例互斥体不存在时直接拉起后台，不再先等 300 ms。热启动时先用 subscribe 返回的有效名单进入页面，`enumerate` 改为后台核对：核对为空时回到管理器流程，失败时保留原选择。冷启动期间的客户端查询加入托盘首次初始化，不再串行启动第二个 catalog-worker。降噪图标路径改在线程池解析，artwork 索引不再常驻。设备名单只用于展示和选择，不授予控制，也不触发 SET。详见[主界面启动](../research/main-window-startup-0.9.9.md)，后续项为待办 CPU-10、CPU-11。

验证边界：Linux 上 Client 190→194、Host 114→116 个 PASS 行为新增检查且全部通过，DeviceManager 8 个用例通过。App 新增 1 项无头检查（另有 1 项原生窗口用例未运行）。App 的 `AncFeatureTests` 与 `ArtworkPersistenceTests` 各有一项检查在未修改的 `cb8171e` 上同样失败（登记为 BUG-03、BUG-04），临时改为不中断运行后，本轮与 HEAD 的结果仅差新增的 1 项通过。App 编译 0 警告 0 错误；Host 以 `-p:EnableWindowsTargeting=true` 编译 0 警告 0 错误。Windows 启动耗时、CPU 与内存未测量。

2026-10-23（主界面启动的后续反馈）：提交 `40acbb9` 的 CI 全部成功（Windows packages x64／arm64 含 Verify-Release，以及 Filament runtime）。用户下载该构建在 Windows 上试用，反馈体验良好、功能正常；这是口头反馈，未提供启动耗时数据。

2026-10-23（左侧加载状态）：左侧产品区在加载时不再显示文字或空白，统一显示延迟 200 ms 出现的 48 px 不定进度环。覆盖首次查找或下载设备美术、位图解码、Lottie 与动态 WebP 加载、3D 模型加载直至首帧。3D 原有的“正在加载耳机模型…”文字已移除，失败说明文字保留。进度环只在窗口可见且未最小化时转动；带 detail 海报的 Lottie／WebP 直接等海报，不叠加进度环。首次美术查找失败后回到通用耳机图标，重试期间不再显示进度环。3D 模型加载提速未在本轮实施：用户选择“托管层加原生小改”，并要求在 Windows 上由另一个会话完成，步骤见[交接书](../research/model-loading-handoff-2026-10-23.md)（待办 UI-11）。

验证边界：Linux 上 App 无头检查新增 1 项，并在模型生命周期、动态 WebP 与美术目录检查中加入进度环断言（含最小化时停转）。两项 BUG-03、BUG-04 既有失败以外的各组全部通过（临时改为不中断运行，共 138 个 PASS 行）。ModelPreview 180、Calibration.UI 13 项不变且通过。App 编译 0 警告 0 错误。Windows 原生窗口中的进度环外观与动画开销未检查。

2026-10-23（模型加载）：实现 UI-11 的托管并行加载与原生 SHA-256 优化。同摘要预读和正式加载共用在途读取，读取与退役、工厂创建重叠；当前选中的模型外观解析后提前读取并准备当前后端，未选中设备不提前占用模型缓存或启动探测。同一运行库、同一后端共用进行中的探测，单个调用方取消不终止共享子进程；失效代次不会恢复旧缓存。Windows 原生摘要改用 CNG，其它平台逐块计算，仍保留独立校验、材质摘要门控、输入复制和容量 2 的 LRU。继续使用 Avalonia／FluentAvalonia 的延迟 200 ms 进度环，首张尺寸匹配的帧呈现后隐藏；可选诊断增加每代加载计时，默认关闭。版本保持 0.9.2，未发布。

单独移除 `loadResources` 后的 `drain()` 曾作源码生命周期复核和 240 次交替配对测量。该候选没有稳定的首帧总收益，OpenGL Free4 还出现较大的尾部耗时，因此最终恢复原屏障；20 对候选／对照模型图像的像素完全一致。分阶段与最终性能、构建身份、稳定期资源和边界见[实测记录](../research/model-loading-performance-2026-10-23.md)，原始证据分别保全，不用分段耗时相加推断总收益。

验证边界：Windows x64 的 App 加载检查 55、Catalog 选择／取消独立检查 6 组、原生通用窗口 92、Filament 便携检查 108、两种后端的实际模型窗口各 76、ModelPreview 原生检查各 280、隔离部署原生恢复 4 项均通过，九组原生摘要参考向量及 CNG／portable 对拍通过。完整 `TestsOnly` 入口在 Resources 38/41 处停止后，已继续逐个执行其余验收程序；Resources、Host、Client 的失败保持未闭合，App 的 BUG-03／04 在基线同样存在，未宣称全量通过，也未顺手修改这些方向。原生截图只检查模型区域与新增诊断行。受控窗口私有工作集中位数 320.531→320.596 MiB（+66 KiB）、最大值 362.703→359.453 MiB；CPU 平均值 2.309%→2.213%，用户已确认按内存基本持平验收，原始正差保持。真实耳机、完整设备选择／美术 RPC 提前量与 ARM64 硬件不在本次实测证明范围。

2026-10-25（日志与退出审计）：审计 `d5ed91e` 统一 Debug 日志与 `1840a68` 托盘退出可靠性。日志以完整为先：writer 改为无界队列，磁盘落后时保留事件并记录 `logging.backlog`，不再丢弃；日志调用不再向调用方抛出异常；重复加入会关闭旧 writer；被捕获忽略的异常以 `exception.firstChance` 记录抛出点，正常退出时刷新。按用户决定，云端密钥与资源请求签名 `sign` 在任何层级（含 IPC 原始行内嵌 JSON）只记录长度和 SHA-256 前缀。补齐 worker 退出码、IPC 连接与重连、子窗口启动／退出码与 stderr、设置与缓存读写失败、设备连接入口等日志。退出路径修复投票收集异常后停在“正在确认退出”、设备管理器准备失败不投票、校准取消异常未处理且不投票、未送达的同意票残留、单个连接接入异常终止 accept 循环、退出中托盘“打开主界面”误报重新安装，并在退出回调后加 15 秒强制退出看门狗；退出各阶段（投票、断管计为同意、取消原因、stopping 与确认、收尾耗时）均有日志。日志设置界面重新设计：开关位于折叠项标题，组件保留原始名称并用“全部组件”三态复选框，日志目录独立成卡片，本轮记录状态与错误提示在折叠时也可见；修复全局卡片样式误套到展开器内部造成的双重边框。

验证边界：仅 Linux。Diagnostics 默认检查新增脱敏、不抛出、writer 替换、first-chance 与积压检查并通过，之后在既有的跨进程子组件启动检查处中止（未改动前同样失败，`--log-retention-tests` 亦然）；App `--diagnostic-settings-tests` 40、Host 131、Host `--exit-tests` 10、Client `--exit-lifecycle-tests` 13、Resources 41、Protocol 77、Daily 177、Control 20、Devices 21、Calibration 55、DeviceManager 9、Calibration.UI 15 通过。Client 默认检查在基线上同样间歇失败（BUG-07 或 demo 快照检查），App `--artwork-persistence-tests` 基线即失败（BUG-13）。设备管理器与校准窗口新增的退出回归属 `--native-exit-smoke`，未在 Windows 执行；强制退出看门狗、真实托盘退出与 Windows 原生窗口未验证。

## 历史版本

以下原段整理自 2026-10-11 文档重整前的根目录 `README.md` 与 `REPOSITORY.md`，保留原版本、日期、设备、平台及验证范围，仅调整链接位置。原段中的 “current”、默认行为与检查数量属于对应历史版本，不作为后续版本的保证。两个来源有重叠时分别列出；原文未给验证日期的条目不补写日期。

历史 `research/`、`artifacts/`、`release/` 链接指本地研究证据或发布产物，通常不随普通 Git clone 提供。完整原稿和摘要清单见本地[本轮整理档案](../../research/documentation-entrypoints-2026-10-11/README.md)；缺少该档案不影响阅读下列公开原段。现行构建与验证入口见[开发说明](../development.md)。

本页收录 0.8.11–0.9.2。更早记录分为[0.8.8–0.8.10](release-notes-0.8.8-0.8.10.md)、[0.8.4–0.8.7](release-notes-0.8.4-0.8.7.md)和[0.8.3 及更早版本与校准实测背景](release-notes-early.md)。项目早期阶段的独立记录仍见[历史索引](README.md)。

## 0.9.2（Windows 本地构建）

2026-10-11，针对 Enco Air4 Pro 的控制连接不可用反馈核查并修复 Windows SDP 缓存缺失、歧义及过期通道可能阻止建连的问题。实际 SDP 查询仍只使用所选来源允许的 UUID，要求唯一有效通道，并保持身份、能力与回执校验。连接失败保留具体步骤、命令号或 Windows 错误码，Daily、Host 和主界面显示相同诊断，成功恢复后清除。没有增加 UUID 猜测、固定通道或设置重放；配置与原 App 路由依据见[Air4 Pro 控制连接核查](../research/enco-air4-pro-control.md)。

本轮按用户授权在 Windows 本地构建 0.9.2，未发布到 GitHub。第三次完整构建退出 0，十三个默认 runner 全部通过，共 719 项数值用例及 Windows x64 SDP/ABI 离线检查五组 PASS，包括 App 106 项、Daily 139 项、Client 163 项、Host 84 项；完成 x64、x86、ARM64 共十五个 publish。完整流程零编译错误，保留一个既有 App 测试 CS0067 未使用事件警告。合并 ZIP `release/HeyMelody_Windows_0.9.2_AllArchitectures.zip` 为 748,491,996 字节；静态校验通过，核对 ZIP、程序集版本、运行时、许可及九项内嵌美术，收据为 `release/HeyMelody_Windows_0.9.2_STATIC-VALIDATION.json`。另独立核对十五个入口的 ReadyToRun 头与架构全部通过。构建日志保存在本机 `artifacts/release-0.9.2-20261011/`，摘要、检查收据和修改前文档保存在 `research/release-0.9.2-20261011/`。

前两次完整构建停在既有 Client 重连测试的计时联合断言，失败日志均保全。第二次明确观测到 400 ms 延时的计时结果为 399.946 ms，而模拟服务的同设备恢复与验证快照均正常；为计时断言增加 20 ms 容差，原暂停检查、同设备恢复及不发送 SET 的检查保留。首次失败未单独确定具体子断言原因。未执行 Air4 Pro 真机恢复、实设备 SET 或 ARM64 硬件验证。

## 0.9.1

2026-10-11 发布于 `arch/ui-perf-0.9.0` 分支，修正 0.9.0 在 Windows 实测中发现的四处主窗口问题，不改协议、依赖、字体或打包内容。

页头去掉“断开客户端”和刷新按钮，只保留应用设置按钮，并在标题与副标题两行间垂直居中。双击底栏中的当前设备卡片刷新状态；三击断开控制连接，1 秒后重新连接，不重发设置。键盘可用 F5／Shift+F5。BassWave 改为均衡器卡片的直接子行，不再露出内层卡片底色，开关与滑块回到右侧。所有下拉框文字垂直居中（FluentAvalonia 的 ComboBox 主题没有设置垂直对齐，0.9.0 实测偏上约 3 px）。操作记录恢复为只读文本框。

验证（Linux x64 无头，2026-10-11）：App 98 项（新增 6 项手势、按键、下拉框与操作记录检查），Client 162 项（新增 4 项重连检查），设备管理器 8 例，校准界面 13 项，全部通过；Host、FitTest、HearingSetup 构建通过。未验证：Windows 上雅黑字体下的实际居中效果、真实耳机的断开与重连、系统双击间隔下的三击手感。

## 0.9.0

2026-10-11 发布于 `arch/ui-perf-0.9.0` 分支。依据与验证边界见[界面与渲染性能](../research/ui-performance-0.9.0.md)。

0.9.0 完成一轮界面审计与渲染性能优化。主界面不再在窄窗口改为上下堆叠：小于 1080 px 时边距和右侧面板收窄（480 px），1600×1000 以上面板与主图放大并居中；设置按“声音”“听感与佩戴”“设备”分组，贴合度测试改为原生卡片，操作记录改为不滚动的最近 20 条加“复制全部”，切换设备时主图淡入，设置对话框的主题即时预览、取消即恢复。设备管理器的活动条不再挤动页头，错误提示固定在列表上方，列表只占一个 Tab 停靠点并支持方向键，刷新时就地更新行。两个校准窗口把提示固定在页面上方，忙碌时不再整页置灰，进度显示在标题下方，图例在深色主题下使用正确颜色，Enter／Esc 对应主按钮和取消。托盘单击打开主界面，提示显示当前已连接耳机。

渲染方面，三维模型改为按显示器刷新率逐帧旋转并新增帧率设置；动态 WebP 改为后台解码，大图异步解码；动态设置区、均衡器的 BassWave 行与设备管理器列表按 ID 复用控件；隐藏的进度条不再持续计时；升级 Avalonia 12.1.4，固定 Win32 合成与 Skia 资源参数，发布启用 ReadyToRun。界面固定使用系统微软雅黑 UI，并为没有雅黑的系统随附 Noto Sans SC（OFL-1.1），避免英文系统把汉字回退成日文字形。

本轮在 Linux（x64，无头）完成构建与测试：App 92 项、设备管理器 8 例、校准界面 13 项、Client 158 项、Calibration 55 项、Protocol 65 项、Resources 25 项、Devices 19 项、Control 19 项、Daily 131 项通过，ModelPreview 运动检查 180 项通过；Windows 发布交给 CI。没有在 Windows 显示器上实测帧率，没有原生窗口检查、实机耳机或 ARM64 执行。

## 0.8.13

原 `REPOSITORY.md`（行 49, 51）：

Version 0.8.13 adds the Golden Hearing result radar, bundled official audition music and spectrum animation. Game switches are adjacent; game type is hidden globally, and BassWave belongs to the equalizer panel. Device information displays complete running component firmware while capability checks retain their original version value. Sources, behavior and validation limits are described in the [preview and settings research](../research/hearing-preview-settings.md).

Final Windows x64 verification passed all thirteen default runners: 683 checks across the twelve application/protocol harnesses plus Windows offline checks. Separate native runs passed 59 main-window, 33 calibration-window and 32 demo checks. The local self-contained x64 package passed the Python static verifier; all 2,843 ZIP paths, sizes and SHA256 match the published directory, including the complete embedded audition WAV and source notices. Logs are under `artifacts/release-0.8.13-final-20261011`, with preserved copies in the local research archive. No headset audition, Free4 firmware capture or native ARM64 execution was performed.

原 `README.md`（行 3, 5）：

**0.8.13** 使用 Avalonia、FluentAvalonia 和共享托盘后台，在 Windows 上提供资源驱动的耳机控制、状态监听及图片、三维模型和动画展示。

0.8.13 完善黄金听感结果页：六轴雷达图显示左右耳校准曲线与增强区间，内置官方试听音乐，支持增强前后播放比较和频谱动画。两套来源的游戏开关相邻排列，全部型号隐藏游戏音效类型；BassWave™动态低音开关及强度滑块移入均衡器。设备信息按耳机组件显示完整固件版本，固件功能门槛继续使用原始版本值。依据与验证边界见 [黄金听感预览及声音设置](../research/hearing-preview-settings.md)。

## 0.8.12

原 `README.md`（行 7）：

0.8.12 区分日常状态与校准通知订阅：校准事件的明确拒绝不再引发日常状态告警，真实订阅和读取故障仍会提示。耳机状态提示移到顶部连接状态右侧，以圆点分隔，长提示可悬停查看完整内容。降噪保持默认展开的 Expander。说明见 [通知提示修复](../research/notification-header.md)。

## 0.8.11

原 `README.md`（行 9）：

0.8.11 修复游戏音效开关与类型的独立写入、失败后的状态回读及控件恢复，选项同时受所选来源配置、固件条件和耳机能力约束；Hi-Res 与空间音效按各自开关提交，不再显示互斥限制或自动关闭另一项。主界面右侧降噪恢复为默认展开的 Expander，手动折叠状态在刷新时保留。原厂依据、跨型号范围及 X4 验证见 [游戏音效与空间音效研究](../research/game-audio-spatial.md)。
