# 优化待办

本页登记 review/0.9.9 分支按方向分轮优化时发现的优化项，供后续轮次选择。入口见 [AGENTS.md](../AGENTS.md)，开发与验证入口见[开发指南](development.md)。

## 用途与规则

- 本分支（review/0.9.9）按方向分轮优化。每轮只实施用户指定的方向。
- 全局审查中发现的其他方向可用优化只登记在本页，不顺手实施；用户另行要求时才例外。
- 实施会话完成后更新条目状态，并链接对应专题文档或[版本记录](history/release-notes.md)。
- 完整证据放 `research/<专题>/`（本机完整档案，通常不随 Git clone 提供），此处只写摘要。
- 行号会随代码变化。登记时注明 commit 短哈希与日期；本轮改动过的 Resources、Contracts、Client、Host 文件的行号可能已移动，按符号定位，不按行号。
- “预估收益”是预期，不是测量结果，除非写明来源。验证层级沿用项目分级：静态分析、模拟或离线测试、Linux 测量、Windows 原生或真机验证；未写明 Windows 的条目均未在 Windows 测量。

## 条目字段

编号（方向前缀加序号）；方向（由所在小节标题和编号前缀给出）；位置（`file:line @commit`）；现象与依据（括号内为验证层级）；预估收益；风险与依赖；状态（待处理、进行中、已完成、放弃并说明理由）；登记来源（日期加当轮方向）。下列“后端内存”小节的登记来源均为 2026-10-21 后端内存轮；其他小节为同一轮全局审查中顺带发现的条目，登记来源写作“2026-10-21 后端内存（顺带发现）”。位置的 commit 为 `f415d29`（该轮审计起点）；MEM-00 与 CPU-05 涉及本轮新增或改动的文件，以该起点为基准。“能耗（PWR）”小节及 2026-10-22 后端能耗轮登记的条目以 `53a436a`（该轮起点，上一轮已提交的末尾）为基准，其位置涉及该轮尚未提交的改动，按符号定位。“能耗（PWR）”小节的登记来源为 2026-10-22 后端能耗，其中同轮顺带发现的条目写作“2026-10-22 后端能耗（顺带发现）”。2026-10-23 主界面启动轮登记的条目（CPU-09 至 CPU-11、BUG-03、BUG-04）与同日左侧加载轮登记的 UI-11以 `cb8171e`（该轮起点）为基准，涉及该轮改动的文件按符号定位，登记来源写作“2026-10-23 主界面启动”或其“（顺带发现）”。

2026-10-23 模型加载提速轮以 `f17ab2b` 为实现前基线，更新 UI-11 并登记 UI-12、BUG-05 至 BUG-08。跨方向失败只登记和保全证据；源码未变不等于已查明根因，不据此判定本轮回归或环境原因。本轮完整原始计时、日志与收据位于本机 `research/model-loading-2026-10-23/`。

## 后端内存（MEM）

MEM-00 为本轮已完成项的汇总，实施细节、Linux 检查结果与未验证内容见[后台内存审计](research/backend-memory-audit.md)。MEM-01 起为本轮未实施的后续项，其中 MEM-10 已在 2026-10-22 能耗轮完成。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| MEM-00 | Host、Resources、Contracts、Client 多文件，详见[审计](research/backend-memory-audit.md) @f415d29 起点 | 用户报告 Windows x64 托盘常驻约 80–100 MB（0.7.0 前后端分离时约 30 MB，测量口径未知）。已实施：GC 配置、托盘软件渲染与 RunTray 拆分、资源流式哈希／下载／解压、快照 Clone、BLE 公司 ID 过滤、试听单拷贝与缓冲复用、目录载荷缓存／内置目录只解析一次／OriginalBytes 仅在线保留、安装后压缩回收、IPC 源生成（静态分析、模拟或离线测试、Linux 测量） | 未在 Windows 测量 | Windows 实测与真机验证待补，清单见审计文档 | 已完成（Linux 验证，Windows 待验证） | 2026-10-21 后端内存 |
| MEM-01 | `src/HeyMelody.Resources/ResourceCoordinator.cs:455-463` `CachedCatalogExists` @f415d29 | 每次 10 分钟配置检查都 ReadAllBytes、AES-GCM 解密、gunzip 到 MemoryStream 再 SHA256，瞬时 LOH 峰值为配置大小的数倍（静态分析） | 降低每 10 分钟的瞬时 LOH 峰值；未测量 | 缓存有效性判断语义（摘要、密钥校验）须不变 | 待处理 | 2026-10-21 后端内存 |
| MEM-02 | `src/HeyMelody.Resources/ResourceCatalog.cs:37-85` `Parse` @f415d29 | utf8.ToArray、GetRawText（120 KB 内置目录约生成 240 KB 的 UTF-16 字符串，进 LOH）、JsonNode.Parse、SerializeToElement，逐 profile SerializeToElement／DeepClone；常驻 Raw 与逐 profile 副本；启动、每次在线更新和每次 worker 解析都执行（静态分析） | 减少目录常驻堆与瞬时 LOH；未测量 | 须保持目录摘要、OriginalBytes 与修复路径语义；需 Resources.Tests 对拍 | 待处理 | 2026-10-21 后端内存 |
| MEM-03 | `src/HeyMelody.Resources/ResourceCache.cs:9-26` `ResourceCacheCodec.Encrypt/Decrypt` @f415d29 | MemoryStream 与 ToArray 多份拷贝，配置上限 8 MiB（静态分析） | 降低缓存读写的瞬时峰值；未测量 | 信封格式（12 字节 nonce、16 字节 tag）与旧缓存须字节兼容 | 待处理 | 2026-10-21 后端内存 |
| MEM-04 | `src/HeyMelody.Resources/VendorResourceClient.cs:103-110` `BoundedReadAsync` @f415d29 | 每次调用分配 byte[81920]，MemoryStream 倍增后 ToArray；每 10 分钟逐目标的元数据 POST 也走此路径。包下载已于本轮改为流式，此处仅余目录与元数据响应（静态分析） | 小；降低每 10 分钟的瞬时分配 | 响应大小上限与取消语义须不变 | 待处理 | 2026-10-21 后端内存 |
| MEM-05 | `src/HeyMelody.Resources/ArtworkPackageExtractor.cs` `IsAnimation`、`IndexAnimation`（:196、:231）@f415d29 | 仍整读 Lottie JSON（上限 24 MiB）并 JsonNode.Parse，保留是为了 UTF-8 BOM 语义不变（静态分析） | 仅影响在线美术安装时的瞬时峰值；未测量 | 须保持 BOM 与解析语义，需对拍测试 | 待处理 | 2026-10-21 后端内存 |
| MEM-06 | `src/HeyMelody.Resources/BundledResourceStore.cs:34-39` @f415d29 | `GetDefaultKey` 每次 `ActiveKey()` 都重新反序列化 default-keys.json；`ReadEmbedded` 双份拷贝；每个实例都反序列化 artwork.json（224 KB），Host 中的实例数待统计（静态分析） | 小；确认实例数后再定 | 本轮已把 GetCatalog 改为实例级 Lazy（见 MEM-00），此处为其余部分 | 待处理 | 2026-10-21 后端内存 |
| MEM-07 | `src/HeyMelody.Resources/ResourceCoordinator.cs:95` `ExportEffectiveCatalogJson` @f415d29 | 每次调用分配整份字符串；src 与 tests 中已 grep，无调用，疑为死代码（静态分析；公开成员，外部引用未查） | 无运行时收益，仅删除死代码 | 删除前确认无外部或反射使用，属公开成员变更 | 待处理 | 2026-10-21 后端内存 |
| MEM-08 | `src/HeyMelody.Host/BackendSupervisor.Resources.cs:124-132`、`NativeWorker.cs` @f415d29 | 每个 worker 都保留完整目录而只需一个 profile；gzip 加 base64 的目录在每次 worker 启动时经 stdin 发送，可只传该 profile 或缓存路径。本轮只让 Host 端载荷按目录实例缓存（见 MEM-00）（静态分析） | 每个 worker 的常驻堆与启动开销；未测量 | RpcWire 每行 524,288 字符上限；worker 的目录解析行为须不变；涉及 Host 与 worker 内部约定 | 待处理 | 2026-10-21 后端内存 |
| MEM-09 | `src/HeyMelody.Host/CalibrationAuditionPlayer.cs:43-75` @f415d29 | 试听样本（约 4.4 MB 的 short[]，在 LOH）从 Prepare 到 Cleanup 一直存活，可改为直接读内嵌资源的 UnmanagedMemoryStream（静态分析） | 试听期间约少 4.4 MB 常驻 | 须在 Windows 真机验证试听播放；与本轮 `AuditionWave` 流式解析相邻，不要回退 | 待处理 | 2026-10-21 后端内存 |
| MEM-10 | `src/HeyMelody.Host/BackendSupervisor.cs:440-467` `ScheduleBroadcastExpiryLocked` @f415d29 | 每条匹配广播都新建 linked CTS 与 Task.Delay，每个 peer 一个可复用计时器即可（静态分析） | 广播密集时减少分配；未测量 | 须保持过期、开盖去重与 30 秒新鲜性语义；需真机广播验证 | 已完成（2026-10-22 能耗轮：每个 peer 一个可复用计时器，Linux 离线测试通过，Windows 真机广播待验证；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存 |
| MEM-11 | `src/HeyMelody.Host/NativeWorker.cs:221-222`、`:277-278` @f415d29 | worker 的 stdout、stderr 在匿名管道上各占用一个线程池线程的读取（静态分析） | 小 | 需 Host.Tests 覆盖管道读取行为 | 待处理 | 2026-10-21 后端内存 |
| MEM-12 | `src/HeyMelody.Windows/RfcommConnection.cs:33-34`、`:144` @f415d29 | RFCOMM 的 RegisteredWaitHandle 每个网络事件重新注册（静态分析） | 小 | 属接收通道，须保持每连接单一接收通道；需真机验证 | 待处理 | 2026-10-21 后端内存 |
| MEM-13 | `src/HeyMelody.Host/DeviceStateStore.cs`、`DeviceWhitelistStore.cs`、`DeviceAppearanceStore.cs` @f415d29 | Host 持久化存储仍用反射 JSON；Linux 探针的 host 场景显示反射 JSON 比源生成多约 5 MiB RSS，可考虑文件字节保持不变的源生成上下文（Linux 测量，探针合成负载；Windows 未测） | 量级约数 MiB（探针 host 场景）；未在 Windows 复测 | 持久化文件字节与中文转义策略须不变，旧文件须可读 | 待处理 | 2026-10-21 后端内存 |
| MEM-14 | `src/HeyMelody.Host/TrayApplication.cs` @f415d29 | 托盘为两项菜单仍加载 Avalonia.Controls 与 FluentAvalonia；原生 Win32 托盘菜单可能更省，但本轮为保持外观和 zh-CN 字体而决定不做（静态分析层面的假设，未测量） | 未测量 | 菜单外观、字体、DPI 与深浅色回归；需 Windows 视觉验证 | 待处理（本轮已决定不做） | 2026-10-21 后端内存 |
| MEM-15 | `src/HeyMelody.Calibration/HearingRadarMath.cs:21-26`、`:96` @f415d29 | 预览期间每 50 ms 分配 double[6] 与 ReadOnlyCollection（静态分析） | 小 | 与结果雷达显示一致性需单元测试 | 待处理 | 2026-10-21 后端内存 |

## 能耗（PWR）

PWR-00 为 2026-10-22 后端能耗轮已完成项的汇总，依据、测试数量与 Windows 待验证清单见[后台能耗](research/backend-energy.md)。该轮同时完成了上文 MEM-10 与下文 IO-01、CPU-01、CPU-02。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| PWR-00 | `src/HeyMelody.Host/EnergyPolicy.cs`、`EnergyController.cs`、`WindowsProcessEnergy.cs`（新增），`BackendPipeServer.cs`、`TrayApplication.cs`、`WindowsStateMonitor.cs`、`BackendSupervisor.cs`、`CalibrationAuditionPlayer.cs`、`DeviceStateStore.cs`，`src/HeyMelody.Resources/ResourceCoordinator.cs`，`RpcWire.cs`、`ClientBridge.cs` @53a436a 起点 | 无界面连接时（启动后 15 秒宽限、最后一个界面断开后 10 秒延迟）托盘与全部 worker 进入 EcoQoS 加 IDLE 优先级，界面连接立即恢复，启动窗口前先恢复默认并保持 30 秒，试听线程豁免；BLE 两个 watcher 只在存在已配对且在白名单的耳机时运行；试听泵与资源协调器改为空闲阻塞或睡到到期，BLE 过期每 peer 一个计时器；设备状态内容未变时不写盘；IPC 缓冲读取与单次序列化。已实施部分由静态分析与 Linux 模拟或离线测试验证，未做 Linux 能耗测量 | 未在 Windows 测量 | 效率模式显示、IDLE 响应、BLE 启停、试听豁免等 Windows 待验证，清单见[后台能耗](research/backend-energy.md) | 已完成（Linux 验证，Windows 待验证） | 2026-10-22 后端能耗 |
| PWR-01 | 开盖弹窗实现处（尚未实现）；`EnergyPolicy.cs`、`BackendPipeServer.cs` 启动窗口路径 @53a436a | 开盖弹窗实现时复核效率模式优先级（IDLE→BELOW_NORMAL 备选）：满载 CPU 时 IDLE 优先级可能让 BLE 广播回调延迟数秒，用户 2026-10-22 决定先保持 IDLE（静态分析层面的推断，未测量） | 避免开盖弹窗因广播回调延迟而迟到；未测量 | 弹窗显示前须像启动主窗口那样先恢复默认；延迟不可接受时改 BELOW_NORMAL 并保留 EcoQoS；须在 Windows 满载下测广播回调延迟 | 待处理 | 2026-10-22 后端能耗 |
| PWR-02 | `src/HeyMelody.Host/BackendPipeServer.cs` `UpdateGuiCount`、subscribe 处理 @53a436a | 管道没有 subscribe 超时：已连接但一直不订阅（保持沉默）的客户端按角色未知的界面计入 GUI 数，直到它断开，期间托盘与 worker 不会进入效率模式；observer 以外全部计入是有意的，以免启动中的窗口遇到被限速的后台（静态分析；CPU 与能耗） | 异常或挂起的客户端存在时仍可进入效率模式；未测量 | 超时须长于正常界面的启动与订阅耗时，不能让慢启动的窗口遇到 Eco；需 Host.Tests 覆盖 | 已完成（2026-10-23 可靠性小轮：连接 60 秒仍未订阅即不再计入界面，之后订阅按角色重新计入，沿用 EnergyPolicy 的时钟；Host.Tests 新增检查在 Linux 通过，Windows 未测；见[后台架构](backend-architecture.md)） | 2026-10-22 后端能耗（顺带发现） |

## 界面与前端（UI）

UI-00 为 2026-10-22 界面性能轮（起点 `a13e236`）已完成项的汇总，依据、反编译核实、测试数量与 Windows 待验证清单见[界面性能（0.9.9）](research/ui-performance-0.9.9.md)。该轮同时完成了 UI-01、UI-02，并核实了 UI-03；UI-04 起为该轮登记的后续项，位置以 `a13e236` 为基准，涉及该轮改动的文件按符号定位。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| UI-00 | `src/HeyMelody.App/RenderingSetup.cs`、`EncoFree4ModelView.cs`、`AnimationYieldTriggers.cs`（新增）、`MainView.axaml`，`src/HeyMelody.Artwork/LottieView.cs`、`AnimationYield.cs`、`GpuJanitor.cs`（新增），`src/HeyMelody.Client/MainViewModel.WriteQueue.cs`（新增）等 @a13e236 起点 | 合成器默认把一帧的全部脏区并成一个包围矩形（反编译核实），改为区域跟踪；界面展开、滚动与窗口失活时，模型空闲自转与 Lottie 降到 30 fps；模型帧直接写入锁定位图，少一次整帧复制；Lottie 末帧缓存为 GPU 图像；EQ 竖直滑块居中不再被裁；动态设置行与 EQ 选择改为非阻塞排队（静态分析、反编译与 Linux 无头测试） | 降低弱 GPU 上的整窗重绘与 UI 线程复制；未在 Windows 测量 | 8cx 等真机的帧时间、Mica 与合成行为、真机写入均待 Windows 验证 | 已完成（Windows 待验证） | 2026-10-22 界面性能 |
| UI-01 | `src/HeyMelody.Client/MainViewModel.Selection.cs:45` `ReadOnlyPresentation` @f415d29 | 用默认选项的 JSON 往返复制快照，可改用 `UiSnapshot.Clone()`（静态分析） | 减少界面端分配与反射序列化；未测量 | 须保持“只读展示不含暂存选择和 EQ 草稿”的语义；Client.Tests 覆盖 | 已完成（2026-10-22 界面性能轮，改用 `Clone()`，语义由既有 Clone 等价检查覆盖） | 2026-10-21 后端内存（顺带发现） |
| UI-02 | `src/HeyMelody.App/RenderingSetup.cs:17`、`:22` @f415d29 | GUI 的 Skia GPU 资源上限为 256 MiB，而 Avalonia.Skia 12.1.4 不按时清理 GPU 缓存（2026-10-22 反编译确认：只调用 `SetResourceCacheLimit`，从不 purge；`WriteableBitmap` 每次修改生成新 `SKImage`，即新纹理） | 可能降低 GUI 长时间运行的显存与内存占用；未测量 | 影响三维模型与 120 Hz 渲染预算；需 Windows 原生窗口验证 | 已完成（保留 256 MiB 上限，新增事件驱动的 `GpuJanitor`：动画让路结束或窗口失活后最多每 10 s 调用 `PurgeUnusedResources(30 s)`，无空闲定时器；Windows 显存未测量） | 2026-10-21 后端内存（顺带发现） |
| UI-03 | FluentAvaloniaTheme 3.1.0 包依赖 @f415d29 | 该主题包把 Avalonia.Controls.DataGrid 和 ColorPicker 带入每个 GUI 进程（包元数据，静态分析）。2026-10-22 反编译：`ControlThemes/Controls.axaml` 无条件以 `typeof(DataGrid)`、`typeof(ColorPicker)` 等为键注册延迟主题，主题类没有排除选项 | 减少加载的程序集；未测量 | 只能替换主题包或修补其资源字典，会影响外观，见 [FluentAvalonia 调研](research/fluent-avalonia.md)；加载时机未运行验证 | 待处理（本轮核实后不实施：无受支持的排除方式） | 2026-10-21 后端内存（顺带发现） |
| UI-04 | `src/HeyMelody.App/EncoFree4ModelView.cs` `PresentLatestFrame`，`src/HeyMelody.Rendering.Filament` @a13e236 | 模型每帧仍经 GPU 回读、CPU 复制到位图、再由 Skia 上传新纹理（静态分析）；可改为 Filament 与 Avalonia 共享 GPU 纹理（D3D11/Vulkan 互操作） | 去掉回读与重新上传，弱 GPU 上收益最大；未测量 | 需要原生层与 Avalonia GPU 互操作改造，OpenGL/Vulkan 两后端分别验证；属架构改动 | 待处理 | 2026-10-22 界面性能 |
| UI-05 | `EncoFree4ModelView.cs` `FitCamera`（物理像素尺寸、`MsaaSamples = 4`）@a13e236 | 模型按物理像素和 4×MSAA 渲染，200% 缩放的大窗口约 1440×1280（静态分析） | 弱 GPU 上可按渲染耗时降低分辨率或 MSAA；未测量 | 画质取舍需用户决定；须在 8cx 等真机测量 | 待处理 | 2026-10-22 界面性能 |
| UI-06 | Avalonia 12.1.4 Win32 合成（WinUI/DirectComposition 交换链）| 反编译未见部分呈现（partial present）；区域脏矩形减少的是重绘，交换链是否仍整窗提交未核实（静态分析，低置信度） | 若整窗提交，可进一步降低 GPU 带宽；未测量 | 需 Windows 上的 GPU 计数或 PIX 测量；可能依赖上游改动 | 待处理 | 2026-10-22 界面性能 |
| UI-07 | `src/HeyMelody.Artwork/LottieView.cs` `LottieHandler` @a13e236 | 未让路时循环 Lottie 仍按每个合成帧执行 Skottie（0.9.0 为 120 Hz 平滑有意保留；静态分析） | 若 Windows 测量显示 Lottie 仍是瓶颈，可按动画原生帧率或缓存循环帧 | 违背 0.9.0 的平滑决定，需用户确认；缓存循环帧内存开销大 | 待处理 | 2026-10-22 界面性能 |
| UI-08 | `src/HeyMelody.App/RenderingSetup.cs`／Avalonia 12.1.4 ARM64 Adreno 选卡 | 本机默认选卡命中上游屏蔽规则，普通 UI 实际为 WARP；同 WebP 场景显式 Adreno 后回调节拍与 CPU 明显改善，见[本机卡顿调查](research/ui-lag-arm64-2026-10-23.md) | 解除已实测的主要软件绘制成本；数据仅适用于本机离线组合压力场景，不是上屏 FPS | 上游历史透明窗口问题仍需兼容验证；覆盖所有共享 RenderingSetup 的窗口、弹出层及设备丢失恢复，保留回退 | 已完成：所有 Windows 默认首适配器与持久 Basic Render 开关；本机 ARM64 离线原生与失败后备已验，x64／x86 仅构建，旧驱动／长期兼容性仍有边界 | 2026-10-23 本机 ARM64 卡顿调查 |
| UI-09 | `src/HeyMelody.Artwork/AnimatedWebpView.cs`／X4 主图 | 真实主图为 780×780、约30.3帧／秒 WebP；同组件同源暂停后帧回调由约86升至112次／秒，已定位持续更新路径的额外成本，未拆分解码／复制／纹理上传占比 | 硬件路径上的后续帧时改善；保持素材、画质与完整功能 | 需继续用同源对照与线程／纹理数据定位，不能把源30帧当作整个UI上限 | 调查定位完成，机制细分与修复待处理 | 2026-10-23 本机 ARM64 卡顿调查 |
| UI-10 | 本地 Windows 安装器与 `uiBasicRenderMode` 兼容默认值 | 旧 Adreno 680／685／690 的 QCOM043A 家族使用保守默认，依据及范围见[安装器说明](installer.md)与[上游历史](research/avalonia-adreno-history-2026-10-23.md) | 安装时启用 Basic Render，降低首次启动失绘风险 | 仅匹配当前存在的显示设备硬件 ID；匹配后将既有 false 也设为 true，其他设备不读取或修改偏好；不按所有 ARM 设备笼统启用 | 首轮 NSIS 后按用户要求切换 Inno Setup modern；保留当前用户安装、单一开始菜单入口与持久桌面选项，辅助工具 28 组检查通过；本机静默验收记录见安装器说明，旧 GPU／ARM64 运行仍待实机验证 | 2026-10-24 用户指定安装器方向及 Inno 替换 |
| UI-11 | `DeviceModelView.LoadAsync`／`VerifiedModelCache`、`NativeBackendProbe`／工厂、`hm_sha256`／`RendererState::load` @f17ab2b | 已实现读取与退役／创建重叠、同摘要在途合并及 LRU 2、仅选中 hero 模型解析后提前准备、共享探测与 CNG／portable SHA；Windows x64 Vulkan／OpenGL 的隔离模型区分阶段实测见[本轮摘要](research/model-loading-performance-2026-10-23.md)，不覆盖 Catalog／美术 RPC 提前量 | 配对热加载中位数改善 0.4%–11.5%，部分冷加载近持平；Vulkan Free4 热加载最大值变差，完整尾部保留；drain 候选未达到门槛，已恢复 | 保留摘要、材质门控及既有退役机制，静态发现的既有交接窄窗另列 BUG-08；关联 UI-04／UI-05，但本轮保留 GPU 回读→CPU 位图→Skia 上传、原始模型／贴图、物理像素与 4×MSAA；跨进程缓存另列 UI-12，实施约束见[交接书](research/model-loading-handoff-2026-10-23.md) | 已完成（实现、本地专项及分阶段实测完成；中位数 +66 KiB 按用户确认视为基本持平，峰值下降 3.25 MiB；CI 与未验证边界见实测记录） | 2026-10-23 模型加载提速 |
| UI-12 | `NativeBackendProbe`／`payload.Identity` 的跨进程成功缓存 @f17ab2b | 本机基线三来源冷探测中位数为 Vulkan 828.4–871.8 ms、OpenGL 427.3–465.3 ms，每来源各 5 个新进程；见[加载基线](research/model-loading-performance-2026-10-23.md#加载基线)。当前成功缓存仍仅在进程内 | 潜在省去新进程首次探测等待；跨进程缓存未实施、收益未实测 | 改变每次启动的驱动崩溃隔离边界；需运行库、GPU／驱动身份失效与创建前 pending 崩溃守护；须用户另行决定，不以现有耗时自动扩大授权 | 待用户另行决定（本轮不实施） | 2026-10-23 模型加载提速（后续取舍） |

## 磁盘 I/O（IO）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| IO-01 | `src/HeyMelody.Host/DeviceStateStore.cs:113-123`、`:137-165`、`:216`、`:277`；调用方 `BackendSupervisor.cs:600-607` @f415d29 | 每个 SPP 快照都重写并以 WriteThrough 落盘，因为 SampleTime 总在变；`:277` 为检查大小而序列化整份展示。建议仅 SampleTime 前进且小于 60 秒时跳过落盘（界面只显示 HH:mm）（静态分析） | 减少频繁的磁盘写与 fsync；未测量 | 须保持缓存展示与“上次连接时间”语义；设备状态缓存见[设备状态](device-state.md) | 已完成（2026-10-22 能耗轮：内容未变且样本前进不足 60 秒时不写盘，关闭时写入最新样本；Linux 离线测试通过，Windows 未验证；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| IO-02 | `src/HeyMelody.Host/DeviceStateStore.cs` `Load`、`Remember` @53a436a | `Load` 不预置写入节流状态（已排队内容与样本时间），重启后第一次 `Remember` 总会重写文件，即使内容与文件相同（静态分析） | 每次重启每个设备少一次写盘；小 | 预置须与文件内容一致并保持 60 秒规则；崩溃后保存时间最多晚 60 秒的约定不变 | 已完成（2026-10-23 可靠性小轮：Load 按文件预置 schema 2 且未经修复条目的节流状态，60 秒规则与关闭时写入不变；Host.Tests 新增检查在 Linux 通过） | 2026-10-22 后端能耗（顺带发现） |

## CPU、IPC 与启动（CPU）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| CPU-01 | `src/HeyMelody.Contracts/RpcWire.cs:38-52` `ReadLineAsync` @f415d29 | 每次 ReadAsync 只读一个字符并写入 StringBuilder（行约 14 KB）（静态分析） | 降低 IPC 读取的 CPU 与异步开销；未测量 | 须保持每行 524,288 字符上限与畸形输入行为 | 已完成（2026-10-22 能耗轮：带缓冲的 `RpcLineReader`，Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| CPU-02 | `src/HeyMelody.Host/BackendPipeServer.cs:251`、`:314` @f415d29 | 同一事件对每个客户端分别序列化（静态分析） | 多客户端时减少重复序列化；未测量 | 每客户端按键合并的输出队列语义须不变 | 已完成（2026-10-22 能耗轮：每个事件序列化一次，线格式字节不变，Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-21 后端内存（顺带发现） |
| CPU-03 | 快照事件，`src/HeyMelody.Contracts/UiContracts.cs:69-100` @f415d29 | 每次快照事件都重复发送不变的 AncModes、FeatureSections、EqualizerEntries（合成估计约 14 KB）；增量协议需提升 IPC schema（静态分析加合成估计，非真机） | 降低 IPC 流量与序列化；未测量 | 需提升 IPC schema 版本，与旧后台的升级处理见[后台架构](backend-architecture.md) | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-04 | `--catalog-worker` 启动，`src/HeyMelody.Host/NativeWorker.cs:301-316` @f415d29 | 每次目录刷新新建一个进程（已有 200 ms 合并）；进程隔离是设计要求（静态分析） | 进程创建开销；收益有限 | 进程隔离不能放弃；可作调度优化而非改变隔离 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-05 | `src/HeyMelody.Contracts/RpcJsonContext.cs`（本轮新增，基准 @f415d29）与探针日志 | 源生成 JSON 首次使用仍 JIT 约 1,000 个方法（`JsonPropertyInfoValues<T>` 与上下文 lambda 以 Tier0 编译）；可试 GenerationMode=Serialization，或查明 crossgen 拒绝预编译的原因（Linux 测量，探针日志，合成负载） | 降低首次往返的 JIT 时间；未在 Windows 测量 | 须保持线格式字节兼容；Windows ReadyToRun 行为可能不同 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-06 | `src/HeyMelody.Host/CalibrationAuditionPlayer.cs` `Fill` @f415d29 | 逐帧复制并带取模与边界检查，分段 Array.Copy 即可（静态分析） | 试听期间每 5 ms 的 CPU；小 | 须保持循环与声道映射输出样本逐位一致 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-07 | `src/HeyMelody.Host/WindowsStateMonitor.cs:220` `Advertisement` @f415d29 | 每条匹配广播分配一个 DataReader 与 byte[]（按公司 ID 过滤后影响较小）（静态分析） | 小 | 须保持单包解析语义，需真机广播验证 | 待处理 | 2026-10-21 后端内存（顺带发现） |
| CPU-08 | `src/HeyMelody.Client/ClientBridge.cs` `Connection.Reader` @53a436a | `RpcLineReader` 构造在 `StreamReader`（4096 字符缓冲）之上，字节经两层缓冲与拷贝，可直接从管道字节解码（静态分析；Host 端读取点是否同样叠两层未核对） | 小；每次读取少一层拷贝 | 须保持 UTF-8 严格解码、行长上限与 EOF、'\r' 语义 | 已完成（2026-10-22 审查修复：`RpcLineReader` 以 `Stream` 构造并增量解码字节，客户端保持严格解码，Host、worker 端同样直接读流；Linux 离线测试通过；见[后台能耗](research/backend-energy.md)） | 2026-10-22 后端能耗（顺带发现） |
| CPU-09 | `src/HeyMelody.App/Program.cs`、`App.axaml.cs`、`MainWindow.axaml.cs`、`DeviceStartupCoordinator.cs`、`DeviceVisuals.cs`，`src/HeyMelody.Client/ClientBridge.cs`、`MainViewModel.Events.cs`，`src/HeyMelody.Host/BackendSupervisor.cs`、`BackendPipeServer.cs` @cb8171e 起点 | 主界面启动轮已完成项汇总：取得主实例后在 Avalonia 初始化前提前连接管道；托盘互斥体不存在时不再空等 300 ms；热启动先显示 subscribe 返回的有效名单，`enumerate` 改为后台核对（空名单时回到管理器流程）；冷启动时客户端查询加入托盘首次初始化，不再串行启动第二个 catalog-worker（部分缓解 CPU-04）；降噪图标路径在线程池解析，artwork 索引不再常驻（静态分析，Linux 离线测试） | 冷启动与热启动均缩短遮罩时间；内存不增，预计略降；未测量 | Windows 启动耗时、CPU 与内存未测量；原生窗口用例未运行。详见[主界面启动](research/main-window-startup-0.9.9.md) | 已完成（Linux 验证，Windows 待验证） | 2026-10-23 主界面启动 |
| CPU-10 | `src/HeyMelody.Host/TrayApplication.cs` 管道服务启动与 `StartAsync`，`BackendSupervisor.InitializeAsync` @cb8171e | 冷启动时管道要等托盘的 Avalonia 初始化完成才开始服务；首次初始化先做资源初始化，再做首次目录刷新，两者串行（静态分析） | 冷启动再缩短；未测量 | 外观提示与目录依赖资源初始化；托盘菜单依赖 Avalonia 调度器；需 Windows 测量托盘各阶段耗时后再决定 | 待处理 | 2026-10-23 主界面启动（顺带发现） |
| CPU-11 | GUI runtimeconfig（`src/HeyMelody.App/HeyMelody.App.csproj`）@cb8171e | 托盘已关闭 TieredPGO，GUI 仍默认开启；在 ReadyToRun 之上，动态 PGO 的插桩阶段可能增加启动和交互初期的 JIT 与内存（推断，未测量） | 可能小幅降低启动 CPU 与内存 | 可能降低稳态热点性能，影响 120 Hz 目标；需 Windows 对照测量 | 待处理 | 2026-10-23 主界面启动（顺带发现） |
| CPU-12 | `src/HeyMelody.Client/ClientBridge.Lifecycle.cs` `StartOwnedComponent`／`StartBackendIfAllowed` @1840a68 | 进程级 `BridgesGate`（后者还持 `_stateGate`）跨越 `Process.Start`、owner 描述文件读取与后台启动；`ReadMessagesAsync`、`CommitStopping` 需要同一把锁，启动慢时会推迟消息泵，若在 UI 线程调用还会卡顿（静态分析，未发现死锁，未测量） | 缩短锁持有时间，避免启动子进程时阻塞消息处理 | 拆锁须保持“退出后不得复活后台”的屏障语义，需补并发检查 | 待处理 | 2026-10-25 日志与退出审计（顺带发现） |

## 发布体积（PKG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| PKG-01 | `Build-Client.ps1:219-222` @f415d29 | HeyMelody.Resources 的 Bundles/data（约 34 MB、635 个文件）同时复制进 GUI 目录和 backend；Host 只检查存在并把路径交给界面（静态分析） | 发布包减小约 34 MB 每份副本；未测量 | 去重前先确认界面实际读取哪一份；Verify-Release 与三架构包结构需同步 | 已完成（2026-10-23 可靠性小轮：确认 Host 用 backend 副本把美术路径交给界面，GUI 直接读自己目录的副本（降噪图标、听感引导动画），两份都在用；改为只发 GUI 一份，Host 缺自身副本时读 `..\ResourceBundles`，Build-Client 删除后台副本并在清单标记，Verify-Release／verify_release.py／Verify-ConnectedHeadset 同步；体积收益未测量，Windows CI 构建与静态校验见[版本记录](history/release-notes.md)） | 2026-10-21 后端内存（顺带发现） |

## 正确性（BUG）

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| BUG-01 | `src/HeyMelody.Host/BackendPipeServer.cs:159` @f415d29 | 值为 null 的 uid 或 color 被 WhenWritingNull 省略后，`GetProperty` 抛出（静态分析；既有行为，本轮未改） | 修复一个已有的边界缺陷，无内存收益 | 需先确认触发路径，修复须有测试依据 | 已完成（2026-10-23 可靠性小轮：触发路径为关闭黄金听感（uid 为 null）与无颜色提示的美术请求；缺失的可空参数按 null 处理，必需参数缺失返回通用失败且不转发运行时异常文字；Host.Tests 新增管道检查在 Linux 通过） | 2026-10-21 后端内存（顺带发现） |
| BUG-02 | `src/HeyMelody.Resources/ResourceCoordinator.cs` `StartAutoRefresh` 的循环 @53a436a | 自动刷新一轮中抛出 OperationCanceledException 以外的异常时，循环内的 `try` 只捕获取消，异常逃出 `Task.Run`，循环静默结束，此后不再自动刷新也没有记录（静态分析；既有行为，改为睡到到期后仍然如此） | 修复静默停摆；无能耗收益 | 须保持取消与释放语义和 30 秒下限；应记录而非吞没异常；需 Resources.Tests 覆盖 | 已完成（2026-10-23 可靠性小轮：意外异常写入 Trace 并设置 LastError，按 30 秒下限重试，取消与释放语义不变；Resources.Tests 新增检查在 Linux 通过） | 2026-10-22 后端能耗（顺带发现） |
| BUG-03 | `tests/HeyMelody.App.Tests/AncFeatureTests.cs` `CheckRowGeometry` @cb8171e；2026-10-24 复核基线 @bc1b8c3 | Linux 无头运行 App.Tests 时“标题与换行说明在左、完整开关在右”的设置行几何检查失败，并中断整个 runner；未修改的 `cb8171e` 上同样失败（Linux 测量）。2026-10-23 模型加载轮的 Windows 基线与本轮默认 runner 也在同一 ANC 行几何检查失败，见[Windows 复核范围](research/model-loading-performance-2026-10-23.md#验证边界与复核入口)及[本地默认检查收据](../research/model-loading-2026-10-23/remaining-tests-20261023-181549.final.json)。2026-10-24 黄金听感 UI 轮的 Windows 默认 App.Tests 再次在 420 px 窄窗状态触发同一布局断言（`CheckRowGeometry:228`、`PreviewAsync:167`）；App 源码与测试无本轮改动，未进行该无关界面的截图审计。见[本地 App 日志](../artifacts/hearing-result-ui-20261024/HeyMelody.App.Tests.log)，本地产物不随 clone 提供 | 恢复 App.Tests 的默认布局验收 | 需区分既有布局问题和平台／字体度量差异；当前日志未记录断言失败时各矩形，根因未知，修复须有测试依据 | 待处理（当前 UI 轮只登记，不修无关 ANC 界面） | 2026-10-23 主界面启动及模型加载轮；2026-10-24 黄金听感 UI（跨方向复核） |
| BUG-04 | `tests/HeyMelody.App.Tests/ArtworkPersistenceTests.cs` `SettingsCardsAsync` @cb8171e | “每项设置（含 3D 帧率）均为图标卡片”检查失败，未修改的 `cb8171e` 上同样失败；可能与新增的 Basic Render 设置项有关（Linux 测量，原因未核实）。2026-10-23 在 Windows 使用保全的基线 App 程序集独立运行美术验收，同样触发该断言；见[Windows 复核范围](research/model-loading-performance-2026-10-23.md#验证边界与复核入口)，不改写原 Linux 证据 | 恢复该检查 | 先确认新设置项的预期呈现，再改实现或检查 | 待处理 | 2026-10-23 主界面启动（顺带发现）；同日模型加载轮补 Windows 保全基线复现 |
| BUG-05 | `src/HeyMelody.Resources/ResourceCoordinator.cs` 提取／发布，`tests/HeyMelody.Resources.Tests/Program.cs` `LeasedRepublish`／`ExtractorUpgrade`／`DamagedMembers` @f17ab2b | Windows 默认 Resources.Tests 为 38/41：重下载失败、初始美术失败及含短 GLB 的资源包验收失败；底层异常被通用 catch 丢弃，根因未观测。src、tests、Bundles 与完整 BCL 依赖配置相对 f17 未变；该次默认检查已提权，不能先归因沙箱。见[本地验证收据](../research/model-loading-2026-10-23/remaining-tests-20261023-181549.final.json) | 恢复资源默认验收，明确提取／发布失败原因；未测性能收益 | 应先取得真实异常再判断；现有证据不能判本轮回归或已证环境，不放宽完整性／跳过成员规则 | 待处理（未闭合；本轮仅登记，不扩实施） | 2026-10-23 模型加载提速（跨方向发现） |
| BUG-06 | `src/HeyMelody.Host` 持久化，`tests/HeyMelody.Host.Tests/HostEfficiencyTests.cs` `UntilAsync` @f17ab2b；2026-10-24 复核基线 @bc1b8c3 | Windows 默认检查打印 112 条 PASS 后在持久化效率检查超时；单独提权复核打印 111 条 PASS 后同类超时，调用点分别为 :57、:42，均为 `UntilAsync:135` 的 TaskCanceledException。Host src／tests 相对 f17 未变；见[本地验证收据](../research/model-loading-2026-10-23/remaining-tests-20261023-181549.final.json)及其中提权日志索引。2026-10-24 黄金听感 UI 轮两次 Windows 本地检查分别在 :38、:57 等待落盘超过 4 秒；第二次复制测试输出并将进程 TEMP/TMP 指向工作区后，原失败项通过而下一项超时。见[本地默认日志](../artifacts/hearing-result-ui-20261024/host-tests-restored.log)和[隔离日志](../artifacts/hearing-result-ui-20261024/host-isolated-rerun/host-tests-temp-isolated.log)，本地产物不随 clone 提供 | 恢复后台持久化效率验收；未测性能收益 | 此前提权未解，本轮沙箱／临时目录等环境约束待复核，根因未知；`DeviceStateStore.WriteLoopAsync` 吞 IO／权限异常后结束写入仅为待排查线索，日志未记录实际写入异常，不能据此定因；不以延长超时替代原因验证 | 待处理（未闭合；当前 UI 轮只登记，不改生产持久化逻辑） | 2026-10-23 模型加载提速；2026-10-24 黄金听感 UI（跨方向复核） |
| BUG-07 | `src/HeyMelody.Client` 写队列，`tests/HeyMelody.Client.Tests/WriteQueueTests.cs` :136／`Check`:150 @f17ab2b；2026-10-24 复核基线 @bc1b8c3 | Windows Client.Tests 打印 155 条 PASS 后在 `The unconfirmed save discards the click` 断言退出（InvalidOperationException），并出现 CLR 0xe0434352 错误框；Client src／tests 相对 f17 未变，根因未闭合。见[本地验证收据](../research/model-loading-2026-10-23/remaining-tests-20261023-181549.final.json)。2026-10-24 黄金听感 UI 轮再次打印 155 条 PASS 后在同一位置和消息退出（-532462766），Client src／tests 无本轮改动；见[本地 Client 日志](../artifacts/hearing-result-ui-20261024/HeyMelody.Client.Tests.log)，本地产物不随 clone 提供 | 恢复默认写队列验收；未测性能收益 | 合取断言要求未确认保存后仅有一条设备操作且 SelectedPreset.Key 回到 0；日志未分别打印两值，不能确定额外操作还是视图回退观测异常。需区分未确认写入后的丢弃与异步观测顺序，不判本轮回归或已证环境，不放宽确认、禁止重放等写入边界 | 待处理（未闭合；当前 UI 轮仅登记，不修无关队列实现） | 2026-10-23 模型加载提速；2026-10-24 黄金听感 UI（跨方向复核） |
| BUG-08 | `src/HeyMelody.App/EncoFree4ModelView.cs` `LoadAsync` worker 返回与 UI 接管、`QueueRendererDisposal` @f17ab2b | 静态发现既有所有权交接窄窗：worker 判断当前代次后返回 created，并先释放生命周期门；UI 尚未接管前若已开始新代，新 worker 可先取得门创建 Engine，旧 local renderer 要等旧 UI 续体才排队退役。f17 同样存在此结构，未作实测复现 | 收紧取消／切换时单 Engine 的所有权边界；收益未测 | 需构造返回至 UI 接管之间的可控交错，协调发布／退役与门持有顺序，避免双重释放或死锁；不能称本轮新增回归 | 待处理（仅静态登记；本轮不实现） | 2026-10-23 模型加载提速（静态审计发现） |
| BUG-09 | SmoothScroll.Avalonia 12.0.0.14 普通滚轮惯性中的直接 Offset 定位；`SmoothScrollingTests.cs` 的 `WheelAndProgrammaticAsync` | Windows 定向检查有一次在设置 Offset=900 后执行 `RunJobs` 的即时断言失败；见[失败日志](../artifacts/scroll-input-20261024/smooth-final.log)。该样本未记录实际回写值和最终位置。此前基线 29 项、首轮 45 项及加入数值诊断后的最终 45 项均通过，未放宽原断言；见[复跑日志](../artifacts/scroll-input-20261024/smooth-diagnostic.log) | 确认合成回写与显式定位的顺序，避免旧惯性回执影响新位置；收益未测 | 源码线索：直接定位请求延迟到组合批次提交，已排队的惯性 `RequestId=0` 通知可绕过 presenter 的非零请求过滤并清空待处理 ID。需采集失败时的实际值、请求顺序及最终回执，不能仅据静态窗口判定完整根因；本轮未添加私有 API 修补 | 待调查（保留严格断言，仅补充失败数值诊断） | 2026-10-24 触控滚动修复（旁支检查） |
| BUG-10 | `src/HeyMelody.Host/BackendPipeServer.cs` 与 `src/HeyMelody.Client/ClientBridge.cs` 的 `DisposeAsync` @1840a68 | 两处均为“已释放即返回”的守卫，第二个并发调用方在清理完成前就返回；`ClientBridge` 的 `_disposed` 先检查后设置，存在竞态（静态分析） | 让并发释放等待同一次清理完成 | 需确认现有调用方是否依赖立即返回 | 待处理 | 2026-10-25 日志与退出审计（顺带发现） |
| BUG-11 | `src/HeyMelody.Client/ClientBridge.cs` `ReadMessagesAsync` 结束分支 @1840a68 | 本窗口已投同意票、另一窗口否决后，若 Host 因队列溢出或写超时断开本管道，客户端可能把断管当作终止停止而关闭窗口，后台仍在运行（静态分析，触发概率未确认） | 避免误关闭窗口 | 需要区分“断管”与“收到 stopping”，补竞态检查 | 待处理 | 2026-10-25 日志与退出审计（顺带发现） |
| BUG-12 | `src/HeyMelody.Client/ClientBridge.cs` `EnsureConnectionAsync` @1840a68 | `CommitStopping` 取消 `_stopConnecting` 后，等待连接门的调用方收到 `OperationCanceledException`，此前为 `IOException`，调用方可能误判为用户取消（静态分析，影响未确认） | 统一异常语义 | 需逐一核对调用方处理 | 待处理 | 2026-10-25 日志与退出审计（顺带发现） |
| BUG-13 | `tests/HeyMelody.App.Tests/ArtworkPersistenceTests.cs` 设置卡片检查 @1840a68 | `--artwork-persistence-tests` 断言设置对话框恰有 8 个带图标的 `FASettingsExpanderItem`；e75453c 增加 Basic Render 卡片后顶层已有 9 张，且该查询会计入展开器内部条目，基线与本轮均在“Every setting, including the 3D frame rate, is an icon card.”失败（Linux 无头复现） | 恢复该模式其余检查的覆盖 | 断言应改为只统计顶层卡片并按名称核对，需同步确认诊断日志区的新布局 | 待处理 | 2026-10-25 日志与退出审计（顺带发现） |

2026-10-26 合并后的 Windows 本地复核仍复现 BUG-06／BUG-07：完整 Host 检查先后在 `HostEfficiencyTests:42`、`:57` 等待落盘超时，效率隔离入口通过，随后完整私有诊断模式 132 项通过，BUG-06 根因仍未闭合；Client 失败消息现分别记录 `Actions.Count=1`、`SelectedPreset.Key=2`，将 BUG-07 范围缩小到显示恢复的观测时序。两处相关产品实现与合并前 `2494901` 相同，保留原业务断言。完整结果和失败日志索引见[本地验证档案](../artifacts/local-windows-validation-20261026/README.md)，不随 clone 提供。

## macOS 后续资源验证（MAC）

以下条目以移植前 `0cabb0b` 为基线，涉及本轮新增文件，按符号定位。

| 编号 | 位置 | 现象与依据 | 预估收益 | 风险与依赖 | 状态 | 登记来源 |
| --- | --- | --- | --- | --- | --- | --- |
| MAC-01 | `src/HeyMelody.MacOS/MacBluetoothPlatform.cs`：`_events` / `Publish`（本轮新增） | 原生事件先进入无界托管队列，广播尚无按设备合并策略（静态分析）；本轮没有蓝牙实测，未测量真实积压或能耗 | 后续可按广播流量限制瞬时内存并减少重复处理；收益未测量 | 授权、断连、扫描代次和休眠事件必须可靠有序，不能简单丢弃最旧事件；需真机广播负载与取消验证 | 待处理 | 2026-10-25 macOS 移植（资源方向顺带发现） |
