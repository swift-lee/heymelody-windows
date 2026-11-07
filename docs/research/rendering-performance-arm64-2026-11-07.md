# Snapdragon 8cx Gen 3 渲染性能审计（2026-11-07）

## 结论与适用范围

本轮针对 Windows ARM64／Adreno 实机，测了无 3D 的静止、滚动、完整生产 WebP 动画，以及有 3D 的自转、滚动、自转冻结；另做了不经过 Avalonia 的原生分辨率／MSAA 对照。**不修改产品渲染策略，不连接或设置真实耳机。**

主要结论：

1. **该 Demo 普通设置页的 UI 回调能力，并非固定受限于三四十次／秒。** 当前默认 UI 已使用 Adreno 的 ANGLE／D3D11 硬件路径。无 3D 程序滚动约有 100 次 UI 回调／秒；静止界面 CPU 很低。静止界面的低回调频率不代表有动画掉帧，更不能从回调频率推断屏显 FPS。
2. **持续 3D 明显拖累同窗滚动。** 首轮无外部采集器对照中，无模型滚动约 100 次 UI 回调／秒，有模型时约 47 次；补齐健康断言的复测分别约 106／57 次，模型位图交付约 40–52 次／秒。两轮绝对值有波动，结论一致。冻结模型后，持续 CPU／GPU 开销几乎消失，但已加载资源仍留在内存。
3. **瓶颈在完整的离屏渲染／回读／CPU 合成与复制／Avalonia 位图交付链路，不能只盯三角形或 shader。** 相近像素量的 native-only 实验中，Vulkan 完成帧序号可推进约 120／秒，但 latest-frame 成功复制约 80／秒；实际 UI 又更低。这不能解释成“GPU 本身只能画 40 帧”，也不能把两种实验直接相减成 Skia 上传耗时。
4. **本机 Filament 两个后端都经 Mesa／D3D12，不是 Qualcomm 原生 Vulkan／OpenGL。** Vulkan 为 Dozen → D3D12 → Adreno，OpenGL 的 vendor 为 Microsoft、renderer 为 D3D12 Adreno、version 为 Mesa 4.2。Vulkan GPU timestamp query 密集报错，OpenGL 则启用 `dont_use_timer_query` workaround，均没有可信的 GPU 纯渲染耗时。不能泛化到所有 Windows ARM64 驱动，也不能把本机换 OpenGL 当作绕开翻译层。
5. **还有独立的滚动／缓存内存成本。** 无模型滚动的峰值工作集已约 836 MiB，模型加滚动约 1.1–1.2 GiB。短测只证明占用较高，不证明内存泄漏。强制 WARP 显著更慢，不是本机应采用的优化方向。

本轮主指标是 **UI RAF 回调频率、模型位图交付／观察频率、进程 CPU 与内存**，不是实际显示器 present FPS。没有有效的 GPU 时间戳或 ETW 队列／堆栈证据，因此不报告“GPU shader 占 X%、上传占 Y%”这类伪精确分解。

## 版本、环境与方法

| 项目 | 本轮输入／边界 |
| --- | --- |
| 产品源码基线 | `e9248ec37a366c51bc405f2b122be9fb57081395`，额外改动仅为审计工具和文档 |
| 系统与运行架构 | Windows 11 Pro ARM64，10.0.26300；原生 ARM64 进程，8 个逻辑处理器 |
| SDK／运行时 | .NET SDK 10.0.402／runtime 10.0.14；Avalonia 12.1.4 |
| GPU | Qualcomm Adreno 8cx Gen 3；WMI 驱动 30.0.4375.5500，2025-07-16 |
| 实际 UI renderer | ANGLE Qualcomm Adreno／D3D11，renderer 字符串含 30.0.4369.8700；与 WMI 字段分开记录，不强行归为同一个版本 |
| 实际 Filament Vulkan | `Dozen Mesa 26.3.0-devel (git-e3fc7749c6)`／`Microsoft Direct3D12 (Qualcomm(R) Adreno(TM) 8cx Gen 3)` |
| 实际 Filament OpenGL | vendor `Microsoft Corporation`；renderer `D3D12 (Qualcomm(R) Adreno(TM) 8cx Gen 3)`；version `4.2 (Core Profile) Mesa 26.3.0-devel (git-e3fc7749c6)` |
| 供电／显示 | AC、100% 电量、Balanced；DWM timing 指示约 120 Hz；未改电源或驱动设置，未测温度／频率 |
| 窗口／模型 | 1280×900 DIP，缩放 1.75，物理窗口 2240×1575；3D 视口 1050×910，4×MSAA，20 个 mesh、94,927 个三角形 |
| 模型输入 | Enco Free4 Dynaudio `model.glb`，9,863,456 B；SHA256 `7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6` |
| 无 3D 动画输入 | 当前欢律来源 X4 detail WebP，6,508,910 B，780×780，180 帧×33 ms，5.94 s 循环；SHA256 `269c63cccf729ea819a38cc010f6c7f6ed86fefa00bc680e84f1e4863f3d4736` |

使用当前 HEAD 成功 workflow 的 `filament-native-v1-win-arm64-b521be559445f1bdf3b6ad6cd69f1c7160bbd65577a25e175ba19bd9088ccbea`，而非本机旧产物：

- Actions run `42346645780`，artifact `13564510839`；`Verify-FilamentRuntime.ps1 -RequireCurrent` 校验 19 个文件和必要导出通过。
- Filament commit `ea31afeaf66339e7414aa648aa76c7e61adbde28`，ABI 2。
- native DLL SHA256：`f563f85a2fbc1fbbcc475c94caa0a167ef679ef980711d369d0fbb3e6c01b6aa`。
- runtime manifest SHA256：`91761d1c8e470ef1fed3448db773eeaf59450e55631d744931c14667553a6dfa`。

测试使用真实 MainWindow、Dynaudio Demo 数据服务及实际模型控件，不启动生产 Host／蓝牙／IPC；偏好路径隔离且验证没有写入。无模型静态场景把同一 Demo 设置页的 hero 显式替换成仓库内置 X4 PNG（1440×1440，644,604 B），不是合成矢量占位，也不是一个真实 X4 设备页的完整资源路由；动画场景在该槽位直接载入上述生产 WebP。因此这里隔离的是同一真实 UI 的不同美术渲染路径，不代表所有无 3D 设备页。

每场预热 5 秒，再测量约 30 秒；冻结场景在预热后另暂停 Motion 并等待 1 秒，再开始计时。保存完整回调、0.5 秒进程／控件采样和起始截图。程序按墙钟轨迹修改滚动偏移，**不是实际鼠标、触摸板或 SmoothScroll 输入链路的测量**。模型使用无限制帧率设置；冻结场景验证相机更新与位图交付计数不变。

CPU 表中的 100% 是占满 **一个逻辑核**，整机百分比除以 8。峰值工作集取测量期 0.5 秒样本，不是整个进程生命周期的精确高水位。记录的前台／IsActive 也是离散样本，不等于每个时刻的强保证。

## 无 3D 界面：首轮与动画复测

| 场景 | CPU，单核等效 % | UI 回调／秒 | 回调间隔 P95，ms | 峰值工作集，MiB |
| --- | ---: | ---: | ---: | ---: |
| 静止，不主动请求 RAF，带全量计数器 | 1.98 | 不测 | — | 318.21 |
| 静止，请求 RAF 但不主动重绘，带全量计数器 | 2.97 | 42.85 | 33.47 | 318.46 |
| 程序滚动，硬件 UI，无外部采集器 | 60.05 | 100.16 | 16.78 | 836.41 |
| 同类滚动，硬件 UI，带全量计数器 | 61.29 | 98.32 | 16.78 | 842.19 |
| 同类滚动，强制 WARP，带全量计数器 | 482.68 | 42.23 | 26.88 | 573.55 |
| 完整 WebP 动画，无外部采集器，有效复测 | 19.16 | 39.88 | 33.58 | 386.68 |

解释：

- 静止时目标 GPU engine 几乎为 0；没有持续内容更新需求，不能用静止的 42.85 次回调推断显示器只能显示 43 FPS。
- WebP 原素材约 30.3 帧／秒；UI 回调频率不等于独立动画帧，更不等于屏显 FPS。不能要求每次 120 Hz 回调都有不同素材帧。
- 无模型硬件滚动已有约 0.60 个核、约 52–53 MiB／30 秒托管分配及明显工作集增长。删除 3D 不能消除这些普通界面／合成成本。
- WARP 使用约 4.83 个核，回调反而降至约 42／秒。当前默认 renderer 已是硬件，不能把历史 WARP 问题当作当前瓶颈，也不应把 `--basic-render` 当作本机的提速开关。
- 初次无采集器动画复测曾丢失前台，只有 1/59 样本有效，已排除；表内采用后续 59/59 有效样本的独立复测。

## 有 3D 界面：首轮完整对照

以下六场均无外部 GPU 采集器，前台与 Active 样本各为 59/59。位图更新指控件实际接收／观察到新模型位图，不是屏显 present。

| 后端／场景 | 位图更新／秒 | UI 回调／秒 | CPU，单核等效 % | 峰值工作集，MiB |
| --- | ---: | ---: | ---: | ---: |
| Vulkan，自转 | 41.63 | 65.43 | 72.65 | 731.62 |
| Vulkan，自转＋程序滚动 | 44.63 | 46.83 | 114.05 | 1226.34 |
| Vulkan，冻结 | 0 | — | 2.81 | 730.84 |
| OpenGL，自转 | 36.75 | — | 60.54 | 704.07 |
| OpenGL，自转＋程序滚动 | 44.73 | — | 109.93 | 1160.36 |
| OpenGL，冻结 | 0 | — | 2.13 | 668.05 |

Vulkan 自转的新位图观察间隔中位数／P95／最大值为 25.72／35.80／49.31 ms；滚动时为 24.46／33.83／151.17 ms。滚动时不仅平均节拍下降，也存在明显长间隔。冻结两后端后，相机更新、位图锁、新位图复制及 FrameId 的测量期增量均为 0，故不能把冻结前缓存的 native timing 当作冻结期仍在渲染的证据。

首轮 runner 的模型末计数在截止回调之后的 continuation 读取，边界可能相差一帧；且没有模型终态健康／动画绘制推进断言。原始数据保留为 v1，不追溯赋予后续 v2 保证。后续复核补齐这些断言，不修改产品。

## v2 健康与端点复核

使用 `v2-health-endpoint-20261107`：逐样本与终态检查模型 Ready／实际后端／LoadError；动画检查加载、活动状态、错误及测量期实际 draw 推进；截止分支同时固定 UTC、相机／位图计数。六场模型、动画及两次无模型滚动共九场均成功。主无模型对照采用最后独立运行的 `static-scroll-…-v2-03`，首个 `v2-02` 可能与短时资料分析重叠，仍完整保留但不作为主对照。

| 场景 | UI 回调／秒 | 模型新位图／秒 | CPU，单核等效 % | 峰值工作集，MiB |
| --- | ---: | ---: | ---: | ---: |
| 无 3D，静态 PNG，程序滚动 | 106.49 | — | 60.86 | 829.26 |
| 无 3D，完整 WebP 动画 | 41.92 | — | 22.96 | 375.22 |
| Vulkan，自转 | 65.63 | 46.64 | 64.76 | 762.73 |
| Vulkan，自转＋程序滚动 | 57.11 | 52.18 | 110.37 | 1211.12 |
| Vulkan，冻结 | 44.88 | 0 | 3.64 | 739.71 |
| OpenGL，自转 | 67.94 | 40.25 | 52.90 | 686.48 |
| OpenGL，自转＋程序滚动 | 57.15 | 52.05 | 105.44 | 1204.42 |
| OpenGL，冻结 | 44.12 | 0 | 3.18 | 677.08 |

所有新主表场景的前台／Active 采样均为 59/59 或 60/60；模型起终 Ready，LoadError 为 null，过程采样坏状态为 0；冻结计数仍不推进。WebP 实际 draw 909 次、约 30.29 次／秒，符合素材约 30.3 Hz；这是 draw callback 数，不是独立解码帧数或屏显 FPS，已排除“静态 fallback 却把它当动画性能”的风险。

本轮 Vulkan 滚动的 UI 回调比无模型低约 46.4%，模型新位图间隔 P95 约 34.72 ms；OpenGL 约 35.34 ms。Vulkan 自转／滚动的混合 readback 均值约 13.54／13.29 ms，OpenGL 约 14.52／14.31 ms，仍远高于约 0.17–0.23 ms 的 CPU render 提交均值。未复制新帧的位图锁比例为 Vulkan 自转／滚动 28.93%／8.63%，OpenGL 40.76%／8.92%，确认该现象不是首轮偶发。

交付差异在 UI 内部也能观测：Vulkan 自转约 30 秒有 1970 次相机更新／位图锁、最高完成帧序号推进 1969，但成功复制仅 1400 次；OpenGL 对应为 2039／2037／1208。没有 fallback copy。因此 UI 自身请求／调度节拍较低与“完成序列到新位图”的交付损失同时存在，不能把 40–47 次位图更新说成 native 总共只 render 了这么多。仍需逐 callback／队列证据，才能将调度、锁、回读与上传分别定量。

v2 模型交付较 v1 提高约 9.5%–16.9%，并未统一收敛到某个固定 FPS。这既复现主问题，也说明顺序短测受运行状态／调度影响；未测温度、频率或做严格 ABBA 热稳态设计，不能把变化归因于测试断言修复、采集器或某项产品优化。产品渲染实现未改变。

## 原生隔离：分辨率与 MSAA

同一当前模型／runtime，在不引用 Avalonia 的工具中渲染并复制到托管缓冲。native-only 指绕过 Avalonia，不代表底层驱动是 Qualcomm 原生 API。每场预热 5 秒、测量 15 秒，目标请求 120 Hz；一次仅有一个 GPU 测试负载。**复制 Hz 是 CPU 缓冲交付率，序号 Hz 是最高已完成 render 序号的推进率，均非屏显 FPS。** FrameId 在成功 beginFrame／render 后递增，并在 readback 完成时更新诊断最大值；在途边界、回调重排与旧 ready 帧覆盖使序号推进不能当作精确完成计数。

| 后端 | 方形像素／MSAA | 复制 Hz | 序号 Hz | CPU，单核等效 % | 成功 TryCopy 均值／P95，ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Vulkan | 320／4 | 119.73 | 119.86 | 28.64 | 0.140／0.702 |
| Vulkan | 640／4 | 116.26 | 119.86 | 41.25 | 0.491／1.547 |
| Vulkan | 960／4 | 80.39 | 119.79 | 56.24 | 3.031／6.349 |
| Vulkan | 960／1 | 83.26 | 119.93 | 60.31 | 2.731／5.991 |
| OpenGL | 320／4 | 114.00 | 119.73 | 25.42 | 0.232／0.826 |
| OpenGL | 640／4 | 105.39 | 119.06 | 34.69 | 0.512／1.738 |
| OpenGL | 960／4 | 91.79 | 110.25 | 52.60 | 2.111／5.679 |
| OpenGL | 960／1 | 104.06 | 116.79 | 55.52 | 2.092／4.889 |

每条件一场顺序短测，未做原生矩阵的重复／顺序平衡或置信区间；下述百分比是本轮观测差异，不是所有设备上的固定收益或统计显著性承诺。

关键证据：

- **交付少不等于 GPU 只画了那么多。** Vulkan 960／4 的 15 秒内实际请求 1798 次，最高完成序号推进 1797，但成功复制只有 1206 帧，复制 ID 中跳过 591 个。这远大于起止边界一两帧的差异，指向生产序列到 latest-copy 交付之间的批处理／同步／覆盖损失，不能把 80 Hz 叫作 GPU 渲染上限。
- **像素量确实影响复制／交付，而不仅是三角形。** Vulkan 320→960，像素量增加 9 倍，复制率下降约 32.9%，CPU 约翻倍、成功 TryCopy 墙时从 0.140 增至 3.031 ms，但完成序号推进几乎仍为 120 Hz。OpenGL 复制率下降约 19.5%，序号推进也下降。降低目标尺寸是有实测依据的方向；尚未测量它在完整 UI 中的实际收益和画质。
- **只关 MSAA 不能根治。** 960 下 4→1，Vulkan 复制率只提高约 3.6%，OpenGL 约 13.4%；后者序号推进约提高 5.9%。它是可选画质／功耗取舍，不是全部瓶颈。
- 所有实际请求为 119.85–119.99 Hz，poll 为 474.82–646.65 Hz；进程局部高精 wait 均值 1.438–1.502 ms、P95 1.656–1.676 ms。没有修改全局 timer／电源设置。先前 `Thread.Sleep(1)` 版本实际上仅约 65–66 次请求／秒，已作为**测量方法负对照**保留，其 MSAA 结果不能用于估计 GPU 吞吐上限。
- 181,127 行原始调用／帧样本经结构检查，8 组计数与 summary 一致，复制 ID 严格递增且 generation／尺寸／实际后端稳定；测量期托管分配为 0、GC 为 0，不等于 native／GPU 内存开销为 0，本工具没有采集驻留内存。

native-only 已包含回读、CPU 背景合成和复制，只省略位图锁、Skia 重上传及 DWM。960² 比 UI 1050×910 的像素量少约 3.5%，但长宽比、投影覆盖、自转相位（15 秒／30 秒）与复制目标内存不同。**不能拿二者的 Hz 或 readback 分布直接相减来量化 Avalonia 上传成本。** 小尺寸还接近 120 Hz 请求上限，也没有测出其最大容量。

## 成本路径与已确认的问题

### 1. 全帧 GPU → CPU → GPU 桥接

当前路径不是 Filament 的 GPU 纹理直接交给 Avalonia：

`Filament render → 全帧 RGBA8 readPixels → CPU 背景 alpha 合成 → mutex 下 memcpy → UI 线程 WriteableBitmap.Lock／复制 → Skia 绘制／上传 → DWM`

代码依据：

- [物理像素与固定 4×MSAA](../../src/HeyMelody.App/EncoFree4ModelView.cs)：`FitCamera` 使用 `RenderScaling`，不是按 DIP 控制渲染成本；没有动态分辨率。
- [native render／readPixels／readback_complete](../../native/HeyMelody.Filament.Native/src/hm_filament.cpp)：GPU 回读整个目标；回调在同一 mutex 内遍历每个像素合成背景；`hm_try_copy_latest_frame` 又在该 mutex 内整帧复制。
- [UI 位图交付](../../src/HeyMelody.App/EncoFree4ModelView.cs)：`TryCopyDirect` 在 UI 线程锁位图并调用 native 复制；成功后触发视口重绘。

1050×910×4 的单帧原始缓冲约 3.82 MB（3.65 MiB）；42–45 次／秒意味着**每一道完整缓冲遍历／传递**涉及约 153–164 MiB／秒的数据量。这是尺寸推算，不是实测内存总线带宽，不能把各阶段简单相加成已测得的传输率。

首轮成功复制帧的缓存 timing：

| 场景 | native CPU 提交均值／P95，ms | 混合 readback 墙时均值／P95，ms |
| --- | ---: | ---: |
| Vulkan，自转 | 0.222／0.314 | 13.018／19.104 |
| Vulkan，滚动 | 0.265／0.398 | 13.083／18.642 |
| OpenGL，自转 | 0.219／0.284 | 16.159／23.430 |
| OpenGL，滚动 | 0.231／0.339 | 14.599／18.600 |

`render_milliseconds` 只计 CPU 的 beginFrame／render 提交；`readback_milliseconds` 从读回请求到完成回调中 CPU 合成结束，包含异步排队、GPU 工作／拷贝、回调调度、mutex 等待与 CPU 合成。两者**不是 GPU 纯渲染／纯 DMA 时间**。样本只覆盖最终被复制的帧，旧帧被覆盖／跳过时不在这个总体中。native 空闲／排空路径还有 `flushAndWait`，其单独代价未能用堆栈／队列 trace 隔离。

总 CPU 未占满 8 核，不代表 UI 线程、跨线程锁、GPU／CPU 同步或串行图像交付没有瓶颈；engine 利用率未满也不能把等待／带宽问题排除。当前数据不支持把低更新率简单解释为整机 CPU 算力耗尽或 GPU shader 满载。

### 2. 无新帧时仍锁位图

Vulkan 自转记录 1963 次位图锁、1249 次成功直接复制，约 36.37% 的锁没有新帧；OpenGL 为 1986／1103，约 44.46%。滚动时分别约为 4.70%／5.82%。

核对 Avalonia 12.1.4 固定 commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31` 的 `WriteableBitmapImpl`：解锁会无条件更新像素版本并使 snapshot 失效，下次实际 Draw 可能重建 snapshot。但本项目 `NoFrame` 分支没有调用 `InvalidateVisual`，所以**空锁不是一次即时 GPU 上传的同义词**。可确认这是多余工作，尚不能量化它占掉多少毫秒或带来多少 FPS 损失。

### 3. Mesa／D3D12 translation 与 GPU timing

Vulkan 与 OpenGL 的日志都指向 Mesa／D3D12 → Adreno，而 UI 自身是 ANGLE／D3D11。OpenGL 三个首轮场景记录相同 Microsoft vendor、D3D12 Adreno renderer 和 Mesa 4.2 版本，并启用 `dont_use_timer_query`。因此 OpenGL 没有 Vulkan 的相同 warning，不等于已获得正常的 GPU 计时；切换后端也没有绕开 D3D12 translation。两者仍使用 Adreno 硬件，不是 WARP。没有与同机可用的 Qualcomm 原生驱动作配对实验，不能量化 translation 独占的代价，更不能把全部差距归给 Mesa。

Vulkan 自转／滚动日志分别出现约 1409／1500 条 `Timestamps are not monotonically increasing`。固定版本上游 `VulkanDriver` 在已返回的 begin／end query 非单调时标记 ERROR，`FrameInfo` 丢弃该 GPU elapsed 样本；不是本项目默认 timestamp=0 直接造成，Renderer 会补 steady clock。

这证明**该组合的 GPU query 不可靠**，也有待量化的查询／日志开销；不证明硬件完全不支持时间戳，不证明报错直接强制某个帧率，也不足以锁定 Mesa、驱动或 Filament 中具体哪一层的 bug。Filament 动态分辨率默认关闭，本桥接未开启；不能把坏时间戳说成动态分辨率限帧原因。

### 4. 普通滚动与资源驻留不能忽略

模型冻结后仍保留约 668–731 MiB 工作集，但持续 CPU 接近静止 UI，说明“保留资源”和“不断渲染”是不同问题。模型＋滚动约 1.1–1.2 GiB，而无模型滚动也已约 836 MiB；既不能全部归咎于 3D 原始缓冲，也不能据 30 秒短测认定持续泄漏。

## GPU 计数器的交叉诊断

另用选定 PID 的 3 秒计数器采样做诊断，**不覆盖无采集器主表**。只有整个 counter 查询区间位于 App 测量窗口内的样本参与统计，排除预热／冻结边界污染。

| 场景 | App 3D engine 均值 | DWM 3D engine 均值 | App shared GPU memory 均值，MiB |
| --- | ---: | ---: | ---: |
| Vulkan，自转 | 39.21% | 11.51% | 394.6 |
| Vulkan，滚动 | 51.73% | 15.47% | 838.2 |
| OpenGL，滚动 | 53.28% | 14.17% | 817.1 |
| Vulkan，冻结 | 0% | 0.034% | 390.5 |

无模型硬件滚动的另一轮全量采集器记录 App 3D engine 约 16.45%、DWM 约 26.12%；静止接近 0。数字是特定 engine／PID 的观测，不是“全 GPU 使用率”，不同 engine 不跨类相加，也不据此分解 GPU 时间预算。进程工作集与 shared GPU memory 的口径不同，也不能直接相加成额外物理内存占用。

轻量采集器仍占约 2.9%–11.9% 单核 CPU；带采集器的模型位图交付约 49–52 次／秒，比首轮无采集器高约 17%–19%。这是顺序复测的差异，可能受调度／运行状态等影响，**不能解释成采集器使程序提速**。未锁定温度／频率，故不把单场小数视为硬件固定上限。

## 建议的优化顺序

本轮只给方向与验证条件，不实施；不恢复已移除的失焦／滚动自动降档，也不擅自更改用户的 30／60／无限制设置。

| 优先级 | 建议 | 依据／验收条件 |
| --- | --- | --- |
| 第一轮 | 拆开测量 readback callback 的排队、mutex 等待、CPU alpha 合成、整帧 memcpy、UI Lock／Draw／上传，并记录所有完成帧而不只 latest-copy 样本 | 目前已定位高成本路径，但不能精确分账；补齐可靠计时是后续优化验收前提，GPU timestamp 错误需单独处理 |
| 第一轮 | 优先试验模型目标像素上限／渲染比例，与无新帧空锁／交付调度改进；CPU 背景合成可另做 GPU／opaque 输出对照 | native 尺寸对交付和复制墙时影响明确；空锁和全帧 CPU 遍历已确认。须保持透明度、主题背景、generation、尺寸变化和退役安全，不承诺未测收益 |
| 第二轮 | 评估 Filament → Avalonia GPU 纹理共享／互操作，移除回读再上传的往返 | 架构性消除整帧传输；两后端及 D3D11／D3D12／Vulkan 同步、跨设备资源、窗口关闭和设备丢失恢复均需专项验收 |
| 配套 | 独立审计普通滚动重绘／缓存，做长时 working set、private bytes、GPU shared memory 与回收曲线 | 无模型滚动也已占较多资源；不能只优化 3D，不能把短时峰值直接称泄漏 |
| 配套 | MSAA 1／4、现有用户帧率上限、OpenGL／Vulkan 做配对画质／功耗实验 | 单关 MSAA 或换后端未根治；阴影本来已关闭。用户可自愿选较低帧率降低持续成本，但这不证明交付链路已修好 |

暂不建议换掉整个 Avalonia UI、强制软件绘制、仅减三角形数，或将 timestamp 报错简单静音后宣称修复。现有证据支持优先处理“大像素离屏结果如何交给 UI”，而不是对整个框架作无依据归因。

## 验证边界与证据

- Release／win-arm64 的 App.Tests 与 native-only 工具构建通过；App.Tests 只有既有 `CS0067` 警告。渲染适配器策略、原生 Demo CLI 契约、渲染设置、模型加载、动画、平滑滚动六组离线检查共 180 项通过，审计 CLI 缺参数按预期返回 2；这些不是所有产品测试／发布包门禁。
- 真实原生 Windows ARM64 窗口与 GPU 路径；Demo／离线数据，不是耳机协议／IPC／网络整体性能测试。没有发送设备写入。
- 未测其他耳机模型、其他缩放／窗口尺寸、长期热稳态、休眠恢复或真实输入拖动；程序滚动不覆盖 SmoothScroll 输入质量。
- WPR 初始无录制；尝试 CPU／GPU／DesktopComposition trace 时被系统策略拒绝（`0xc5585011`），之后确认无遗留录制。未改系统策略，不声称已获得 ETW GPU 队列或 CPU 调用栈。
- 原生 UI 操作工具的 native pipe 不可用，因此采用专用真实窗口 runner；没有借截图或回调计数冒充真实用户交互／屏显帧率。
- 完整输入、原始日志、逐样本、失败与排除分支、脚本、上游固定源码和 SHA256 保全到本机 [研究档案](../../research/rendering-performance-arm64-2026-11-07/README.md)，不随普通 clone 提供；原 `artifacts/performance-audit-20261107/` 保留。
- 审计入口见[开发指南](../development.md)，native-only 工具见 [HeyMelody.Rendering.Benchmark](../../tools/research/HeyMelody.Rendering.Benchmark/README.md)。后续工作登记在 [UI-04／UI-05／UI-15／UI-16](../optimization-backlog.md)，本轮没有实施这些产品优化。
