# 研究资料索引

> 当前客户端仅使用欢律配置与资源，见[当前单源规则](heymelody-only.md)。下文涉及已退出支持来源的覆盖、接口和操作要求仅用于历史研究复核，已由当前规则取代；原版本、路径、摘要和验证范围保留。

本目录是公开研究结果的入口。协作规则和按任务阅读顺序以根目录 [AGENTS.md](../../AGENTS.md) 为准；完整档案、原稿保全和公开稿要求见 [研究资料规范](organization.md)。完整研究对象、理解、结论及产物保存在仓库根 `research/`，维护中的复现工具见 [tools/research/](../../tools/research/README.md)，其原始版本及运行产物仍保留在完整研究档案。

下面的 `research/` 链接是本机完整档案入口，通常不随普通 Git clone 提供；没有这些原件仍可阅读公开结论并正常构建。当前完整档案目录由本地 [research/README.md](../../research/README.md) 索引。先根据任务选一个专题，不自动展开全部原件；读取尺寸与输出预算遵循 [上下文读取风险报告](context-read-risk-2026-10-11.md) 和 `AGENTS.md`。

2026-10-09 黄金听感与贴合度的完整重新研究按本轮要求存放在 [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md)，这里保存 [受版本管理的纠正摘要](calibration-reassessment.md) 和 [后续组件修复](calibration-repair.md)。旧 [校准实现记录](calibration-codecs.md) 保留为修复前基线，不再作为当前实现或原厂完整协议证明。

## 公开研究结果

继续设备适配时先读[当前单源规则](heymelody-only.md)，再按需要查阅历史[设备适配交接](device-adaptation-handoff.md)、[云配置](cloud-device-configuration.md)和[云美术](cloud-device-artwork.md)。当前用户说明、开发与历史入口见 [docs/README.md](../README.md)。

| 文档入口 | 内容 |
| --- | --- |
| [本机 ARM64 UI 卡顿调查](ui-lag-arm64-2026-10-23.md) | 预构建的默认 WARP 选卡、Adreno 单变量对照、X4 WebP 持续更新成本，以及回调节拍和真实上屏 FPS 的验证边界 |
| [macOS RFCOMM 控制连接修复（2026-11-05）](macos-rfcomm-connectivity-2026-11-05.md) | macOS 12+ 带 UUID 过滤的 SDP 查询为空操作、缓存端点解析、5 秒超时、TCC 首次授权与 Enco X4 真机日志 |
| [Avalonia Adreno 规则历史](avalonia-adreno-history-2026-10-23.md) | 透明窗口故障、过滤规则引入、社区驱动修复报告、维护者保留默认限制的理由与显式选卡接口历史 |
| [Enco Air4 Pro 控制连接核查](enco-air4-pro-control.md) | 两来源配置与原 APK SPP 路由、官方 v125 OTA 镜像、0x0114 长度与外帧兼容修复、具体失败诊断和离线验证边界 |
| [蓝牙音频编码可行性审计](bluetooth-audio-codec.md) | 参考工具采集方式、普通权限边界与遗留实现撤回；研究结论，功能未纳入 0.9.2 |
| [降噪区域的通透人声与模式名称核查](anc-transparency-controls.md) | X2／X3 强化人声开关、四型号独立聆听、分层模式名称与原 App 控件证据；本轮局部验证边界 |
| [界面审计与渲染性能（0.9.0）](ui-performance-0.9.0.md) | 四个界面的重排、120 Hz 预算与逐项优化、简体中文字体与嵌入字体内存问题、Linux 验证范围与 Windows 验收方法 |
| [界面性能与交互（0.9.9）](ui-performance-0.9.9.md) | 2026-10-22 界面方向：区域脏矩形、界面操作时装饰动画让路到 30 fps、模型帧直写位图、Lottie 末帧缓存、GPU 缓存回收、EQ 滑块裁切与设置非阻塞排队；反编译核实与 Linux 测试，Windows 帧率未测量 |
| [3D 模型加载提速交接书](model-loading-handoff-2026-10-23.md) | 2026-10-23：模型加载链路、静态判断的耗时排序（探测子进程、Engine、贴图、首帧、原生 SHA-256）、先计时再改的托管与原生小改步骤、Windows 构建验证与验收；未实施、未计时 |
| [主界面启动与首屏加载（0.9.9）](main-window-startup-0.9.9.md) | 2026-10-23 启动方向：提前连接、互斥体探针、先显示后台名单再核对、加入托盘首次目录刷新、降噪图标路径移出 UI 线程；Linux 测试，Windows 耗时未测量 |
| [后台内存审计](backend-memory-audit.md) | 2026-10-21 后端内存方向的问题、发现、已实施与暂缓项、Linux 测量结果和未验证内容；Windows 内存未测量，其他方向的待办见[优化待办](../optimization-backlog.md) |
| [后台能耗与 CPU](backend-energy.md) | 2026-10-22 后端能耗方向：无界面时的效率模式（EcoQoS 加 IDLE）、BLE 按需扫描、空闲唤醒、写盘与 IPC 改动、Linux 测试结果和 Windows 待验证清单；Windows 行为未验证 |
| [上下文读取风险排查](context-read-risk-2026-10-11.md) | 文档、源码、研究产物的字符与长行风险，subagent 预览规则和后续处理顺序；503 成因尚未确认 |
| [黄金听感预览与声音设置](hearing-preview-settings.md) | 0.8.13 结果雷达、两种试听的动态左右耳曲线、原厂音乐频谱、游戏分组、BassWave 和完整固件显示 |
| [游戏音效与 Hi-Res／空间音效重新研究](game-audio-spatial.md) | 两 APK 原 DEX、X4 原始固件、游戏开关／类型命令纠正、包名与互斥方向、Hi-Res旧版／V2手机分支 |
| [智能音量调节（原“自动音量”）](auto-volume.md) | 原厂用途与能力门控、17款配置机型、X4 v116静态/实机验证；0.8.10全机型隐藏的决定与右上提示修复 |
| [黄金听感与贴合度重新研究](calibration-reassessment.md) | 两 APK 独立复核、X4 v116 交叉验证、UI路由/filter等纠正与完整研究入口 |
| [黄金听感与贴合度组件修复](calibration-repair.md) | 现有组件改动、当前协议与界面行为、回归验证及验证边界 |
| [EQ 选择](equalizer-selection.md)、[日常功能编解码](daily-feature-codecs.md) | EQ 选择和日常协议的实现依据与验证边界 |
| [右上通知提示](notification-header.md) | 通知状态、当前提示行为与协议状态的区分 |
| [X4 v116追加实测](../../research/calibration-reassessment-20261009/LIVE-TEST.md) | 双耳贴合度良好，12项测听/滤波/保存/读回，旧公开字段恢复；失败、重试与研究UI音量纠错 |
| [earbud-artwork/](earbud-artwork/README.md) | 2D／功能美术的来源、角色与桌面适配结论 |
| [online-whitelist/](online-whitelist/README.md) | 内置／云配置、认证与版本选择结论 |
| [online-artwork/](online-artwork/README.md) | 云模型、格式、材质与验证范围 |
| [Enco X4 美术复核](enco-x4-artwork-audit.md) | 官方动态 WebP、操作 Lottie 角色与帧合成验证 |
| [软件模型材质研究](software-model-materials.md) | 0.8.5 软件材质代表值与光照修正、固定依赖证据和限制 |
| [X3 白色遮挡修复与黑色回归](enco-x3-white-opaque-occlusion.md) | 0.9.2 白色 GLB 精确摘要补匹配、两款材质审计、原生遮挡复测入口与验证边界 |
| [Free4 身份与色号审计](online-artwork/free4-identity-color-audit.md) | 产品身份、色号与请求链审计结论 |
| [3D 验证](3d-validation/README.md) | 历史实验工作树与验证入口 |
| [OppoPods 对照研究](oppopods-comparison-2026-10-08/README.md) | 两个项目的官方资料使用程度、协议经验与局限 |
| [ANC 主模式与提示音](anc-mode-prompt.md)、[蓝牙事件](bluetooth-events.md) | 主模式编号、通知和广播布局；保留对应验证范围 |
| [其他型号](oppo-device-support.md)、[跨平台](cross-platform.md)、[FluentAvalonia](fluent-avalonia.md) | 支持范围调查、桌面框架与暂缓方向 |

## 原件与证据

| 本地目录 | 内容 |
| --- | --- |
| [research/backend-memory-20261021/](../../research/backend-memory-20261021/README.md) | 后端内存审计的完整发现、基线与逐步结果、IPC 源生成与 GC 预算探针（含原始数据） |
| [research/backend-energy-20261022/](../../research/backend-energy-20261022/README.md) | 后端能耗的范围与决定、探索发现、设计、基线与分步结果日志（`baseline/`、`host-step/`、`after/`）、未闭合问题 |
| [research/ui-perf-0.9.0-20261011/](../../research/ui-perf-0.9.0-20261011/README.md) | 0.9.0 审计与设计记录、上游 API 核对、字体内存实验程序与日志、基线和最终测试日志、改动前文档原稿与 SHA256 |
| [research/ui-fixes-0.9.1-20261011/](../../research/ui-fixes-0.9.1-20261011/README.md) | 0.9.1 四项主窗口修复的根因证据（截图像素统计、FluentAvalonia ComboBox 主题出处、Host 暂停语义）、修改前文档原稿与 SHA256、测试日志与截图 |
| [research/context-read-audit-2026-10-11/](../../research/context-read-audit-2026-10-11/README.md) | 读取风险的完整测量脚本、元数据／字符／摘要清单、subagent 预览和校验结果；大清单按路径查询 |
| [research/documentation-entrypoints-2026-10-11/](../../research/documentation-entrypoints-2026-10-11/README.md) | 本轮文档入口整理前的原文、SHA256 清单、修改范围与核查记录 |
| [research/hearing-preview-20261010/](../../research/hearing-preview-20261010/README.md) | 结果截图、完整源码与 APK 保全、计算推导、音频原件及转换、实现与验证证据 |
| [research/apps/](../../research/apps/) | 欢律 APK、OPPO 第一方无线耳机组件 APK |
| [research/game-audio-spatial-20261010/](../../research/game-audio-spatial-20261010/README.md) | 两包与X4固件原件、字节保全、完整过程／失败判断、原DEX指令和8源配置矩阵 |
| [research/calibration-reassessment-20261009/](../../research/calibration-reassessment-20261009/README.md) | 重新研究全过程、专题报告、原DEX/fallback、反编译、固件指令与配置矩阵 |
| [research/OPPO_Enco_X4_v116_reconstruction/](../../research/OPPO_Enco_X4_v116_reconstruction/README_zh.md) | 用户提供的可信X4 v116固件重建包；本轮保留不修改 |
| [research/installers/](../../research/installers/) | Surface MSIXbundle 等研究参考安装包 |
| [research/screenshots/](../../research/screenshots/) | 界面参考截图、用户提供的 X4 PNG 原件 |
| `research/calibration-wizard-design-20261009/README.md` | 历史记录引用的 ColorOS 黄金听感向导原件入口；2026-10-11 核查时该 README 缺失。公开设计说明见 [界面说明](../gui-notes.md)，本轮未补齐或重建原件 |
| [research/upstreams/](../../research/upstreams/) | OppoPods1、OppoPods2、SteamTools 完整源码副本及 Git 元数据 |
| [research/earbud-artwork/](../../research/earbud-artwork/) | 2D 原图、功能素材、图册、预览、库存和完整证据 |
| [research/online-whitelist/](../../research/online-whitelist/) | 厂商配置、下载记录、静态契约、完整 run |
| [research/online-artwork/](../../research/online-artwork/) | ZIP、VFXMS、GLB、KTX、模型预览、完整 run 与审计证据 |
| [research/3d-validation/](../../research/3d-validation/) | 实验 worktree、依赖、NuGet cache、模型材质审计和验证输出 |
| [research/enco-x3-opaque-20261013/](../../research/enco-x3-opaque-20261013/) | X3 白／黑原始模型、两来源配色查询、材质审计与本轮遮挡回归完整证据 |
| [research/history/](../../research/history/) | 公开历史文档整理前的完整记录 |
| [research/oppopods-comparison-2026-10-08/](../../research/oppopods-comparison-2026-10-08/README.md) | 完整对照报告、逐项勘证、机器快照；源码在upstreams |
| [research/evidence-archive-2026-10-09/](../../research/evidence-archive-2026-10-09/) | 早期APK、白名单、模型及归档验收证据的原字节副本 |
| [research/migrations/](../../research/migrations/) | 历史盘点、原稿归档及本轮整理清单 |

`docs/` 的公开整理稿与 `tools/research/` 的维护工具随源码管理；`research/` 继续忽略新加入的完整档案，七份原先已跟踪的静态契约保留原位置与跟踪状态。完整报告、证据和研究对象均保留原文；公开稿从完整资料整理而来，不把个人数据、本机操作流水或完整机器快照直接列为公开结果。

## 路径与使用

2026-10-09 整理将原件按类型放回 `research/apps/`、`installers/`、`screenshots/` 和 `upstreams/`，完整报告、勘证、脚本原版与研究证据归档到 `research/`；公开专题和历史文档另作整理，21 个项目复现脚本迁到 `tools/research/<专题>/scripts/`。位置及保全记录见 [整理记录](organization.md)。2026-10-11 的文档入口调整另存档案，不覆盖此前记录。

现行命令与 Markdown 链接已更新。历史 JSON、manifest、第三方源码及实验记录里的原路径保留，研究工具读取时兼容旧路径。实验 worktree 注册位置恢复为 `research/3d-validation/worktree`，原分支与工作树内容保留。

从仓库根运行已有离线验收：

```powershell
python -B -X utf8 tools/research/accept_cloud_research.py
```

新研究验收输出另存 `research/cloud-acceptance/`，不覆盖历史记录。常规构建和应用运行使用 `src/` 中的资源包；继续研究或重新提取资源才需要本地完整研究对象。
