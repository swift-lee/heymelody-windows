# 研究工具

本目录维护项目复现、资源提取和离线验收需要的工具。核心协作与读取规则见 [AGENTS.md](../../AGENTS.md)，目录与原稿保全要求见 [研究资料规范](../../docs/research/organization.md)。完整研究对象、理解、结论、原始工具版本及运行产物保存在根目录 `research/`；整理后适宜公布的结果见 [研究索引](../../docs/research/README.md)。

| 入口 | 用途 |
| --- | --- |
| [HeyMelody.Rendering.Benchmark/](HeyMelody.Rendering.Benchmark/README.md) | Windows 原生 Filament 分辨率／MSAA／后端性能对照；高精度等待、实际请求频率与完整采样，不含 Avalonia 上传或屏幕呈现 |
| [Compare-PerformanceAudit.ps1](Compare-PerformanceAudit.ps1) | PowerShell 7.5+ 离线对比窗口审计 B/C 目录；重算逐次成功位图复制间隔、核对计数与健康，输出到新 JSON；不运行 GPU，位图交付 Hz 不是屏显 FPS |
| [filament-linux-build/](filament-linux-build/README.md) | Ubuntu 24.04 隔离构建、Linux Filament 失败复现与修复验收证据 |
| [ui-text-audit/](ui-text-audit/README.md) | C#/AXAML/配置文本候选、源码追踪、场景截图关联与人工审计 Excel；保留意见并记录覆盖缺口 |
| [earbud-artwork/scripts/](earbud-artwork/scripts/) | APK美术、Android资源、来源探测、图册与验证 |
| [online-whitelist/scripts/](online-whitelist/scripts/) | 配置认证、解密、版本分析及离线验证 |
| [online-artwork/scripts/](online-artwork/scripts/) | 模型获取、容器解析、身份/颜色审计及验证 |
| [accept_cloud_research.py](accept_cloud_research.py) | 对固定本地配置、模型、图册进行离线验收，默认新输出位于research/cloud-acceptance |
| [inspect_oppo_apk.py](inspect_oppo_apk.py) | APK来源与内置配置检查 |
| [event_dex_audit.py](event_dex_audit.py)、[anc_dex_audit.py](anc_dex_audit.py)、[equalizer_dex_audit.py](equalizer_dex_audit.py) | 固定APK中事件、ANC与EQ相关DEX依据 |
| [preset_name_audit.py](preset_name_audit.py) | 官方预设名称的资源依据 |
| [game_audio_spatial_audit.py](game_audio_spatial_audit.py) | 固定两APK与X4固件的游戏／Hi-Res／空间离线审计；保存原件、原DEX、callback原字节和8源配置矩阵，默认另建research输出 |
| [fetch_hearing_audition.ps1](fetch_hearing_audition.ps1) | 获取并核对官方黄金听感试听原件，离线转换 PCM16；原件、转换工具版本和摘要默认归档到 research/hearing-preview-20261010/audio |
| [fetch_earbud_firmware.py](fetch_earbud_firmware.py) | 按所选 APK 来源查询官方 OTA，在线需显式 `fetch --online`；离线检查/解包 Air4 Pro 的 legacy 分块 LZMA，保存逐块摘要并区分下载校验与未闭合的内部 CRC |
| [air4_pro_ghidra/](air4_pro_ghidra/README.md) | Air4 Pro 独立 ARM Thumb 镜像映射、完整已发现函数导出、定向索引及 0x0114/0x0105 字节重构；自动反编译和人工确认分别记录 |

## 当前采集与历史复核

当前配置、外观和固件采集仅使用欢律；`online-whitelist/scripts/fetch_whitelist.py`、`online-artwork/scripts/fetch_artwork.py` 及 `fetch_earbud_firmware.py` 不再提供组件来源。APK 美术提取也只接受欢律。构建资源生成使用 `tools/bundle_resources.py`，不会联网。

固定旧样本的审计入口现在只启动 `historical_replay.py`，从本地 `research/migrations/drop-coloros-2026-10-18/originals/` 加载按原路径保存的原字节。完整文件大小与 SHA256 见该档案的 `preservation-manifest.json`。该路径用于保留历史双来源依据，不表示当前应用仍支持组件来源；普通构建及应用测试不依赖这些档案。

历史复核禁止联网、设备操作和覆盖已有证据。传入原工具的离线参数，并使用新的 `research/` 输出路径；缺少本地归档时明确失败，不以当前单源结果冒充旧双源验收。七份已跟踪静态契约保持原位置与原字节。

## 使用与输出

按任务选择工具和固定输入版本，不批量读取整个研究目录。审计结果可能包含大量 DEX 指令、资源数据或状态快照：优先使用脚本已有的输出文件选项；没有该选项时，将完整结果重定向到新的 `research/<专题>/` 文件，终端仅回传路径、尺寸、摘要和必要结论。读取结果前先测量字节、字符和最长行，按键、方法或指定范围查询；大型结果先由 subagent 做有界预览。具体阈值与已知风险入口见 [读取风险报告](../../docs/research/context-read-risk-2026-10-11.md)。

新运行保留输入来源与摘要、工具版本、命令、完整输出及失败分支，不覆盖历史 run；向模型回传的正文、并行工具结果和 subagent 摘要均需遵守 `AGENTS.md` 的累计预算。

21个专题脚本原先位于 `docs/research/<专题>/scripts/`，2026-10-09迁到此处，旧路径解析与专题依赖同步更新。原字节版本位于本地 `research/<专题>/archive-2026-10-09/scripts/`，用于保留研究历史；运行入口使用此处维护版本。

旧脚本路径的运行兼容指向维护版本，不保证修改后的代码仍匹配旧manifest中的脚本摘要。核对旧脚本字节时使用完整档案中的归档版本；历史JSON中的原始path/hash不改写。

常规构建及离线应用测试不需要原始APK或完整研究目录。上述专题复现需要本地输入及已有Python依赖；在线获取仍需显式 `--online`，本轮整理不重新请求厂商资源。已有JSON、manifest和实测记录中的采样路径保持原值。
