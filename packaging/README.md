# HeyMelody Windows 使用说明

排障时可在“应用设置 → 故障诊断 → 诊断日志”选择组件，启用“在下次启动程序时记录日志”并保存，然后退出托盘后台再重新启动。首次默认全选五个组件；`HeyMelody.Host` 包含蓝牙通信和 worker。日志只持续该轮后台运行，下一轮恢复关闭。点击“打开日志目录”可打开 `%LOCALAPPDATA%\HeyMelody\logs`；该按钮不会开启记录。复现后正常退出托盘后台，再收集对应周期文件夹。日志包含完整协议、设备标识、本地路径和异常，分享前自行检查；旧记录需自行删除。

普通界面默认使用 Windows 的默认 GPU。“应用设置 → 外观 → 使用 Basic Render 兼容模式”可启用兼容绘制，可能降低流畅度；保存后手动关闭并重新打开所有界面窗口生效，托盘后台无需退出，不影响三维模型 API 设置。如果界面完全透明或黑屏而无法操作，关闭界面后将 `%LOCALAPPDATA%\HeyMelody\preferences.json` 的 `uiBasicRenderMode` 改为 `true`（保留其他字段），再打开界面；文件不存在时可创建 `{"uiBasicRenderMode":true}`。切换前先正常结束或取消正在进行的校准。

如使用本地生成的 Inno Setup 安装器，程序只为当前用户安装，默认目录为 `%LOCALAPPDATA%\Programs\HeyMelody`，目录页允许自定义，无需管理员权限。最后一页的“创建桌面快捷方式”首次默认勾选，以后保留上次选择，包括取消勾选。开始菜单只添加一个 HeyMelody 主程序入口，不创建子目录；可从 Windows“应用和功能”卸载。卸载保留用户设置和已下载资源，只清理安装清单列出的程序文件。更新前关闭所有界面并从托盘退出后台，安装器检测到程序仍在运行时会提示退出。安装 EXE 由独立本地脚本生成，GitHub Actions 继续提供目录和 ZIP，实际下载内容以发布页为准。

Inno Setup 安装器识别到旧 Qualcomm `ACPI\QCOM043A` GPU 家族（Adreno 680、685、690）时，将 `uiBasicRenderMode` 设为 `true`；8cx Gen 3 的 `QCOM0636`、Snapdragon X 及未识别设备不修改此设置。其他偏好字段保留；偏好文件损坏时安装器报错并保留原文件，不会静默覆盖。来源、匹配规则和验证范围见[安装器说明](https://github.com/CberYellowstone/heymelody-windows/blob/review/0.9.9/docs/installer.md)。

本 Filament 试验提供 Windows x64、ARM64 单架构包，请选择与系统对应的版本；各架构的实际验证范围以匹配文件摘要的本地验收收据为准。三维模型默认使用 Vulkan，可在应用设置的“3D模型渲染API”下拉框选择 OpenGL，保存后立即生效。无法使用 Vulkan 时显示静态外观并提示手动选择 OpenGL，不自动切换；没有同款图片的演示显示通用耳机图标。帧率滑块提供 30帧、60帧、无限制，默认无限制；旧跟随显示器和试验版 120 帧设置对应无限制。请保留原生库、`hm_filament_probe.exe`、`filament-runtime.json`、`materials` 和 `licenses`。

这是采用 Avalonia 和 FluentAvalonia 的社区 Windows 耳机客户端。资源目录使用欢律的 85 个耳机产品 ID；配置、协议与实际设备能力共同决定可用功能，未知控制不会开放。OPPO、OnePlus、realme 耳机目录已纳入，音箱排除。

完整解压后双击 **`Start-HeyMelody.cmd`**，启动器选择 x64、x86 或 ARM64 目录中的程序。发布包自带 .NET 运行时，无须安装 SDK；单架构包仅包含所选架构。先在 Windows 中配对并连接耳机，随后通过设备管理器保存名单。管理器、贴合度测试和黄金听感向导分别为 `HeyMelody.DeviceManager.exe`、`HeyMelody.FitTest.exe`、`HeyMelody.HearingSetup.exe`，从主界面打开，共享托盘后台。

检查 Demo：在主界面长按底部设备添加键“+”满 2 秒，添加并选中 `Demo · Enco Free4 丹拿版`。重复长按仍为同一台，短击仍打开 Windows 蓝牙设置。Demo 加载全部已实现主界面控件，可展开设置并在本地调整开关、选项、Slider、文本和 EQ；测试按钮只显示演示反馈。关闭主窗口后 Demo 自动移除，不影响真实设备。没有已配置耳机时，双击 **`Start-3D-Demo.cmd`** 可直接进入同一离线 Demo，不需要白名单或网络。模型默认 GPU 优先，支持拖动旋转、惯性及空闲自转。

应用仅使用欢律配置与美术，默认开启在线资源。自定义欢律密钥留空时使用预置密钥。旧设置中的欢律密钥、在线开关和缓存继续使用；旧组件来源的配置、美术和功能快照退出使用。关闭在线资源后不发起网络请求，保留已下载的有效新版。更新先校验新包、切换引用，再删除旧在线版本；软件预装资源不清理。设置和缓存加密密钥以用户本地 JSON 明文保存。

降噪模式由圆形按钮选择，下方显示细分选项；均衡器频段、预设和可保存方案数由设备决定。修改自定义 EQ 后选择“保存并启用”。耳塞贴合度测试需要双耳正确佩戴。黄金听感下拉框选择本机向导保存及耳机当前完整读回的方案；没有方案时先打开设置向导。校准中可以取消；断连、摘下耳机、关闭窗口等情况会停止测试。

关闭主界面后后台继续运行，右键托盘可打开主界面或退出。直接启动 `backend/HeyMelody.Host.exe` 只启动后台。本版本不添加登录自启或开盖弹窗。Demo 的合成控件集合只供布局检查，不代表丹拿版实机能力。

`ResourceBundles`、各程序 DLL、原生渲染库、`backend` 和 `licenses` 都是运行所需文件，请保留完整目录。资源来自用户提供的欢律 APK、已验证的官方资源包和单独提供的 X4 图片，版权及源摘要见 `SOURCE-NOTES.md`、`PACKAGE-NOTICES.json`、`ResourceBundles/provenance.json` 和 `licenses/upstream/Extracted-Earphone-Resources-NOTICE.txt`。

模拟协议、界面测试及三架构静态检查不证明所有型号实机、手机并发或 ARM64 硬件行为。包内 `manifest.json` 仅记录构建信息；完整默认验收的实际结果以本轮保全的构建日志为准，包结构、版本与文件 SHA256 校验见 `release` 中与包目录同级、匹配版本的静态验证收据。只有与版本、文件摘要匹配的执行记录才能证明对应包的实机验证。
