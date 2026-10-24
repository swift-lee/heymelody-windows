# 文档索引

当前版本为 **0.9.2**，本轮为 Windows 本地构建，尚未发布到 GitHub。初次使用先读项目 [README](../README.md)；参与开发或研究先读 [AGENTS.md](../AGENTS.md)，再按任务选择下列文档。无需一次展开所有链接。

## 用户说明

- [欢律单源与升级兼容](research/heymelody-only.md)：当前配置、资源、缓存迁移与历史证据边界。

- [使用指南](usage.md)：连接耳机、日常控制、更新、在线资源与常见问题。
- [界面说明](gui-notes.md)：设备页、设置、贴合度测试与黄金听感窗口的交互和已知限制。
- [设备选择与离线控件](device-state.md)：选择恢复、缓存展示、上次连接时间与重新连接刷新。
- [外观资源持久化与重新下载](artwork-persistence.md)：外观提示、颜色读取、缓存、动态 WebP 与“重新下载在线资源”。
- [三维模型渲染设置](model-rendering.md)：GPU 优先、软件渲染、0.9.0 的逐帧旋转与帧率设置、材质差异。
- [界面与渲染性能（0.9.0）](research/ui-performance-0.9.0.md)：界面重排、120 Hz 渲染预算、简体中文字体与验证范围。
- [界面性能与交互（0.9.9）](research/ui-performance-0.9.9.md)：区域脏矩形、动画让路、模型帧复制、GPU 缓存回收、EQ 滑块与设置非阻塞排队；Windows 帧率未测量。
- [3D 模型加载提速交接书](research/model-loading-handoff-2026-10-23.md)：交给 Windows 会话的托管与原生小改步骤；未实施、未计时。
- [主界面启动与首屏加载（0.9.9）](research/main-window-startup-0.9.9.md)：提前连接后台、先显示后台名单再核对、冷启动少一次目录枚举；Windows 耗时未测量。
- [黄金听感预览与声音设置](research/hearing-preview-settings.md)：0.8.13 的结果雷达、原厂音乐试听与频谱、游戏分组、BassWave 和完整固件显示。
- [发布包说明](../packaging/README.md)、[来源及许可](../packaging/SOURCE-NOTES.md)：发布包内容与第三方来源。

## 开发与验证

- [开发指南](development.md)：环境、构建、测试、界面验证与发布步骤。
- [仓库维护入口](../REPOSITORY.md)：当前维护状态与进一步阅读入口。
- [后台架构](backend-architecture.md)：托盘、共享 IPC、多设备监听、BLE 观测与退出生命周期。
- [日常控制层](daily-notes.md)：GET/SET、通知订阅、字段合并与结果证据。
- [CLI 工具](cli-tools.md)：只读查询与受控 Hi-Res 实验，使用前核对各命令边界。
- [研究工具](../tools/research/README.md)：资源提取、协议依据与离线验收。
- [优化待办](optimization-backlog.md)：分支按方向分轮优化的规则，以及各方向已完成与待处理的优化条目。
- [后台内存与能耗测量脚本](../tools/Measure-HostMemory.ps1)：只读，列出 Host 托盘、worker 与界面进程的私有工作集、CPU、优先级与电源节流等计数（仅 Windows）。

各文档中的静态分析、模拟测试、原生窗口检查和真实设备验证具有不同范围；已有离线结果不证明真实耳机广播、手机并发、休眠恢复或 ARM64 实机行为。

故障复现与日志收集见[诊断日志](diagnostics.md)，包含组件选择、一次性启用、日志目录和排障字段。

## 研究结果

完整专题与本地证据入口见 [研究索引](research/README.md)，档案与公开整理稿的职责见 [研究资料规范](research/organization.md)。以下入口便于按问题查阅。

- [后台内存审计](research/backend-memory-audit.md)：2026-10-21 后端内存方向的发现、已实施与暂缓项、Linux 测量结果；Windows 内存未测量。
- [后台能耗与 CPU](research/backend-energy.md)：2026-10-22 后端能耗方向的效率模式、按需 BLE 扫描、空闲唤醒与 Windows 待验证清单；Windows 行为未验证。
- [上下文读取风险排查](research/context-read-risk-2026-10-11.md)：大文件、超长单行与工具输出风险，后续读取和处理顺序；503 成因尚未确认。
- [黄金听感与贴合度重新研究](research/calibration-reassessment.md)、[组件修复](research/calibration-repair.md)：两 APK 与 X4 v116 的路由、滤波、停止／保存流程及实现验证。
- [游戏音效与 Hi-Res／空间音效](research/game-audio-spatial.md)：游戏开关和类型命令、包名、互斥方向与手机分支。
- [智能音量处理决定](research/auto-volume.md)：原厂用途与能力门控，以及 0.8.10 全机型隐藏的决定。
- [EQ 选择](research/equalizer-selection.md)、[右上通知提示](research/notification-header.md)、[日常功能编解码](research/daily-feature-codecs.md)：功能行为与协议依据。
- [ANC 主模式与提示音差异](research/anc-mode-prompt.md)：主模式父编号与自适应子档，提示音效果的待验证范围。
- [设备适配交接](research/device-adaptation-handoff.md)、[云端配置](research/cloud-device-configuration.md)、[云端美术](research/cloud-device-artwork.md)：复现入口、配置与资源的来源和限制。
- [X4 美术复核](research/enco-x4-artwork-audit.md)、[软件模型材质](research/software-model-materials.md)、[Free4 丹拿版模型](research/enco-free4-dynaudio-model.md)：官方资源、渲染和色号证据。
- [OppoPods 对照](research/oppopods-comparison-2026-10-08/README.md)、[蓝牙事件](research/bluetooth-events.md)：项目适配经验、通知与广播布局。
- [FluentAvalonia](research/fluent-avalonia.md)、[其他型号](research/oppo-device-support.md)、[跨平台](research/cross-platform.md)：调研结论与暂缓范围，不作支持承诺。

## 版本与历史

- [版本记录](history/release-notes.md)：历次版本说明与对应验证范围。
- [历史资料索引](history/README.md)：早期 CLI、WPF 与阶段记录。
- [Enco X4 0.8.1 实机验证](research/enco-x4-0.8.1.md)、[Free4 0.7.1 体验包](research/enco-free4-0.7.1.md)：对应版本的连接和模型验证。
- [X3 三维模型验证](research/enco-x3-3d-validation.md)、[外壳遮挡修复](research/enco-x3-opaque-occlusion.md)：早期独立实验。

除 Markdown 链接外，路径相对于仓库根目录。历史记录中的日期、版本、`reference/` 等原始路径和验证范围保留原义；新的复核另存记录。`research/` 链接指向本地完整档案，通常不随 Git clone 提供；公开结论、维护工具和正常构建不依赖这些档案随源码分发。
