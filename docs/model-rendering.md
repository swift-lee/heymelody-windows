# 三维耳机模型渲染设置

## 当前 Filament 实验分支

本分支将耳机三维渲染迁移至 Google Filament，界面继续使用 Avalonia 12.1.4 和 FluentAvalonia。两份本地官方样本（欢律 17.6.5、ColorOS 无线耳机 17.6.3）都通过 OPLUS VFX ModelViewer 1.3.8 的 Android 封装调用 Filament/gltfio；1.3.8 是封装版本，不是已确认的 Filament 上游版本。本项目使用固定上游源码自行构建 Windows 原生桥，不分发 Android SDK 或 JNI 二进制。

三维模型默认使用 Vulkan，在应用设置的“3D模型渲染API”下拉框选择 OpenGL；说明为“若存在渲染问题，请尝试切换到 OpenGL”。两种模式均保留原始模型、完整 PBR 贴图、拖动、惯性、自转和隐藏／最小化暂停，按控件实际尺寸与系统 DPI 渲染。“3D模型渲染帧率”使用 30帧、60帧、无限制三档吸附滑块，默认无限制；保存后立即应用，取消不改变设置。旧跟随显示器和试验版 120 帧设置对应无限制，原有 30、60 帧选择保留。30、60 帧限帧按真实时间累计并保留余量；无限制档不设额外上限，每个 Avalonia 动画回调最多请求一帧，停顿后不追赶补帧。实际帧率还受窗口回调频率和渲染耗时限制。切换后端保留已验证字节与相机运动状态，单独改变帧率不重建 Engine。

Vulkan 不可用时显示静态 detail 外观并提示手动选择 OpenGL，不自动选择 OpenGL 或修改偏好；OpenGL 不可用时显示普通不可用提示。没有同款图片的内置演示使用通用耳机图标。损坏资源和缺失组件单独归类，不提示无效的后端切换。

原生 Engine 初始化时按所选后端的实际能力选择深度纹理格式：优先使用 `DEPTH24`，不支持时使用 `DEPTH32F`；两者均不支持时返回受控初始化失败，沿用现有静态外观与错误提示。原生日志 `depthFormat.selection` 记录后端、两种格式的支持情况和最终使用的格式，便于定位深度纹理创建失败。

2026-10-25 的 Radeon 680M／驱动 31.0.22013.1007 [本地用户日志](../research/rdna2-novulkan-logs/)来自版本 `0cabb0b`：Vulkan 已识别 AMD 设备并报告 feature level 3，随后原先硬编码的 `DEPTH24`（Filament 格式编号 22）被拒绝，三次 probe 均以 `0xC0000409` 结束；同周期 OpenGL probe 和模型加载成功。此次修改针对这个深度格式能力差异；新产物在该显卡与驱动上的实机回测仍待用户确认。

当前运行验收范围为 Windows x64 的 Vulkan、OpenGL；ARM64 为交叉构建和静态校验。macOS Metal、Linux Vulkan／EGLHeadless 的构建准备、ABI 2、探测程序及验证边界见 [Filament 构建与接口](filament-renderer.md)。完整截图留在本地，只检查本轮变更的模型和设置区域。

帧复制与让路：`PresentLatestFrame` 从 `ApplyCamera` 等请求新帧（`_frameRequested`）起轮询渲染器，直到已取到帧且距最后一次请求满 0.25 s 才停止，因为锁定 `WriteableBitmap` 即使不写入也会使 Skia 快照失效并触发一次纹理上传；连续请求可能重叠，先取到的帧未必是最后姿态，宽限期保证停止转动时最后一帧仍会呈现；位图尺寸与配置相同且 `RowBytes == 宽×4` 时，渲染器直接写入已锁定的位图，托管 `_frameBuffer` 逐行复制只作为步幅或尺寸不符、渲染器拒绝较小目标缓冲时的回退（`DirectCopyDisabled`）。`AnimationYield`（`HeyMelody.Artwork`）在设置展开（脉冲 600 ms）、滚动（300 ms）和主窗口失活期间要求装饰动画降到 30 fps：空闲自转（`Motion.Phase == Idle`）改用独立的 30 fps `ModelFramePacer`，`Motion.Step` 仍逐回调推进以保持自转时间连续；拖动和惯性仍用用户设定的帧率。Lottie 同样只在 30 fps 步进变化时重绘；停在末帧的 Product 动画在有 GPU 上下文时只渲染一次并以 `SKImage` 重绘，循环动画从不缓存。这些都是离线验证，30 fps 让路的实际流畅度与 GPU 缓存效果需 Windows 实机确认。

## 模型加载、预读与计时

加载开始时先持有资源租约并启动 GLB 读取／校验，与旧渲染器退役、生命周期门等待及新 Engine 创建重叠；调用 `LoadModel` 前等待字节准备完成。Engine 创建继续受生命周期门与退役任务约束；worker 返回至 UI 接管间的既有窄窗见[BUG-08](optimization-backlog.md)，该项仅静态登记、未实测复现。已完成的读取若失败，不再创建 Engine；较晚发生的读取错误释放已创建的渲染器。取消或加载代次过期后仍观察读取任务并保持租约直到读取结束，过期结果不发布。

已知 SHA-256 的并发读取合并为一个在途任务，完成后只保留容量为 **2** 的已校验模型 LRU；命中时不重新读取或校验，同一摘要不额外保留第二份模型字节。任务结束后清除在途条目，失败仍可重试。无摘要的资源不按路径推定身份、缓存或合并。`DeviceVisualCatalog` 仅在当前选中 hero 解析得到 `DeviceModelVisual` 后预读；未选中设备的美术解析继续，但不提前读取模型或启动探测。切换到已完成的模型 slot 时按内容代次与后端去重准备，getter 只读取状态。排队前从该外观取得独立租约，读取结束释放；预读错误由真正附加的视图报告。同一时机按当前渲染后端提前准备运行库与探测，不创建进程内 Engine，不在应用启动时或非模型设备上无条件准备。

后端探测在当前进程内按运行库身份与后端共享，一个 key 同时只有一个探测子进程。单个视图取消只结束自己的等待；共享任务仍完成或由自身 30 秒期限终止。只有成功结果缓存；失败可重试。运行错误使成功缓存失效；若旧代探测尚未退出，后来者先等待它结束，再共享新代探测，旧结果不能恢复失效缓存。`PrepareBackendAsync` 与工厂 `Create` 各自重新核对当前 DLL、probe 和 manifest 身份，提前准备不替代实际创建前的完整性检查。接口及原生 SHA 实现见 [Filament 构建与接口](filament-renderer.md)。

Ctrl+Shift+F12 或 `HEYMELODY_RENDER_DIAGNOSTICS=1` 开启诊断叠加层，并在每次成功加载首帧后输出一行结构化 `Trace.WriteLine`。新增“加载 … ms（探测 …／引擎 …／模型 …／首帧 …）”行；完整记录另含 attach 到加载开始、退役等待、门等待、读取／校验及缓存命中、运行库身份准备、探测状态（Cached／Started／Joined）。首个加载总耗时从控件附加开始，到匹配当前尺寸的首帧写入位图并隐藏 ProgressRing；后续同一附加期间的重载从该次加载开始计时。首帧阶段从 `LoadModel` 完成开始；重叠阶段不能相加推算总耗时。200 ms 延迟出现的 ProgressRing 及首帧隐藏行为不变。默认关闭时不为诊断创建额外文本。

需要正常产品窗口的本地 JSONL 时，启动前将 `HEYMELODY_MODEL_LOAD_TRACE` 设为**已有目录中的新绝对文件名**。该独立 opt-in 输出一行 session 元数据及每个成功首帧的一行 load，包含进程、模型摘要和阶段计时；以 `CreateNew` 打开，不覆盖旧证据，未完成或失败的加载不形成成功首帧记录。它不要求打开叠加层，默认不创建日志。例如：

```powershell
$env:HEYMELODY_MODEL_LOAD_TRACE = [IO.Path]::GetFullPath('artifacts/model-load-trace.jsonl')
.\src\HeyMelody.App\bin\Release\net10.0\HeyMelody.exe
Remove-Item Env:\HEYMELODY_MODEL_LOAD_TRACE
```

`HeyMelody.exe --model-loading-benchmark` 使用真实应用可执行文件、Filament 与 Win32 窗口，但只运行隔离的 hero 模型控件场景：每个进程一次冷附加，再切到非模型外观并重新附加作热加载。它不经过设备选择、Catalog 或美术 RPC，不在线下载，也不代表完整产品启动或完整主窗口的稳定私有工作集验收。`--model` 支持 `free4`、`dynaudio`、`file`，文件模式另给 `--file`；`--backend` 选择 `vulkan` 或 `opengl`，`--hot-runs` 为 5–30，`--output-dir` 必须是新目录或空目录。

批量入口 [Measure-ModelLoading.ps1](../tools/Measure-ModelLoading.ps1) 每个冷样本重启真实 exe，默认 Vulkan／OpenGL、每组 5 个冷进程及每进程 5 次热加载，保存原始收据、二进制／模型摘要与各阶段中位数、最大值。默认模型组包含 online，须传入由当前欢律资源服务取得并已校验的 `-OnlineModelFile`；仅测内置模型时明确限制 `-Models`。前后对比必须使用各自独立目录，不能混用二进制或模型摘要不同的样本。示例：

```powershell
pwsh -NoProfile -Command "& ./tools/Measure-ModelLoading.ps1 -OutputRoot research/model-loading-2026-10-23/local-run -Models free4,dynaudio"
```

本轮 `HeyMelody.Rendering.Filament.Tests` 已通过 108 项可移植检查，覆盖共享探测加入、取消、失效换代、重试及 GLB 完整性；该入口未加载原生库、GPU、UI 或蓝牙。性能收益、当前原生 SHA、窗口画面与完整主窗口内存的验证范围以对应实测记录为准，不由这些便携检查推定。

## Ava3D 历史实现与验证

下文保留迁移前版本及原始验证范围，不描述当前 Filament 行为；尤其软件三维回退和 Ava3D 的照明参数仅适用于当时实现。

0.9.0 取消三维模型的固定帧率。0.8.x 用 1/30 s 的 `DispatcherTimer` 驱动旋转，并把 Ava3D 的 `MaxFrameRate` 设为空闲 30、拖动 60；Win32 计时器约 15.6 ms 的粒度还会让帧间隔不均匀。现在改用 `TopLevel.RequestAnimationFrame` 的逐帧回调，与显示器的合成节拍对齐，`MaxFrameRate` 为 0（不设上限），`RenderTrigger` 仍为 `OnDemand`：每个显示帧推进一次已按时间积分的 `ModelOrbitMotion`，只有姿态变化时才 `InvalidateCamera`，高回报率鼠标的多次移动在下一帧合并应用。隐藏、最小化或卸载时循环停止，`AnimationActive` 表示循环是否在运行。

应用设置新增“三维模型帧率”：跟随显示器（默认）、60 帧或 30 帧，保存在 `preferences.json` 的 `modelFrameRate`（`display`／`60`／`30`，缺失或未知值按跟随显示器）。GPU 后端按所选间隔均匀跳帧；CPU 后端仍保持 0.8.x 的空闲 30 帧，拖动时至多 60 帧，并在单帧渲染耗时较长时按 1.3 倍渲染时间继续放慢，因为 CPU 渲染发生在整窗的帧内，过快会拖慢其他动画。4×MSAA、阴影贴图与光照参数不变。

重新附加模型（切换设备或布局后）复用最近两份已校验的 GLB 字节，不再重复读取与 SHA-256；每个视图仍解析自己的独立场景。软件材质副本改为第一次实际使用 CPU 后端时才在后台生成，GPU 模式不再解码和采样全部贴图。设 `HEYMELODY_RENDER_DIAGNOSTICS=1` 或按 Ctrl+Shift+F12 可显示帧率／帧时间叠加层，模型左上角另显示 Ava3D 的帧率、P95 帧时间、渲染耗时、后端、MSAA 与迟到帧；只有画面在动时数字才有意义。0.9.0 的这些改动已通过 Linux 无头测试与运动检查，**尚未在 Windows 显示器上实测 120 Hz**，完整依据见[界面与渲染性能](research/ui-performance-0.9.0.md)。

Enco X3 白色模型的半透明外壳问题沿用已验证的 Free4 方案修复：导入后，仅对 SHA256 匹配的已知不透明资产，将材质 `1001` 设为 `Opaque` 并开启深度写入。白色 GLB 原先未命中已有规则；黑色 GLB 已被覆盖，本轮补充其回归。修正规则统一放在内部 `ModelMaterialCorrections`，模型与贴图原件不改写，也不按型号名称全局覆盖透明材质。本轮仍为 0.9.2；静态依据、外部模型复测命令与验收范围见 [X3 白色遮挡修复与黑色回归](research/enco-x3-white-opaque-occlusion.md)。

0.8.6 恢复默认 GPU 优先：没有偏好、只有主题的旧文件、损坏 JSON 或非布尔值时开启“三维模型 GPU 加速”，请求 Ava3D 的 `Automatic` 后端。已保存的明确 `false` 继续使用软件；已保存的 `true` 继续开启。GPU 不可用时的自动软件回退及 0.8.5 材质／照明修正保留。

0.8.5 修正软件后端的模型过亮与曲面层次不足。固定版本的 CPU 渲染器仅逐像素读取基础色贴图，原模型中由其他贴图调节的金属度、粗糙度和发光系数因此被当作均匀参数使用。应用在每个网格的实际 UV 上采样，并按三角形世界表面积加权估算这些贴图的代表值，放入独立的软件材质副本，并降低软件环境光与直接光，保留曲面明暗。

软件副本保留原基础色贴图，模型和贴图文件不改写。GPU 使用原始材质引用与原有光照；`Automatic` 实际退回 CPU 时也应用软件方案。该方案不增加逐像素法线、AO 或完整材质贴图渲染，视觉效果仍与 GPU 和手机端不同，具体依据见 [软件模型材质研究](research/software-model-materials.md)。

代表值采样使用最近邻，不承诺与 `Texture.Filter` 的线性过滤一致；显式 `Srgb` 数据 MR 图的 gamma2.2 转换也只是固定依赖近似，可能不同于标准分段曲线。当前官方模型的 Auto MR 仍按线性数据读取。

当前软件光照为 `EnvironmentLight.Studio(.06)`、方向 `normalize(-4,-2,-1)`、直接光强度 `1.5`、ambient `.02`。GPU 保留 Studio `1.2`、方向 `normalize(-1,-2,-3)`、直接光 `2`、ambient `.15`。较弱的环境光配合方向光保留曲面明暗，避免将照明颜色提前推至饱和。

0.8.4 新增应用设置中的“三维模型 GPU 加速”，在 0.8.4／0.8.5 默认关闭，0.8.6 起默认开启。关闭时，所有耳机模型（包括在线 GLB、普通 Free4 和丹拿版演示）明确使用 Ava3D 的 `RenderBackendKind.Software`，保留三维显示、自动旋转和鼠标拖动。已保存的软件选择仍在控件附加到窗口前应用。

开启后使用 Ava3D 的 `Automatic` 选择，优先使用当前平台提供的 GPU 后端；平台不提供 GPU 时仍可退回软件渲染。设置只在点击“保存”后应用，并在当前模型的下一帧生效；取消不会启动 GPU 后端。新建、切换设备和重新附加的模型均使用已保存选择，无需重启。该开关控制耳机三维模型，与 Avalonia 的普通界面合成独立。

主题与此偏好共用 `%LOCALAPPDATA%/HeyMelody/preferences.json`，键为 `modelGpuAcceleration`。0.8.6 只有明确布尔值 `false` 按关闭处理；缺失或无效值使用开启的默认值。保存通过同目录临时文件替换，保留已有主题与其他字段；主题单独更新也不会清除 GPU 选择。

软件渲染使用同一官方 GLB，无须借用其他设备图片。Ava3D 12.1.0-preview.16 的随包文档说明，软件后端按顶点照明，仅采样基础色贴图且没有边缘抗锯齿，因此外观和性能可能与 GPU 模式不同。接口依据为该固定版本的 `Ava3D.xml` 中 `Ava3DView.PreferredBackend` / `RenderBackendKind.Software`，以及随包 `README.md`；上游入口为 [Ava3DControl](https://github.com/pavel-zheltiakov/Ava3DControl)。

针对性验证：

```powershell
dotnet run --project tests/HeyMelody.App.Tests -- --output-dir artifacts/gpu-ui-validation
dotnet run --project tests/HeyMelody.ModelPreview.Tests -- --native-software-smoke --output-dir artifacts/gpu-software-validation
```

前者检查旧偏好升级、安全默认、保存/取消、当前与后续模型、重新附加以及无关偏好保留。后者在独立 Win32 测试窗口中核对实际 `Software` 后端、丹拿版的 20 个 mesh / 94,927 个三角形、完整模型像素、旋转与卸载；产出渲染 PNG 和 JSON 收据。测试不调用蓝牙、不下载资源、不写用户偏好。ARM64 / 骁龙 8cx Gen 3 的实机兼容性仍需在该硬件复测。

0.8.4 的历史验证：2026-10-09 本机 Windows x64 软件渲染验证通过 125 项（115 项运动检查和 10 项原生软件检查）。实际后端为 `Software`、renderer 为 `Skia`，源模型保留 20 个 mesh 和 94,927 个三角形；CPU 渲染诊断计数为当帧背面裁切后约 48,145 个三角形，不应直接套用 GPU 提交全部三角形的计数。640×580 渲染图有完整可见耳机模型；旋转、停止、卸载和窗口关闭正常。这些检查没有覆盖材质明暗质量，不能作为 0.8.5 修正的验证。Avalonia 界面合成仍可以使用 GPU，这与模型选择软件渲染并不冲突。

0.8.5 历史 Windows x64 原生软件检查通过 151 项，收据与五张截图保存在 [software-lighting-085-20261009b](../artifacts/software-lighting-085-20261009b/report.json)。实际后端为 `Software`，丹拿版保留 20 个 mesh、94,927 个源三角形，四张原 PNG 摘要及软件／GPU 材质引用往返恢复均通过；材质往返没有初始化 GPU 设备。固定前盒区域的亮度 P90−P10 为 `18.2126`，旧平面图为 `5`；左右改光使约 `38.429%` 的模型内部像素变化，移除基础色贴图使约 `97.398%` 变化，证明光照与基础色实际影响图像。浅色／深色截图均已目检，曲面与耳机内托可辨。

这些原生截图检查对象是丹拿版；普通 Free4 与黑色 X3 另以离线真实 Preview 目检，不能称作三款都完成原生验证。X3 仍偏暗，受 CPU 先计算照明再乘基础色的近似限制。PNG 摘要证明法线图被加载，不证明 CPU 逐像素使用法线图。

发布后将 x64 `HeyMelody.dll` 与 `Ava3D.dll` 加载到独立 model harness，再次通过 [151 项原生检查](../artifacts/software-lighting-085-package-20261009/report.json)，程序集摘要与发布文件匹配，得到相同的亮度和贴图／光照对照结果。这验证发布的 GUI／renderer 程序集在模型测试窗口中的行为，没有完整启动客户端 exe、Host 或蓝牙。

0.8.5 的 13 个默认 runner、三架构发布及两套静态校验均完成，见 [构建记录](../REPOSITORY.md)、[PowerShell 收据](../release/HeyMelody_Windows_0.8.5_STATIC-VALIDATION.json) 与 [Python 收据](../artifacts/release-085-20261009/static-python.json)。ARM64 与骁龙 8cx Gen 3 仍未实机验证。

0.8.6 的 App 回归继续覆盖 GPU 默认开启、明确 `false` 的保留、保存／取消及当前／新建／重新附加模型。独立 [原生演示检查](../artifacts/native-demo-086-agent-20261009a/native-demo-receipts.json) 通过 40 项，以未写入的临时偏好隔离用户设置：请求 `Automatic`，本机实际使用 OpenGL GPU，丹拿版保留 20 个 mesh、94,927 个三角形，并捕获浅色／深色与宽窄窗口的四张真实模型帧。另行 [原生软件检查](../artifacts/software-lighting-086-20261009/report.json) 显式关闭 GPU，通过 151 项，前盒亮度跨度、改光和移除基础色的对照值与 0.8.5 相同。这两项是 Windows x64 源码测试窗口验证，不是 ARM64、真实耳机或本轮发布客户端完整执行记录。
