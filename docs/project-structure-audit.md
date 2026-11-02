# 项目结构审计与跨平台治理

审计日期：2026-11-01。改动前基线为 [`6072901140a4f9070954eee8d729200a93724178`](https://github.com/swift-lee/heymelody-windows/tree/6072901140a4f9070954eee8d729200a93724178)，下文“基线位置”均指该提交，不把迁移前的行号当作当前行号。版本保持 **0.9.2**。本报告同时记录现状、首轮改动和后续方向，不代表所有建议已实现。

## 结论

项目不是没有平台层：蓝牙已有 BCL 契约、Windows 实现、macOS 原生桥接和 Host 组合入口，项目引用图未发现明显环。应保留这些边界，而不是重写协议、后台或所有 OS 判断。

主要混乱集中在共享桌面代码的所有权和构建编排：App 被当作其他入口的源码库，Windows 窗口实现借放在设备管理器；DMG 挂在 Filament 工作流下，且等待整个三平台原生矩阵；应用包策略由 Filament 缓存模块提供；本地和 CI 的测试集合不一致。首轮治理针对这些可离线验证的结构问题，不扩展设备功能。

## 模块与数据流

基线 `src/` 有 24 个项目文件（包含嵌套的 Windows OfflineChecks），`tests/` 有 19 个可执行验收项目；测试入口是 `dotnet run`，不是 `dotnet test`。首轮新增 Desktop 共享库，不将历史实验塞入默认产品构建。

| 层次 | 模块 | 应有职责与依赖方向 |
| --- | --- | --- |
| 平台基础 | Platform、Diagnostics | 设备及事件契约、路径与进程边界、诊断；不依赖具体应用 |
| 协议与领域 | Protocol、Devices、Control、Calibration、Daily | 编解码、配置与能力、控制和校准工作流；不依赖 UI 或具体 OS 后端 |
| 共享客户端 | Contracts、Client | IPC DTO、客户端通信、MVVM 状态、写队列和偏好；不拥有蓝牙接收循环 |
| 平台实现 | Windows、MacOS | Windows API；macOS IOBluetooth、CoreBluetooth、CoreAudio 和原生 ABI |
| 后台组合 | Host | 托盘、supervisor、worker、连接代次、单接收通道、能力门控和写入冲突管理 |
| 共享桌面 | Desktop（首轮新增） | 字体配置、主题、窗口集成、背景、滚动、渲染初始化和 UI 诊断 |
| 产品入口 | App、DeviceManager、Calibration.UI、FitTest、HearingSetup | 主界面、设备选择、校准窗口及独立启动器；通过 Client 使用后台 |
| 资源与渲染 | Resources、Artwork、Rendering.Filament、native | 欢律资源解析、二维美术、托管渲染边界及原生库；资源不授予控制权限 |
| 可选工具 | Probe、Audio、Experiments、InstallerSupport | Windows 只读探测、历史驱动实验及独立安装器支持；验证范围分别说明 |

产品的运行数据流仍为：

```text
App / DeviceManager / Calibration.UI
              | Client + Contracts（IPC）
              v
Host supervisor -> 每设备 worker -> Platform 契约 -> Windows / MacOS 后端
              | Protocol / Devices / Control / Daily / Calibration
              + Resources（外观和配置，不产生控制权限）
```

Desktop 是多个入口共同引用的 UI 库，不替代 Client 或 Host。Host 内依赖校准状态和 UI DTO 的试听适配器仍属于业务组合层，不应为了“平台化”搬入底层 MacOS 项目，形成反向依赖。DesktopRuntime 是集中平台 facade，存在 OS 分支不等于业务泄漏。

## 问题、依据与处置

P1 表示影响交付边界或回归可信度，应优先治理；P2 表示可维护性、可复现性或范围表达问题。结构风险不等于已经发生的用户故障。

| 优先级 / 问题 | 基线证据 | 影响 | 本轮处置 |
| --- | --- | --- | --- |
| P1 共享源码所有权倒置 | DeviceManager、Calibration.UI、Host 跨入口链接 8、10、2 份源码；其中 Calibration.UI 为 App 的 9 份加 DeviceManager 的 Mica；App 也链接 Mica | 无二进制引用环，却有源码层的双向所有权；一次入口改动影响其他程序 | 抽取 Desktop；产品入口使用 ProjectReference；资源链接与测试 fixture 作为明确例外 |
| P1 DMG 失败域过大 | `.github/workflows/filament-build.yml:114-116` 的 `macos-package` 为 `needs: renderer`，renderer 是三平台矩阵 | Windows/Linux 原生构建失败也阻止与其无直接依赖的 macOS 包 | 独立 macOS packages，仅依赖本次运行的 osx-arm64 原生产物 |
| P1 CI 核心回归缺口 | `windows-build.yml:84`、`Invoke-MacOSPackage.ps1:109-116`；后者仅补 Platform、MacOS 与 Python 检查 | 包构建成功不能证明 Host、Client、Control、Calibration 等回归通过 | 统一离线 profile，相关测试失败阻止打包，SkipTests 仅消除已执行测试的重复 |
| P2 应用策略混入原生缓存 | `FilamentArtifactCache.psm1:32-42`、`Invoke-MacOSPackage.ps1:50,60` | macOS 包为获取上传许可和保留期而依赖 Windows/Filament 缓存职责 | 中立 CI 策略负责事件与包保留规则；Filament 只拥有原生缓存 |
| P2 Host 平台归档不对称 | 根目录 `MacCalibrationAuditionPlayer.cs`，对应 Windows 实现在 `Platforms/Windows`；共享接口夹在 Mac 文件 | 难以判断共享接口和平台实现的归属 | 对称归位 macOS 实现及 Windows 地址解析；共享接口、WAV 解析保留 |
| P2 版本来源重复 | workflow 硬编码 Filament SHA；另有 `filament.lock.json`；macOS CI 使用滚动 `10.0.x` 而非现有工具锁 | 当前值可一致，但后续易漂移 | 从既有锁读取，不升级依赖、不另造版本配置体系 |
| P2 测试清单漂移 | 两个 Build 脚本和 CI 分别维护 runner；Windows 默认遗漏 Platform 等新增检查 | 新平台模块可能不进入通常的验收入口 | Windows/MacOS/Renderer 共用一个执行器，保留已有单项运行接口 |
| P2 写队列测试缺少呈现屏障 | `src/HeyMelody.Client/MainViewModel.WriteQueue.cs:104,116` 先完成被丢弃 click，再恢复显示；`tests/HeyMelody.Client.Tests/WriteQueueTests.cs` 只等待 save/click | 无 UI 同步上下文的测试可在 preset 恢复前抢先断言，产生历史 BUG-07 失败 | 仅测试订阅实际 preset 恢复事件并使用原 Deadline；保留仅一次操作和最终 preset 0 的断言，不改产品队列 |
| P2 原生验证表述易混淆 | 原生准备默认 `-SkipSmoke`；Rendering/ModelPreview 默认回执明确 `nativeRequested=false` | 静态清单校验或托管测试可能被误称为 GPU/原生运行通过 | 新增无 GPU 的精确库加载/ABI 检查；仍与真实 renderer smoke 区分 |
| P2 全仓不完全自包含 | `Audio.csproj:10` 引用不存在的相邻 `../../../manager/src/LhdcManager.Core` | 全量遍历构建会失败，但不阻止正常 GUI 发布 | 文档明确可选实验边界；不删除能力，也不加入默认门禁 |
| P2 平台 facade 内部职责较多 | `Platform/DesktopRuntime.cs` 同时含 Windows 会话和 Unix 目录、权限、macOS P/Invoke；InstallationCheck 主要验 Mac 包 | 后续平台增长时维护成本增加 | 后续分内部平台实现，保持外部 API，首轮不重写安全敏感 IPC |
| P2 编译平台标志双轨 | 根 props 定义 `HEYMELODY_WINDOWS`，Host 工厂与能耗适配使用 SDK `WINDOWS` | 当前 Windows TFM 同时定义二者，尚未证实 bug；交叉参数组合易漂移 | 后续统一标志并增加 TFM/RID 一致性验收 |
| P2 文档导航落后于移植 | 开发模块表未列 Platform、MacOS、Rendering.Filament；CI 说明仍从 renderer 找 DMG | 新贡献者难以区分产品、平台与历史实验 | 更新开发导航、macOS 操作入口和索引；历史记录保持原义 |

已有正确设计继续保留：Host 的平台条件引用、未知能力保持未知、单设备单接收通道、身份/代次/快照顺序校验、通知和缓存不触发 SET、缺失回执不重放、macOS 试听显式选择蓝牙输出且不回落系统默认设备。Linux 蓝牙仍为 unavailable，渲染兼容性通过不代表完整 Linux 客户端可用。

## 首轮构建与接口

- Desktop 保持原 namespace 和内部可见性，按实际消费者授予友元访问；不全面公开原 internal 类型。渲染初始化改为入口显式传入现有偏好值，库不读取 Client 偏好。主题归共享库，图标 XAML 显式指定程序集；字体文件、图标、美术、模型和 WAV 不迁移。
- `Invoke-OfflineTests.ps1 -Profile Windows|MacOS|Renderer -OutputRoot <目录>` 是统一检查入口。Windows 在原默认集合上补 Platform、MacOS 托管检查、ModelPreview 和 InstallerSupport，并保留 Probe 与 Windows OfflineChecks；MacOS 保留原 16 项及 Python 打包测试；Renderer 只运行两项离线渲染检查。Audio 单独可选。
- Windows 为 `checks -> package[x64, arm64]`；macOS 为 `native-osx-arm64 -> package-arm64`。Filament 工作流自动检查 Linux，手动可检查三平台；不再生产 DMG。原生 SHA、RID、ABI、文件摘要与来源验证失败必须阻断打包。
- `Rendering.Filament.Tests --native-abi <绝对native目录> --output-dir <目录>` 不创建引擎，只加载指定库、核对绑定导出和 `hm_get_abi_version`，回执绑定库及原生清单摘要。Windows x64/macOS arm64 强制执行；本轮 CI 的 Windows arm64 为交叉构建，不宣称执行。
- 本地 Build 入口也通过 `tools/ci/Invoke-NativeAbiCheck.ps1` 检查输入和实际 staged 原生文件；`SkipTests` 不绕过 ABI，`FinalizeOnly` 重新验证。是否同架构由所选 `dotnet --info` 决定；真正异构 Windows 明确记为静态检查，macOS 异构直接失败。回执放包旁，不改变 bundle 或发布目录。
- 包策略保持 push/manual 可上传、PR 只构建验收；main 包 7 天、其他 ref 1 天、检查回执 1 天、Windows 原生缓存 30 天。保留缓存来源仓库、事件和 workflow 白名单，不把“整个来源 run 成功”新增为缓存要求。

不变的公开边界：IPC schema 4、协议编号、缓存格式、发布目录、五个产品入口、版本 0.9.2、ad-hoc 未公证签名。本轮不创建 GitHub Release，不修改设备支持或硬件验证范围。

## 验证记录与局限

| 范围 | 输入与结论 |
| --- | --- |
| 改动前 Windows 基线 | Windows x64，SDK 10.0.403，`6072901`；App.Tests 152 项通过、`nativeBluetoothCalls=0`；Platform.Tests 2 通过/6 Unix 专属跳过。BUG-03 历史 ANC 窄窗失败未在该 Windows 基线复现，不代表其他 OS 已复验 |
| 改动前 GitHub CI | 同 SHA 的 [Filament 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/40451023635)与 [Windows 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/40451023727)成功；DMG 上传步骤成功，但分支包只保留 1 天，现已过期。此证据不证明旧 CI 有完整核心测试门禁 |
| 本轮 Windows 完整门禁 | Windows x64、SDK 10.0.403；19 个 .NET runner 中 18 个通过，Resources 为 40/41、导致整体非零。App 152、Client 198、Host 132、Rendering.Filament 141、ModelPreview 180 均通过；Platform 6 个 Unix 专属子项跳过，MacOS 原生 ABI/worker-loop 在 Windows 未执行。完整收据：`artifacts/structure-windows-final-a743c0c14c934383ba050bd5caff93ad/offline-validation.json` |
| 本轮 Resources 单次补验 | 2026-11-01 用户临时授权 full access、关闭工具沙箱，以非管理员身份仅运行一次 Resources：41/41 通过、无 skip，包含 `DamagedMembers`。回执：`artifacts/bug05-full-access-repro-20261101-205349-a3f311412b3d4126ab2f23f7f14934a1/`；不代表完整 Windows profile 全绿，也不确定先前失败的原因 |
| 本轮便携与 CI 合同 | Python 3.11：结构 7、Windows 发布校验 fixture 8、macOS 打包 fixture 10，汇总 25/25 通过，日志在 `artifacts/structure-python-final-9642d71f92b34733a7541f1311b5d31c/`；CI 合同 216（含 ABI gate mock 30）、原生缓存 fixture 54 通过，12 个 PowerShell 文件解析通过。最终 CI 合同收据在 `artifacts/ci-implementation-checks/91c6671373b341a08dd4fe233ef9229d/contracts.json` |
| 本轮托管构建 | 五个 Windows 入口全部构建通过；App、DeviceManager、FitTest、HearingSetup 的 osx-arm64 托管交叉构建通过。Mac Host 还需要原生 macOS 构建 `libHeyMelodyMac.dylib`，未在 Windows 伪造或绕过。收据：`artifacts/structure-managed-builds-9749104fba2e4473acd76552e8da0f5f/managed-builds.json` |
| 本轮 Windows 原生 ABI | 旧本地清单缺少当前必需的 log callback 字段，被拒绝复用。直接 GitHub 连接明确失败后仅进程级使用获准的 59991 代理，按 Filament 锁定源码重新构建；精确加载新 win-x64 库，11 个导出与 ABI 2 通过，无 GPU/蓝牙调用。新打包包装器也真实执行通过，`nativeAbiVerified=true`，回执在 `artifacts/structure-native-gate-18c95f4322db46128cf37c85f83c4145/native-abi-validation.json`。库 SHA256：`2e811953a523d7ce0ffae78401d5d6c64244cddccfbdd0004f0f86b1d5da0ddb` |
| 本轮原生 macOS/DMG | 尚未执行，本轮无新 DMG 或远端验收结果。后续仍必须绑定精确 SHA，检查签名、摘要、挂载/ditto 复制和五入口安装复验；不能拿旧 DMG 代替 |

### 当前阻塞与历史失败

本轮未放宽或跳过资源完整性测试。第一次完整 Windows profile 为 Resources 40/41，增加仅失败时的有限异常诊断后定向复验为 37/41，最终完整 profile 仍为 40/41，失败 case 从短 GLB 转移至 WebP/GLB JSON 段等输入。最终诊断确认：坏 GLB 的 `InvalidDataException` 已被正常捕获，随后发布 `.download-*` 目录时在 `ResourceCoordinator.cs:377` 的 `Directory.Move` 收到 `IOException`，HResult 为 `0x80070005`。诊断补丁不改变输入、断言或刷新次数；未增加成功重试。

这补充了历史 BUG-05 的底层错误证据，但尚不能确定拒绝访问的句柄或进程，也不能将其它 “Initial art failed” 统一归因。静态检查未发现可证明的 fixture 同目录冲突或应用持句柄泄漏；Resources 及其依赖的产品实现未在本轮修改。后续单次 full access 补验通过不构成根因定位，不能将先前失败归因于沙箱；BUG-05 仍未闭合。按分轮规则，资源发布行为修复属于额外方向，需另行确认范围，不能以结构治理为由改掉测试或放宽断言。

2026-11-02 用户指示按正常签名流程提交并推送本轮改动，不等待 GitHub Actions。本次状态更新仅记录该交付授权，不预填提交 SHA 或宣称推送成功，也不将远程 CI 或新 DMG 验收记为已完成；此前完整 Windows profile 的 18/19 结果保留。

Host 的历史 BUG-06 曾在本轮首个默认 runner 超时；同业务断言的完整诊断模式及随后两次完整 Windows profile 均为 132 项通过，根因未闭合，不标为已修复。Client 的 BUG-07 则已定位为上述测试观测竞态，仅增加实际恢复屏障后两次完整 profile 均为 198 项通过。

验证分层：结构/静态检查、离线模拟、原生 ABI 加载、原生窗口、真实 GPU、真实耳机分别记录。Hosted macOS 显式跳过 Metal probe，但不跳过五入口和挂载安装检查；蓝牙授权、双连、睡眠恢复、实际音频路由及听感未验证。Windows 本机不具备 `codesign`/`hdiutil`，交叉编译不能替代 DMG 验收；Windows arm64 的交叉发布也不能证明 ARM64 硬件运行。

完整日志、图像和产物留在忽略的 `artifacts/`、`release/`，公开文档不收录设备地址、用户快照或完整命令输出。CI 回执和 DMG 绑定构建 SHA，最终交付报告实际运行链接、文件位置及 SHA256。

## 2026-11-02 Actions 失败复核

结构治理提交 `d8103ad` 的首批远端结果补充如下，不改写上述本地及历史失败记录：

- [Windows 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/41326973212)的 19 个 .NET runner 全部通过，Resources 为 41/41；Python 结构检查的两个 fixture 报错，故两个架构的打包 job 均未开始。
- [macOS 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/41326973115)的原生 runtime job 成功，离线检查为 15/16 个 .NET runner 通过，Resources 同为 41/41。App 的设备信息复制按钮全显检查及同一 Python 结构检查失败；后续精确 ABI 加载、应用构建、DMG 签名和安装校验尚未执行。模拟 ABI 合同通过不能替代实际加载。
- 同批 [Filament Linux 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/41326973064)成功。上述两处失败不属于 BUG-05，也不能据此关闭该历史项。

结构检查的问题是项目路径先 `resolve()`、传入的根目录却保留别名：Windows 临时目录的 `RUNNER~1` 与长路径、macOS 的 `/tmp` 与 `/private/tmp` 在 `relative_to()` 中不匹配。修复位于两个扫描入口，而非只改变 fixture；canonical 与 `nested/..` 回归要求违规列表、相对路径及合法引用判定完全一致。不修改 `TMPDIR`、Python 版本或结构规则规避失败。

macOS App 的失败目标为“复制设备信息”，不是日志按钮。回执显示按钮底边约比视口低 13 DIP；现有夹具在尺寸出现后只请求一次 `BringIntoView`。该证据不足以定位生产滚动库内部缺陷，也没有证据证明字体、主题或 Desktop 源码迁移引入了该行为。本轮只调整 headless 准备：几何稳定后发起一次可视化请求；再次稳定且仍有纵向裁切时，最多一次按残差夹限 Offset，随后重新等待稳定。原 3 秒总期限、全显、真实命中、连续三个成功样本和复制断言均保留；独立 SmoothScrolling 测试继续检验未经夹具校正的产品滚动行为。

本轮 Python 回归为结构 9、Windows 发布校验 8、macOS 打包 fixture 10，合计 27/27 通过；日志在 `artifacts/actions-structure-fix-tests-d8485fc1b278480aa158a8f3b62bcd30/`。CI 合同 216、原生缓存合同 54 通过，回执在 `artifacts/ci-fix-contracts-522c08a6854c42c1958a9be1a1620444/`。App 和完整 profile 的新证据如下，不将这些便携检查称为原生 macOS 或 DMG 验收。

Windows x64、SDK 10.0.403 的新布局定向回归为 6/6，单独完整 App 为 158 项通过、111 张 headless 图像，两项独立验收均首轮退出 0，`nativeBluetoothCalls=0`。日志与回执位于 `artifacts/actions-fix-ui-helper-fa0de9307cfe47d2859967aa10f4aa3f/`。三个负向场景分别要求覆盖层、超高目标和横向裁切仍然失败，并保留原期限；不能将该夹具通过等同于真窗口、显示器 DPI、GPU 或真实设备验证。

首轮修复工作树随后唯一一次完整 Windows profile 执行了 19 个 .NET runner 及两个 Python 门禁：17 个 .NET runner 和两个 Python 门禁通过，其中 App 158、Client 198、Rendering 141、ModelPreview 180。本地整体仍非零：Resources 为 40/41，失败于租用副本的重下载，未取得足以确认其与此前拒绝访问相同根因的证据；Host 为 `HostEfficiencyTests.cs:57` 的 `UntilAsync` 取消，和历史 BUG-06 记录一致。未重跑至通过或修改这些产品行为。收据在 `artifacts/actions-fix-windows-a7eaa172c4df49d19e8d24500c14dd09/offline-tests/offline-validation.json`；新修复的跨平台验收仍以同 SHA 远端完整门禁为准，不关闭 BUG-05/06。

### 同轮后续门禁

修复提交 `f6e0de5` 的 [Windows 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/41442085864)为 21/21 个执行组通过，App 158、Resources 41、Host 132，两个架构的打包均成功；同批 [Filament Linux](https://github.com/swift-lee/heymelody-windows/actions/runs/41442085955)成功。Windows 检查与架构包回执下载于 `artifacts/actions-fix-delivery-f6e0de5-d17b8cbcf4454f4d825cff2fcd56edd3/`，不把 arm64 交叉发布的静态检查称为原生加载。

该提交的 [macOS 运行](https://github.com/swift-lee/heymelody-windows/actions/runs/41442085906)中两个 Python 门禁及新增六个布局夹具场景通过，但后续 Host 和 App 仍失败，16 个 .NET runner 为 14 个通过，尚未生成 DMG。Host 位于 `EnergyTests.cs:78` 的线程断言，不是前述 HostEfficiency 等待取消；App 位于折叠信息卡组的全显断言，不是先前展开后的复制按钮点击。完整回执保存在 `artifacts/actions-fix-macos-watch-229113d0cc3145f2afd4b9b8c637916c/receipts/`，监控客户端中途的网络断线不计为 CI 构建失败。

后续仍只修测试准备和观测：折叠组先等待两卡及视口几何稳定，再请求一次 `BringIntoView`，再次稳定后按原公式居中一次，最后等待全显与稳定，仍受 3 秒总期限约束。原复合断言完整保留并补全部操作数；新增正常居中与首次请求后延迟几何增长回归。能耗测试则改用保持存活的专用非线程池 caller，记录真正的 `IsThreadPoolThread` 并保留不同于 caller 的断言，避免异步 caller 释放后池线程合法复用 ID 被误判；触发、观察及清理共享原 4 秒预算。生产控制器的线程池排队、滚动、动画、字体及布局均未修改。

第二轮 Windows x64 本地定向布局为 8/8、完整 App 为 160 项通过，日志在 `artifacts/actions-fix-ui-group-2ac6086dd2f24667ae19a0fd10c539c5/`；完整 Host 默认验收为 132/132，日志在 `artifacts/actions-host-energy-fix-64524095f54c4249913dd7d3fbc45204/`。各验收均首轮退出 0、未超时，`nativeBluetoothCalls=0`，不冒充原生 macOS 或真实设备结果。

本次交付要求等待同一修复 SHA 的 Windows/macOS Actions 完整通过，下载短期回执与 DMG 核对源 SHA 和摘要；不以重跑到绿、跳过门禁或更改产品行为替代修复。用户随后因暂时无法签名，授权本轮使用延后签名包装器创建临时无签名提交、登记待补签，保留 hooks 与持久签名配置；这不改写此前 `d8103ad` 的正常签名记录。

## 后续分轮

1. **平台内部治理**：拆分 DesktopRuntime/InstallationCheck 的内部 OS 实现，统一编译标志，覆盖平台、TFM、RID 一致性；不重写现有路径权限与会话隔离语义。
2. **可选工具与开发入口**：为常规产品、测试和历史实验提供清楚的构建分组；评估 Audio 外部依赖自包含化，保留来源、许可与原有能力。
3. **资源与原生交付**：在保留来源、ABI 和摘要校验的前提下评估 macOS 原生缓存与工具链统一；不引入跨运行 latest 产物捷径。
4. **设备与运行时问题**：按现有 BUG、MAC 待办分别复现；无界广播队列需要真机负载、取消和顺序验证，不作为本轮顺手重构项。

条目状态统一维护在[优化待办](optimization-backlog.md)。已有历史失败在当前平台未复现时补充新证据，不删除原始记录，也不将全部历史项一并标为解决。
