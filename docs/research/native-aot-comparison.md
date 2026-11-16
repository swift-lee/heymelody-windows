# Windows x64 Native AOT 构建与性能对比

本报告记录 0.9.2 试验分支的 Native AOT 适配、对比方法和验证边界。目标是得到可校验的 AOT 发布包，判断它对启动、常驻占用和界面负载的影响。性能结论来自相同源码和资源的发布产物；入口级发布、离线验收和整个包的完成状态分别记录。

App、Host 已完成 AOT 适配、混合包构建和完整默认性能矩阵，两组最终目录及 ZIP 均通过验包。19 个 .NET 离线入口和 3 个 Python 组全部成功，最终发布程序另通过诊断开启验收；各次验证的源码边界见第五节。

本机无耳机合成负载下，四种启动路径的外部 ready 中位数缩短 43.0%–70.0%，ZIP 减小 17.2%；Host 进程组和集成进程组的 Private Working Set 中位数分别减少 20.4%、15.7%。模型展示、模型滚动的 CPU 中位数分别增加 24.8%、10.7%，回调间隔接近，不能判断所有负载均受益。模型加载总耗时中位数缩短约 2.2%–4.3%，收益远小于启动。本轮保留默认 R2R 发布方式，AOT 作为显式实验选项；工具链安全告警、第三方反射路径和真机验证边界见第六节。

## 一、实验范围与对比对象

本轮只覆盖 Windows x64。App 和 Host 进入 AOT；设备管理器、贴合度检测和黄金听感入口继续使用现有 ReadyToRun 发布方式。Host 启动的耳机 worker 与 Host 使用同一个可执行文件，因此也属于本轮 AOT 范围。macOS、Windows ARM64 和 x86 没有因这次实验获得新的 AOT 支持或硬件验证。

| 组件 | 基线包 | AOT 实验包 |
|---|---|---|
| HeyMelody 主窗口 | 自包含 ReadyToRun，保留 JIT | 自包含 Native AOT |
| HeyMelody.Host 托盘与 worker | 自包含 ReadyToRun，保留 JIT | 自包含 Native AOT |
| DeviceManager、FitTest、HearingSetup | ReadyToRun | ReadyToRun |
| Filament 原生运行时、模型与预装资源 | 固定输入与摘要 | 使用相同输入与摘要 |

“JIT 基线”是项目原有的 ReadyToRun 加 JIT 形态，已在发布时预编译部分托管代码，不等于裸 IL 发布。工具拒绝通过 `DOTNET_ReadyToRun=0` 或 `COMPlus_ReadyToRun=0` 关闭基线的 ReadyToRun。

本轮使用 .NET SDK 10.0.401，正式还原记录中的 ILCompiler 为 10.0.12。机器默认 `dotnet` 曾选择 SDK 11.0.100，复现时必须核对实际路径和版本。实验保持应用版本 0.9.2，两个变体使用相同产品源码；打包脚本修复和重新封包另记输入快照，保留早期失败产物。

## 二、为 AOT 完成的实现调整

### JSON、RPC 与本地文件

原实现为部分 RPC 类型提供 source-generated context，其余 Host 私有对象、匿名诊断和缓存仍依赖反射。AOT 适配将这些路径收敛到静态元数据。

产品 RPC、worker 标准输入输出协议现在通过 `JsonTypeInfo<T>` 重载序列化，补全请求、消息、集合根和 Host 私有 diagnostics 类型。已有 JIT 调用的兼容 fallback 由 `JsonSerializer.IsReflectionEnabledByDefault` 特性开关控制；AOT 关闭该开关时只保留生成元数据。对这段可被裁掉的兼容代码只作窄范围说明，没有全局关闭裁剪或 AOT 分析。

白名单、设备外观、位置记录、设备展示缓存、校准历史、资源偏好、预装索引和资源包标记均有对应 context。迁移保留原字段名、大小写、null、缩进和版本迁移规则。厂商请求使用独立的紧凑序列化配置，因为请求体的确切字节参与签名；将其改成便于阅读的缩进 JSON 也会改变协议输入。

本轮验收发现，当前工具链的 sourcegen fast writer 把可选 `byte[] ScanData=null` 写成空 base64，读回空数组后触发完整性校验。校准历史 context 改用 Metadata 模式保留 null，仍使用静态元数据，保留原输入校验。

### 诊断与界面属性

匿名诊断载荷改为显式 JSON 字段；稳定 DTO 使用独立 Metadata context。协议与诊断的命名配置分开：RPC 保持原 camelCase，日志内原先由默认 serializer 生成的 DTO 字段保持原默认命名。字节数据保留 base64，enum 保留数值，显式 JSON 在入队前复制，避免接收缓冲区或后续对象修改污染既有记录。

字段、getter、异常格式化或序列化失败被收集为诊断错误，不改变设备操作。关闭日志时，`DiagnosticData.From` 不额外序列化 DTO。

FeatureControlView 使用明确 getter、setter 和属性类型的 compiled binding，保留绑定方向、数值格式和选项来源。MainViewModel 展示通知使用 124 项显式 getter 表；JIT 验收与公开实例属性集合比较，检查遗漏、多余项和重复项。

### Windows 音频与原生渲染

Host 的 Windows 传递依赖不包含实验 `HeyMelody.Audio` 项目，因此只迁移 Host 内的 WASAPI interop。原有 `[ComImport]` 和运行时 RCW 改为 `GeneratedComInterface`、显式 `CoCreateInstance` 与 `UniqueComInterfaceMarshaller`。GUID、vtable 顺序、HRESULT、UTF-16 和 PROPVARIANT 布局保留；所有返回接口使用 unique wrapper，在原先拥有它们的 STA 上确定释放。

合成 IRenderClient vtable 验收覆盖 IUnknown 转换、方法槽位、参数和引用归零。它不打开音频端点或播放声音，不能证明真实试听、系统音频路由和校准效果。

Filament 继续通过明确导出名加载原生模块。加载原生 DLL 不等于生成托管代码；需要核对的是原生 ABI、callback、marshal、模块与 probe/manifest 的身份，以及模型输入是否一致。模型加载基准不再依赖 Native AOT 中不可用的托管 assembly 文件位置，而以实际发布产物和输入摘要识别运行对象。

## 三、发布与公平性约束

`Build-Client.ps1` 新增 `CompilationMode`，默认仍为 `ReadyToRun`；`AotExperimental` 只接受 win-x64，并使用独立的输出根和实验清单。每个组件使用隔离的 publish/obj 输出，避免同一路径的中间文件或硬链接把一个变体的还原、裁剪、编译结果带入另一个变体。

两种包都使用 Release、自包含发布，保持相同资源、原生运行时、辅助入口和符号排除规则。AOT 组件启用 `PublishAot`、`PublishTrimmed` 并关闭 `PublishReadyToRun`；其余入口走 R2R。包级验收按清单校验组件路径、架构、文件、摘要、原生依赖和许可。

混合包先组成三个 R2R 辅助入口所需的 GUI 目录，再合并 AOT App；重名原生库或内容文件必须 SHA256 相同。GUI 保留辅助入口所需的 CoreCLR 和托管依赖，AOT `backend` 则禁止混入 CoreCLR。清单记录五个组件的编译方式、SDK、runtime、ILCompiler 与 EXE 摘要；缺少这些扩展字段的旧清单仍按原 R2R 规则验证。性能测量阶段交付目录和 ZIP，安装器当时拒绝实验模式；报告完成后增加的两种体验安装器见第九节。

首轮完整封包曾在原生编译完成后因许可文件定位失败而终止，原目录和日志保留。实际 ILCompiler 包提供 MIT SPDX 声明与第三方通知，没有独立 `LICENSE.TXT`。修复后仅在两个包的 MIT 声明、版本、来源仓库及精确提交均一致时，才从实际 .NET runtime 包复制未修改的 MIT 正文，同时保留 compiler 原始 nuspec、版权和第三方通知，逐文件记录真实来源与摘要。不同许可证或来源提交会失败。修复后在新目录重新完整构建两组，没有把失败耗时或入口级 probe 当成最终成功耗时。

采样前记录 manifest、App/Host SHA256 和文件清单，确认对应已校验 build。脚本检查模型、原生库、窗口尺寸、DPI、实际 backend 和 harness revision；签名不同则停止汇总，避免把输入或环境差异归给 AOT。

比较使用独立的本地 fixture、IPC 端点和临时数据目录。集成场景经过真实的 ClientBridge、Host 和 worker 进程边界，但耳机观测是固定模拟输入。它不扫描真实蓝牙、不请求在线资源、不读取用户偏好，也不提交设备设置。正式性能采样关闭诊断；另行验证打开诊断后的兼容性，避免两个变体承担不同的日志工作。

模拟 worker 保留当前 `PeerWorker` 的退出管理：Host 停止时会回收 worker，实测退出码为 `-1`。验收记录该真实值，要求 Host 本体退出码为 0、worker 在停止前存活、Host 停止后消失，且 controller 没有强制清理；不把 Host 管理的 worker 终止写成 worker 优雅退出。

## 四、测量方法与指标含义

统一驱动为 [`tools/Measure-AotComparison.ps1`](../../tools/Measure-AotComparison.ps1)。主矩阵每组先执行一次不计入结果的 `ui-static` rehearsal，Host 阶段观察另有每组一次 rehearsal，模型加载另有每组、每模型一个 rehearsal 进程。正式运行按轮次交换先后顺序，降低顺序、缓存和机器温度漂移的影响。单次回执、进程身份和原始采样均保留。

脚本默认设置为启动 20 轮；稳定负载每场景 5 轮，每轮测量 30 秒，普通场景预热 5 秒；模型加载使用每组、每模型 10 个进程样本，每个进程区分首次与进程内后续 5 次加载。正式结果必须记录实际调用参数，不能在缩短试跑后仍引用默认样本量。Host 空闲在外部采样前至少等待 17 秒，并检查记录的 Eco 状态。

启动分为四种路径：单独主窗口、单独 Host、主窗口启动一个尚未运行的 Host、主窗口连接已 ready 的 Host。外部计时包含进程启动至 ready 回执被观察到的时间；进程内部计时记录应用定义的 ready 点。两种指标的起点和观察开销不同，应并列报告，不用其中一个替代另一个。

主矩阵中的 Host ready 包含托盘、IPC 和模拟 worker 控制快照全部就绪。另由 `Measure-AotHostStartup.ps1` 在独立运行中作为 `observer` 订阅真实隔离 IPC，使用同一外部单调时钟记录首次 subscription baseline，以及首次 `ControlReady=true` 的快照。前者表示托盘初始化调用完成且 IPC 接受订阅，不证明 Shell 已画出图标；后者表示 Host 已接收 worker 初始化结果并发布控制快照。若首次 baseline 已 ready，两个观察时点相同并记录来源，不用差值推断 worker 的独占初始化耗时。该 observer 不计作 GUI，不发送设置请求；原始阶段数据与主矩阵分开保留。

这些样本称为“新进程启动”。没有清空 Windows 文件缓存或逐轮重启机器，不能称为物理冷启动；时钟之外的发布文件摘要核对也会预热文件缓存。Host 已 ready 的路径与从零启动路径分别统计，后者承担额外的进程创建、IPC 和初始化。

稳定负载包含静态空闲、静态滚动、模型展示、模型加滚动、集成静态界面和 Host 空闲。App 使用同一固定场景和模型帧率策略；Host 场景包含模拟 worker，从而可以报告后台进程组总占用，而不是仅挑一个较小的父进程。

| 指标 | 来源与解释 |
|---|---|
| ready 时间 | 外部进程计时与应用内部 ready 回执，分别统计 |
| CPU | 进程 CPU 时间增量除以实际首末采样时长，按单逻辑核心 100% 口径报告，可超过 100% |
| 内存 | 每进程 private working set、working set、private bytes，以及进程组 private working set 合计 |
| 分配与 GC | primary report 进程的托管累计分配增量和各代 GC 次数，不代表子进程总量或原生/GPU 分配 |
| 界面间隔 | UI callback 与 bitmap-copy 间隔的中位数、p95 |
| 模型加载 | 每模型、backend、首次/后续加载的分段时间与总时间 |
| 发布大小 | 组件文件、完整目录和归档体积，并保留文件摘要 |

外部采样在每次进程查询后等待 500 ms，实际间隔还包含 CIM 查询时间，观测峰值可能漏过短暂峰值。总 working set 可能重复包含共享页，因此同时报告进程组 private working set 和各进程数据；托管分配变化不能直接等同于整个进程占用变化。进程组合的内存先对同一时点的各进程求和，再计算中位数和观测峰值，不把各进程在不同时点的峰值相加。

内部托管分配/GC 与外部进程 CPU/内存均按 30 秒负载设计，使用独立采样窗口。Host 内部在 5 秒预热并进入 Eco 后开始，外部在观察 ready 后至少等待 17 秒；集成场景内外各预热 5 秒，但外部还有回执观察与进程身份查询开销。窗口没有握手同步，Host 内部记录结束后继续等待 stop，App 退出则会结束外部循环。各自按实际首末采样区间解释，不将它们当成严格同一时段的联合观测；分配速率使用内部实际时长，不固定除以 30，也不借用外部时长。

UI callback 和 bitmap-copy 间隔不是 DWM 实际显示 FPS。托管调度、原生渲染、驱动、复制与桌面合成属于不同阶段；某阶段变快不保证显示帧率变高。

分布以中位数和 nearest-rank p95 汇总，百分比取同场景 AOT 中位数相对基线的变化。稳态典型内存是各轮中位数的中位数，每轮等权；观测峰值另取全部同时点样本的最大值。UI 与 bitmap 间隔先在每轮内汇总，再跨轮汇总；各轮 p95 的中位数不等于合并所有回调后的 p95。n=5 或 n=10 时 nearest-rank p95 为最大值，n=20 时为第 19 个有序值。小样本 p95 较粗，不能只保留最好结果；缺失、超时或身份不符的样本判为不可用，保留原因，不能记为零耗时。基线为零时不计算相对变化。

模型加载另测 free4 与 dynaudio，固定 Vulkan backend 和输入摘要。每组、每模型 cold n=10，hot n=50；后者来自 10 个进程各 5 次重复，不是 50 个独立进程。首次/后续指测试序列中的缓存状态，没有清理操作系统缓存。总耗时采用记录的 `TotalMilliseconds`，不把各阶段中位数相加。原生渲染占主导的区间是否受益，需要实际数据判断。

## 五、已经完成的离线验证

完整离线运行使用 Windows profile、0.9.2、win-x64，22 个组全部执行并成功退出，没有超时，完整耗时 473.516 秒。44 份 stdout/stderr 已逐个重新计算 SHA256，与验证清单全部匹配。这次 profile 在 final-v2 的首帧诊断修正之前执行；该修正将一个模型计时 DTO 的诊断序列化改为已有的显式 context，没有宣称在 final-v2 上重跑了整套 profile。最终发布 EXE 另通过打开诊断的集成与 Vulkan 模型验收。

| .NET 验收入口 | 通过 | 跳过 |
|---|---:|---:|
| Platform | 2 | 6 |
| Diagnostics | 126 | 0 |
| Protocol | 77 | 0 |
| Resources | 41 | 0 |
| Devices | 21 | 0 |
| Control | 19 | 0 |
| Daily | 177 | 0 |
| Calibration | 55 | 0 |
| Client | 200 | 0 |
| Host | 160 | 0 |
| App | 347 | 0 |
| DeviceManager | 8 | 0 |
| Calibration.UI | 15 | 0 |
| Rendering.Filament | 169 | 0 |
| ModelPreview | 180 | 0 |
| MacOS | 58 | 1 |
| InstallerSupport | 28 | 0 |
| Probe | 23 | 0 |
| Windows.OfflineChecks | 成功，未报告 case 数 | 0 |

除 Windows.OfflineChecks 外均对应 `HeyMelody.<名称>.Tests`。有计数的 .NET 入口通过 1,706 项；Python 的 `test_project_structure.py`、`test_ui_assets.py`、`test_verify_release.py` 分别通过 9、10、17 项。明确计数为 1,742 项通过、7 项跳过，另有 Windows.OfflineChecks 成功，未把其 PASS 摘要当成额外 case 数。

随后补充的 NuGet SHA512 等价形式与拒绝不同摘要的回归，使 `test_verify_release.py` 独立运行达到 19/19；这两项没有回填到上述已归档 profile 的 1,742 项中。

6 项 Platform 跳过针对 Unix IPC 路径、权限、符号链接和会话语义；1 项 MacOS 跳过是当前进程不是原生 macOS ARM64。它们没有被记为通过，也不代表那些平台已经在硬件上验证。Windows.OfflineChecks 验证 ABI、预装传输白名单、预取消和有界 SDP 解析等静态/模拟规则，没有进行真实蓝牙调用。

离线日志仅出现一次测试 fixture 未使用事件的 CS0067，没有编译 error code 或 FAIL/Unhandled exception/Build FAILED 标记。这与正式 Native AOT 发布的分析警告是两份证据，不能用离线测试“无 error”掩盖发布时的兼容性或安全告警。

## 六、保留的兼容性与安全边界

早期 Host probe 展开分析后，`Avalonia.Controls.DataGrid 12.1.0` 的 `TypeHelper.GetPropertyOrIndexer(Type,String,Object[]&)` 有 IL2070 四条（反射成员注解）、IL3050 两条（`MakeGenericType`）。正式 App、Host 日志分别以 IL2104、IL3053 汇总，该批共四条 summary；不能与六条细项相加，也不能写成最终全包仅六条警告。业务源码没有 DataGrid 声明，依赖通过 FluentAvalonia 进入图中；警告未批量抑制，第三方反射路径的任意输入尚未验证。

正式 AOT restore 另出现 NU1903：`Microsoft.DotNet.ILCompiler 10.0.12` 落入 [微软 CVE-2026-111497 / GHSA-p3jp-9v83-76w9 公告](https://github.com/dotnet/runtime/security/advisories/GHSA-p3jp-9v83-76w9) 的受影响范围。Brotli 解码器越界写影响 10.0 分支 `>=10.0.0, <10.0.14`，10.0.14 修复。最终 AOT 日志的 NU1903 重复 42 次，规范化后的唯一内容为 1 项；DataGrid 的 IL2104、IL3053 各出现 2 次，各为 1 项唯一内容。没有以抑制告警完成构建。

限定 `src` 的只读搜索未发现直接使用 BrotliStream、BrotliDecoder 或显式启用 Brotli/All 自动解压；VendorResourceClient 明确只启用 GZip 与 Deflate。这个结果说明已审查的产品配置边界，不等于不存在其他可达路径，更不能据未触发来判断没有风险。Native AOT 与自包含发布携带自身运行时内容，后续安全修复必须更新受影响工具链/运行时输入并重新构建、验收发布包。

为保持本轮已批准的相同输入，对比继续锁定 SDK 10.0.401，不在测量中途更换版本，也不屏蔽 NU1903。这是实验限制，不是安全修复。安全升级应作为后续独立任务：确认修复后的 SDK/运行时与 ILCompiler 解析版本，重建两种变体，重跑包级验证与相关回归，并将新一轮性能结果和本轮分开记录。

正式 40 份模型进程 stdout 的每次 cold/hot 初始化均记录同一实现：`Vulkan`、`Dozen Mesa 26.3.0-devel (git-e3fc7749c6)`、`Microsoft Direct3D12 (NVIDIA GeForce RTX 2060)`，共 240 组记录，无其他设备或实现。性能结论限定于此 Dozen 环境，不能外推为厂商原生 Vulkan 驱动或其他 GPU 的结果。离线清单的 `nativeGpuVerified=false` 与后续原生模型验收分别记账。

Computer Use 管道在本轮不可用，因此没有把进程成功回执或应用内部帧信号当成交互式目视检查，也没有提供经过该渠道核验的界面截图结论。自动原生窗口烟测、内部帧回执、离线布局测试与人工观察分别记账。

最终 AOT 诊断短测的集成和模型两场景均成功，三份 JSONL 的 `serializationError` 为 0，均记录 started、ready、loggingStopped。Host 收尾还记录 1 条 `Runtime/task.unobserved` Error：`StreamWriter.Dispose` Flush 已关闭 pipe，产生 `ObjectDisposedException`。只读核对表明，`Client.RunAsync` 的任务丢弃、`finally` 关闭 pipe 与 writer 随后释放的顺序在基线 `beb45f4` 已存在，本轮未改变；已登记 [BUG-16](../optimization-backlog.md)。该次 Host Exit0、worker 被 Host 回收、没有 controller 强杀或遗留进程。序列化验收成功与这项运行时关闭异常分别记录，不能将日志描述为完全无错误。

本轮没有操作真实耳机，没有验证真实音频播放、设备回读、固件组合、校准听感或蓝牙重连效果。协议离线验收、生成 COM ABI 验收和进程间模拟只能证明对应测试条件下的行为，不扩大为所有受支持型号、Windows ARM64 或整个硬件生态的结论。

## 七、复现入口与证据组织

普通开发与环境要求见[开发指南](../development.md)，渲染边界见[模型渲染](../model-rendering.md)，包级规则见[打包说明](../../packaging/README.md)。准备好指定 .NET SDK、Windows C++ 工具链、可解析的 `vswhere.exe`、相同 Filament runtime 和依赖源后，可分别生成两个独立输出根：

```powershell
$env:DOTNET_ROOT = (Resolve-Path .\artifacts\tools\dotnet-10.0.401).Path
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet --version
$native = (Get-Content .\artifacts\filament-prepared\win-x64\preparation-receipt.json -Raw | ConvertFrom-Json).RuntimeRoot
pwsh.exe -NoProfile -File .\Build-Client.ps1 -Version 0.9.2 -Architectures x64 `
  -CompilationMode ReadyToRun -OutputRoot .\release\jit-baseline -FilamentRuntimeRoot $native
pwsh.exe -NoProfile -File .\Build-Client.ps1 -Version 0.9.2 -Architectures x64 `
  -CompilationMode AotExperimental -OutputRoot .\release\aot-experimental -FilamentRuntimeRoot $native
```

确认两包完成全部验收后，使用新的空目录启动矩阵。下例展示脚本默认样本量；正式回执应记录实际执行值：

```powershell
pwsh.exe -NoProfile -File .\tools\Measure-AotComparison.ps1 `
  -BaselineDirectory .\release\jit-baseline\HeyMelody_Windows_0.9.2 `
  -AotDirectory .\release\aot-experimental\HeyMelody_Windows_0.9.2 `
  -OutputRoot .\artifacts\aot-comparison\new-run
pwsh.exe -NoProfile -File .\tools\Measure-AotComparisonSummary.ps1 `
  -ResultDirectory .\artifacts\aot-comparison\new-run `
  -OutputDirectory .\artifacts\aot-comparison\new-analysis
```

复现使用新输出目录，保留失败回执。补充汇总使用 PowerShell 7.5+，从已有回执复算进程组统计，输出 `analysis.json`、`run-metrics.csv` 和 `summary.csv`。源码输入清单逐文件记录路径、bytes 与 SHA256，封包后再次核对并记录经过授权的变更。缺少私有档案不影响普通构建，也不能据公开摘要补写历史证据。

本机证据归档于 `research/native-aot/2026-11-16-implementation/`；`offline-validation-evidence.json` 保存 22 组摘要和 44 日志摘要复核，并引用原始输出。公开文档不复制完整流水或个人设备数据。

## 八、正式产物与性能结果

### 最终构建、包体积与身份

最终产物位于 `release/native-aot-comparison/2026-11-16/final-v2/` 的 `r2r`、`aot` 子目录，两包目录名均为 `HeyMelody_Windows_0.9.2`，归档名均为 `HeyMelody_Windows_0.9.2_x64.zip`。两套验包器均成功，清单、许可、ABI 和原生依赖检查通过。测量使用的客户端目录及 ZIP 在后续安装器封装中保持不变；安装器的新增支持和验证范围另见第九节。

| 指标 | R2R + JIT | AOT 混合包 | 相对变化 |
|---|---:|---:|---:|
| 完整目录 bytes | 507,333,917 | 400,435,959 | −21.07% |
| 目录文件数 | 1,310 | 1,092 | −218 个 |
| ZIP bytes | 247,599,049 | 205,016,449 | −17.20% |
| 发布至 ZIP 耗时（秒） | 482.033 | 689.979 | +43.14% |

耗时来自一次顺序执行的完整 `Build-Client` 成功流程，两组均使用 `SkipTests`，共用此前完成的 473.516 秒离线 profile 和已准备的 Filament runtime；不是多次干净编译的分布。原生准备回执没有起止或耗时字段，因此该部分耗时标为未知，没有推算或填零。本轮数据说明这次发布工作流更慢，不能单独归因于编译器本体。早期失败封包和旧 `final/` 产物保留，不进入本表。

| 最终 ZIP | SHA256 |
|---|---|
| R2R | `9119c9a8b1208975ed526533042468fee88eef493da06b60b3b1a18688fe85fa` |
| AOT 混合包 | `c4e0efc0d8f81d818af96b6e4d90bf8f2c155054c8bda21d5bdf6bc87f1addd7` |

单个 EXE 不适合直接当作总体积收益：R2R 的 EXE 是 apphost，代码和运行时位于其他文件；AOT EXE 自身包含原生编译内容。

| 组件 | R2R EXE bytes | R2R 入口 DLL bytes | AOT 混合包 EXE bytes | 混合包入口 DLL bytes |
|---|---:|---:|---:|---:|
| App | 202,240 | 22,450,176 | 54,976,512 | 无 |
| Host／worker | 202,240 | 6,991,872 | 45,122,560 | 无 |
| DeviceManager | 202,240 | 307,200 | 202,240 | 307,200 |
| FitTest | 202,240 | 98,304 | 202,240 | 98,304 |
| HearingSetup | 202,240 | 98,304 | 202,240 | 98,304 |

App、Host 的原生编译日志、清单、PE 结构与运行时动态代码标志共同证明 AOT 身份。仅凭 EXE 没有 CLR header 不足以区分 AOT 与 R2R apphost；本轮同时核对后者的托管入口 DLL、CoreCLR 和运行时标志。混合 GUI 保留三个辅助入口的 CoreCLR，AOT backend 没有混入 CoreCLR。

final-v2 发布前清单列出 1,303 项代码、构建及验证输入，合计 85,785,880 bytes，基于 `beb45f4e42f951d2c4e1947941bc7b2d0f0dc75f` 加本轮工作区修改。聚合规则为：规范化相对路径按 Ordinal 排序，每项以 `path + TAB + 十进制 bytes + TAB + 小写文件 SHA256 + LF` 拼接，末项也含 LF，再对 UTF-8 无 BOM 字节计算 SHA256。

发布后和正式测量后，PowerShell 与独立 Python 审计均逐文件核对，重现聚合 SHA256 `0a35688e3c4a881f84cdb8298ff00a681196431585deeb17d48213143f60b4ee`，没有遗漏或变化。公开报告、开发说明及待办文档不在此清单内；测量工具另按执行版本归档。这项结论限定于列明输入和审计时间，没有宣称整个工作区均未变化。

最终构建依据为 `packaging-evidence-v2.json`、`publish-final-v2-timings.json`、`source-inputs-final-v2-after-publish-check.json` 和 `source-inputs-final-v2-aggregate-proof.json`，均位于第七节的本机证据目录。

测量后另存 `source-inputs-final-v2-after-measurement-check.json`、`source-inputs-final-v2-after-measurement-aggregate-proof.json`，保留发布后审计原件。最终两份 ZIP 在测量后再次计算 SHA256，与本表完全一致。

### 正式性能矩阵

正式矩阵于 2026-11-16 03:49:04.786 至 04:36:39.444（Asia/Hong_Kong）运行，完整默认调用成功，耗时约 47 分 35 秒。执行前归档三份测量脚本，执行后摘要一致。临时防休眠请求在 finally 中撤销，没有修改持久电源计划。

| 环境 | 本轮记录 |
|---|---|
| OS | Windows 11 Pro for Workstations，10.0.26300，x64 |
| CPU | Ryzen 9 5900X，12 核／24 逻辑处理器 |
| 内存 | OS 可见 63.91 GiB |
| GPU／驱动 | NVIDIA GeForce RTX 2060，32.0.16.2124；另安装 MuMu Virtual Display Adapter |
| 桌面设置 | 2560×1440，144 Hz；不等于本轮测得的呈现帧率 |
| 电源计划 | Ultimate Performance，前后相同 |
| 产品工具链 | .NET SDK 10.0.401，runtime／ILCompiler 10.0.12 |
| 测量 Shell | PowerShell 7.7.0 |

主窗口固定 1280×900 logical units、不可调整大小，172 份 App 收据的 `renderScaling=1`。独立模型输出固定 480×640，44 份收据的 `renderScaling=1`。主稳态模型为同一丹拿嵌入资源，`ModelFrameRate.Unlimited`；没有另设 30/60 帧上限。两组 backend 均为 Vulkan，实际实现及设备见第六节。

缩减样本流程只作工具验收，不进入正式结果。正式主矩阵有 222 份成功 controller 回执，其中 2 份 rehearsal 不计入结果；四种启动路径每组各 20 次，六种稳态负载每组各 5 次，共 220 次正式运行。另有 42 次 Host 阶段运行（40 次正式）、44 个模型进程（40 个正式），模型正式加载记录共 240 条，另有 24 条 rehearsal 加载记录。

主矩阵 1,772 个外部样本的角色与 Private Working Set 完整，没有缺失角色、身份摘要或路径问题。分布表中的每个稳态 CPU 范围均为 5 个有效轮次。百分比均使用未四舍五入的中位数计算，正数表示指标增大，没有检验统计显著性。

正式稳态每轮实际得到 28–30 个外部样本，首末跨度 28.48394–29.97687 秒；内部为 59–61 个样本，首末跨度 29.99835–30.50575 秒。10 轮 Host 空闲的 608 个内部样本全部为 Eco。与第四节所述一致，这些是各自的采样窗口，未将配置 30 秒等同于恰好 30 秒的首末间隔。

### 启动：外部观察与内部 ready

每格为“中位数 / p95”，单位 ms，各组各路径 n=20。外部时钟从进程创建前开始，内部 ready 使用程序自己的计时起点；不能相减来解释某一个独占阶段。

| 路径 | R2R 外部 | AOT 外部 | 外部变化 | R2R 内部 | AOT 内部 |
|---|---:|---:|---:|---:|---:|
| 主窗口独立启动 | 1191.9 / 1336.0 | 630.0 / 716.5 | −47.1% | 1068.8 / 1209.2 | 569.6 / 648.1 |
| Host 完整就绪 | 657.8 / 863.7 | 197.1 / 255.5 | −70.0% | 545.3 / 728.0 | 134.0 / 175.7 |
| App 启动尚未运行的 Host | 1587.9 / 2007.2 | 879.9 / 1116.4 | −44.6% | 1468.7 / 1862.9 | 811.6 / 1045.7 |
| App 连接已 ready 的 Host | 1539.0 / 1699.8 | 877.1 / 1014.6 | −43.0% | 1408.7 / 1582.2 | 811.6 / 951.2 |

Host 完整就绪包含模拟 worker 的控制快照；已 ready 的集成路径不包含预先启动 Host 的成本。独立 IPC observer cohort 的各组 n=20，仍以同一外部时钟观察，各格为中位数 / p95 ms：

| Host 观察点 | R2R | AOT | 变化 |
|---|---:|---:|---:|
| 首次 subscription baseline | 541.1 / 570.6 | 171.7 / 194.8 | −68.3% |
| worker 身份可读 | 546.9 / 570.6 | 190.8 / 213.2 | −65.1% |
| 首次 `ControlReady=true` | 663.0 / 687.7 | 212.6 / 235.3 | −67.9% |
| 完整 ready 文件可读 | 650.0 / 674.8 | 203.9 / 237.1 | −68.6% |

本次两组首次 baseline 已 ready 的计数均为 0。消息处理、轮询和文件观察的开销不同，例如控制快照观察可能晚于 ready 文件观察；不通过两个端点的差值推导 worker 的独占初始化耗时，也不把 observer 与主矩阵的 Host 样本混合。

### 稳态：CPU、内存与进程范围

每场景每组 n=5，设计采样时长 30 秒；CPU 按外部首末样本的实际时长计算。CPU 的 100% 表示一个逻辑核心，各格为中位数 / p95，n=5 的 p95 为最大轮值。前四场景为 App；集成和 Host 使用完整进程组。

| 场景／范围 | R2R CPU % | AOT CPU % | 中位数变化 |
|---|---:|---:|---:|
| 静态空闲／App | 1.99 / 20.11 | 1.52 / 1.66 | −23.3% |
| 静态滚动／App | 39.97 / 43.05 | 35.31 / 45.24 | −11.7% |
| 模型展示／App | 45.49 / 71.51 | 56.79 / 72.13 | +24.8% |
| 模型滚动／App | 99.50 / 106.66 | 110.20 / 112.40 | +10.7% |
| 集成静态／App + Host + worker | 2.40 / 2.74 | 1.74 / 1.95 | −27.6% |
| Host 空闲／Host + worker | 1.76 / 1.81 | 1.39 / 1.52 | −21.1% |

内存典型值采用每轮中位数再跨轮取中位数，单位 MiB（1,048,576 bytes），不是托管堆大小，也不包括 GPU 显存：

| 场景／范围 | R2R Private WS | AOT Private WS | 变化 | R2R Private Bytes | AOT Private Bytes | 变化 |
|---|---:|---:|---:|---:|---:|---:|
| 静态空闲／App | 87.89 | 81.67 | −7.1% | 181.75 | 173.98 | −4.3% |
| 静态滚动／App | 181.39 | 170.10 | −6.2% | 456.40 | 445.66 | −2.4% |
| 模型展示／App | 341.36 | 333.61 | −2.3% | 635.79 | 625.88 | −1.6% |
| 模型滚动／App | 459.00 | 444.34 | −3.2% | 908.76 | 896.56 | −1.3% |
| 集成静态／App + Host + worker | 115.51 | 97.39 | −15.7% | 212.57 | 192.39 | −9.5% |
| Host 空闲／Host + worker | 40.50 | 32.25 | −20.4% | 50.45 | 42.04 | −16.7% |

下表另列内存“各轮中位数的 p95 / 所有外部样本的观测峰值”，单位 MiB；n=5 时前者是最大轮中位数，仍不是后者。进程组峰值来自同一外部样本求和。

| 场景／范围 | R2R Private WS | AOT Private WS | R2R Private Bytes | AOT Private Bytes |
|---|---:|---:|---:|---:|
| 静态空闲／App | 99.32 / 152.19 | 81.74 / 93.38 | 193.68 / 310.16 | 174.66 / 186.25 |
| 静态滚动／App | 182.80 / 187.94 | 171.38 / 176.35 | 459.65 / 473.32 | 448.11 / 459.78 |
| 模型展示／App | 343.51 / 347.40 | 336.46 / 345.50 | 637.02 / 642.75 | 628.88 / 639.40 |
| 模型滚动／App | 482.50 / 489.98 | 455.51 / 466.66 | 912.25 / 924.06 | 898.39 / 912.11 |
| 集成静态／App + Host + worker | 116.69 / 120.98 | 98.06 / 99.25 | 213.04 / 217.47 | 193.04 / 194.75 |
| Host 空闲／Host + worker | 40.82 / 41.89 | 32.46 / 33.36 | 50.79 / 51.88 | 42.22 / 43.43 |

R2R 静态空闲第 1 轮外部 CPU 为 20.10744%，分配 16.6732 MiB、GC 为 1 / 1 / 0，其他四轮 CPU 为 1.76966%–2.32446%、分配约 0.43–0.45 MiB、GC 均为 0。该轮成功、身份匹配，29 个外部样本无缺失或倒退，记录有效，因此保留。原因未经跟踪定位，不据此推断 tiered JIT，也不仅凭较大的 p95 宣称 AOT 稳定性改善。

为便于核对进程归属，下面列出集成和 Host 场景中各角色的中位数（箭头为 R2R → AOT）；完整每角色、每进程组合的统计保存在[进程汇总 CSV](native-aot-comparison-processes.csv)：

| 场景／角色 | CPU % | Private WS MiB | Private Bytes MiB |
|---|---:|---:|---:|
| 集成／App | 2.045 → 1.570 | 71.56 → 62.00 | 158.31 → 147.41 |
| 集成／Host | 0.372 → 0.158 | 37.75 → 31.04 | 44.47 → 35.95 |
| 集成／worker | 0 → 0 | 6.05 → 4.33 | 9.84 → 9.04 |
| Host 空闲／Host | 1.762 → 1.390 | 34.28 → 28.14 | 40.41 → 33.22 |
| Host 空闲／worker | 0 → 0 | 6.21 → 4.31 | 10.04 → 9.00 |

各角色的中位数不能直接相加代替进程组的中位数；进程组先在同一外部样本求和。worker CPU 为 0 表示该采样精度下未观察到 CPU 时间增量，不证明绝对没有 CPU 活动。基准内部采样自身也消耗 CPU，本表只代表这些合成负载条件。

### 托管分配、GC 与渲染回调

下表为 primary 进程的内部采样：前五场景属于 App，Host 场景属于 Host，不包含 worker 的托管分配。分配单位 MiB，各格为中位数 / p95；GC 格为 Gen0 / Gen1 / Gen2 的轮次中位数。

| 场景 | R2R 分配 | AOT 分配 | 变化 | R2R GC | AOT GC |
|---|---:|---:|---:|---:|---:|
| 静态空闲 | 0.441 / 16.673 | 0.414 / 0.414 | −6.1% | 0 / 0 / 0 | 0 / 0 / 0 |
| 静态滚动 | 86.584 / 86.698 | 92.952 / 93.054 | +7.4% | 5 / 1 / 0 | 5 / 1 / 0 |
| 模型展示 | 31.307 / 31.582 | 31.549 / 32.558 | +0.8% | 2 / 1 / 0 | 2 / 1 / 0 |
| 模型滚动 | 89.443 / 90.760 | 97.824 / 98.170 | +9.4% | 6 / 1 / 0 | 6 / 1 / 0 |
| 集成静态 | 0.431 / 0.443 | 0.413 / 0.428 | −4.3% | 0 / 0 / 0 | 0 / 0 / 0 |
| Host 空闲 | 0.292 / 0.298 | 0.272 / 0.272 | −6.8% | 0 / 0 / 0 | 0 / 0 / 0 |

中位数相同不代表每轮相同：AOT 模型展示与模型滚动各代 GC 的轮次最大值分别达到 3 / 2 / 1、7 / 2 / 1，R2R 对应为 2 / 1 / 0、6 / 1 / 0。分配增加而 Private Bytes 减少可以同时发生，两者不能互相替代。

回调表的“典型间隔”是各轮 median 的中位数，“长间隔”是各轮 p95 的中位数，单位 ms；每格为典型 / 长间隔。静态空闲等没有连续回调的场景不填零间隔。

| 场景 | R2R UI callback | AOT UI callback | R2R bitmap-copy | AOT bitmap-copy |
|---|---:|---:|---:|---:|
| 静态滚动 | 6.9446 / 7.3041 | 6.9441 / 7.4035 | — | — |
| 模型展示 | 6.9420 / 7.6010 | 6.9431 / 7.5795 | 6.9432 / 7.6025 | 6.9442 / 7.5749 |
| 模型滚动 | 6.9433 / 7.3858 | 6.9443 / 7.3625 | 6.9451 / 7.4571 | 6.9452 / 7.4128 |

这些间隔接近，不能由此推导实际屏显 FPS 或保证流畅度提升。模型 CPU 的上升和静态滚动分配的上升均保留，不以启动改善替代这些结果。

为交代工作量，下表保留五轮内部实际时长和间隔记录数的范围；CPU 已按外部实际时间归一化，没有进一步按回调数归一化。间隔记录数也不是 DWM 呈现帧数。

| 场景／变体 | 内部记录时长（秒） | UI 间隔记录数 | bitmap 间隔记录数 |
|---|---:|---:|---:|
| 模型展示／R2R | 30.46150–30.49510 | 4180–4239 | 4199–4282 |
| 模型展示／AOT | 30.07164–30.48780 | 3953–4242 | 3916–4289 |
| 模型滚动／R2R | 30.04426–30.50233 | 4158–4284 | 4139–4333 |
| 模型滚动／AOT | 30.01820–30.50072 | 4239–4288 | 4222–4334 |

### 模型首次加载与热重载

每组、每模型首次加载 n=10；热重载 n=50，来自上述 10 个进程各 5 次。各格为中位数 / p95，单位 ms；p95 在 cold n=10 时为最大值。

| 模型／序列 | R2R Total | AOT Total | 变化 | R2R FirstFrame | AOT FirstFrame |
|---|---:|---:|---:|---:|---:|
| Free4 首次 | 1659.8 / 1701.9 | 1623.8 / 1633.7 | −2.2% | 71.5 / 76.1 | 71.6 / 84.2 |
| Free4 热重载 | 586.6 / 710.8 | 562.7 / 614.8 | −4.1% | 74.3 / 90.4 | 69.8 / 86.9 |
| 丹拿首次 | 1645.0 / 2000.8 | 1574.9 / 1631.0 | −4.3% | 70.0 / 82.6 | 67.9 / 82.9 |
| 丹拿热重载 | 586.1 / 711.4 | 572.6 / 661.1 | −2.3% | 75.6 / 99.2 | 72.8 / 87.5 |

这里的 FirstFrame 从原生 `LoadModel` 返回后开始，至首个尺寸匹配的有效模型帧复制到 bitmap、请求重绘并调用 `CompleteLoadTiming` 时结束，包含首帧等待与复制；不是进程创建到首帧，也不是 DWM 显示时延。Total 共享终点，attach 内首次加载从 attach 开始计，其后重载从该次加载开始计；两组同序列使用相同起点。

FirstFrame 的中位数变化依次为 +0.1%、−6.1%、−3.1%、−3.7%。Free4 首次首帧中位数基本相同，p95 从 76.1 增至 84.2 ms；不把 Total 的小幅下降写成所有首帧指标均改善。计时阶段存在重叠，阶段值与 Total 分别记录，不相加各阶段中位数。

抽查首个正式模型进程的四张原生帧，均为 480×640，充电盒和耳机的几何、材质正常；同模型两组 PNG 字节相同，Free4 为蓝灰、丹拿为象牙白。另有主窗口的 1280×900 headless 内容预览，两组字节相同。这些观察分别限定于原生模型帧和 headless 内容，未扩大成全部窗口的交互式目视验收。

### 可复算数据与本机证据

[完整指标 CSV](native-aot-comparison-metrics.csv) 保留 187 项主工具 comparison 的两组 n、median、p95 和相对变化，包括模型十个阶段及每角色的 Working Set、Private Working Set、Private Bytes、CPU 与观测峰值分布。`Milliseconds` 为 ms，`Bytes` 为 bytes，`cpuPercentOneCore` 为单逻辑核心百分比，`genN-collections` 为次数。没有角色前缀的内部 CPU/分配只属于 primary 进程。

[进程汇总 CSV](native-aot-comparison-processes.csv) 另保留 24 行角色及进程组合，`tray` 即 Host 托盘进程。内存典型值为各轮中位数的中位数，`ObservedPeak` 为所有轮次的外部样本最大值；它与完整指标 CSV 中“各轮观测峰值的 median/p95”是不同统计量，不混用。两份公开 CSV 仅含聚合值，没有 PID、用户路径或设备标识。

完整证据根为 `research/native-aot/2026-11-16-implementation/comparison-full-default/`：`execution.json`、环境前后快照、`measurement-tools/` 和 `measurement-tool-inputs.json` 固定执行版本；`matrix/session.json`、`matrix/comparison.json`、222 份 controller、Host 阶段收据、模型计时与 stdout 保留原始结果；`analysis/` 保存逐轮及进程组复算和四张模型帧的目视记录。公开 CSV 的来源与 SHA256 另存 `public-data-provenance.json`。

`audit-packaging/invariant-audit-v2.json` 的 `success=true`、`failures=[]`，包级和模型输入复核均通过。首份 `invariant-audit.json` 因审计脚本错误地要求丹拿模型来自独立 GLB 而失败，已原样保留；实际使用的是冻结输入中的嵌入资源，9,863,456 bytes，SHA256 `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6`，v2 修正了源绑定，没有更换模型或重跑产品来掩盖该审计错误。

测量后的 `full-default-identity-lifecycle-audit.json` 在上一级证据目录保存。580 项进程身份均指向 final-v2 的正确 EXE、X64、`.NET 10.0.12`；R2R 的两个动态代码标志共 290 项均为 true/true，AOT 的 290 项均为 false/false。主矩阵按角色验证 EXE 摘要；Host phase 的 84 项 identity 未填 receipt SHA 字段，因此这部分仅表述为 PID、启动时间、路径与运行时标志核对，EXE 摘要另引用包级文件核对。

退出结论同样分开：主 controller 没有逐项保存 actualExitCode/forcedKill，完整工具成功表明其直接启动进程的非零退出和超时检查没有触发；Host phase 明确记录 42 个 Host Exit0、42 个 worker Exit−1，均无需 controller kill。worker 属于 Host 已有兜底终止，不能称所有子进程都优雅退出。2026-11-16 04:47:09 +08:00 再次检查两包 EXE 路径，匹配的存活进程为 0。

## 九、报告完成后的体验安装器

按后续请求，另外在 `release/` 根目录生成两份 Windows x64 安装器。它们直接封装第八节的 final-v2 客户端，没有重新编译主窗口、后台或辅助入口，因此前述性能结果仍绑定同一组客户端 EXE。安装脚本、Inno 定义及包内使用说明的后续修改另记输入快照，不把当前整个工作树继续声称为性能测量时的源码快照。

| 变体 | 安装文件 | 字节数 | MiB |
| --- | --- | ---: | ---: |
| ReadyToRun + JIT | [HeyMelody_Windows_0.9.2_x64_Setup.exe](../../release/HeyMelody_Windows_0.9.2_x64_Setup.exe) | 191,369,912 | 182.50 |
| 混合 Native AOT | [HeyMelody_Windows_0.9.2_x64_AOT_Setup.exe](../../release/HeyMelody_Windows_0.9.2_x64_AOT_Setup.exe) | 161,926,524 | 154.43 |

安装器 SHA256 分别为：

- R2R：`663df7682dcf4ebc12bf1882dce51ca3717cf8f86bce6671eb4f218999e724c1`。
- AOT：`f0a2de5560038aee195dfd3f207f923c2efc2f937b3d7df1f034707450a5a651`。

封装使用 .NET SDK 10.0.401 与官方 Inno Setup 6.7.4 便携编译器；官方安装档案的 SHA256 与不可变发布摘要一致，签名有效，其许可正文与仓库保留的 1,521 字节文件一致。独立安装支持工具继续使用自包含 .NET 10.0.12，AOT 包从保留的 `HeyMelody.DeviceManager.runtimeconfig.json` 获取该版本。它位于 `.installer`，不改变纯 AOT `backend` 的结构。

两份安装器共用 `AppId=HeyMelody`、安装位置、用户设置和托盘后台，供顺序切换。运行另一份安装器前须关闭界面并退出托盘后台；编译模式改变时，通过已注册卸载器清理旧程序文件后再安装，避免在 AOT 后台留下 R2R 文件。缺少编译模式登记的旧安装按 R2R 处理，同模式覆盖升级保持原行为，用户设置和已下载资源继续保留。AOT 安装界面及卸载项标注实验版；[体验说明](../../release/NativeAOT-体验说明.md)与[安装器专题](../installer.md)说明操作步骤。

复现时先选择上述 SDK，再使用同一组目录和编译器：

```powershell
$iscc = (Resolve-Path artifacts/tools/inno-setup-6.7.4/ISCC.exe).Path
.\Build-Installer.ps1 -PackageRoot release/native-aot-comparison/2026-11-16/final-v2/r2r/HeyMelody_Windows_0.9.2 -Architectures x64 -OutputRoot release -InnoCompilerPath $iscc
.\Build-Installer.ps1 -PackageRoot release/native-aot-comparison/2026-11-16/final-v2/aot/HeyMelody_Windows_0.9.2 -Architectures x64 -OutputRoot release -InnoCompilerPath $iscc
```

输出已存在时脚本会拒绝覆盖；复现应另选输出目录。每份 EXE 附带构建回执 `.exe.json` 和载荷摘要 `.exe.payload.json`。本次成功封装分别耗时 97.38 秒、80.79 秒，包含安装支持工具发布与压缩，不属于第八节的完整客户端构建时间。

首次封装因 Inno 注册表位数参数写法失败，改用 `HKCU32` 后，第二次又在一个 263 字符的资源路径处遇到原生编译器路径限制。该 PNG 的源文件和暂存文件大小、SHA256 均一致；缩短暂存目录并增加显式路径长度检查后，第三次完整封装成功。失败日志、原暂存目录和各轮输入快照保留，未修改源客户端资源或系统长路径策略。

本轮另外通过最终构建脚本的 13 项隔离预检、28 项安装支持工具离线检查，以及 R2R、AOT、旧入口默认模式和两种非法输入的 5 项实际 ISCC 合成编译检查。独立审计核对了全部 2,404 个载荷文件的大小和 SHA256，1,303 个 R2R 架构文件、1,085 个 AOT 架构文件均与原包逐字节一致；源包到载荷仅更新两份 README，新增一份 Inno 许可，manifest 也保持字节一致。两份完整编译日志涵盖全部载荷，最长暂存路径降至 224 字符；原始客户端目录和两份 ZIP 摘要保持不变。

完整安装器成功编译、载荷静态核对及原客户端目录保全分别记录；这些检查没有向当前用户环境执行安装、卸载、双向切换或向导交互，也没有运行安装后的程序控制耳机或播放音频。两份本地安装器未签名。原始证据位于 `research/native-aot/2026-11-16-installers/`，成功封装及源码归档位于 `attempt-03/`，最终独立复核分别为 `final-installer-independent-audit-v2.json` 与 `independent-installer-log-and-zip-audit-v2.json`。审计首版的字段路径及日志版本后缀假设错误保留原件，修正解释后两份 v2 均通过，未修改产品。
