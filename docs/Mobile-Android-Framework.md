# FurtherAgent Android 客户端框架选型

记录日期：2026-10-10。本文保留初次技术选型依据；用户随后授权制作首版，现已采用 MAUI Android 单目标，实际实现与验证范围见 [Android 首版说明](Android-0.9.0-Validation.md)。下文的工具链与未验证项属于选型时快照。

## 1 已确认的约束与首选建议

用户明确：momo-chat 与 HermesApp **只作为功能点参考，不作为开发约束**。新客户端只考虑 Android，不考虑 iOS。选型先满足 Android 平台完整使用所需能力，再选择本工程开发维护最方便的方案；旧代码复用率不是独立目标。

**首选建议：.NET MAUI，C#，仅构建 Android；以当前 .NET 10 工程为基础评估。** 理由是 FurtherAgent 的控制端连接、RPC、会话增量同步和文件传输已有 C# 实现，可以减少另一套协议实现及长期同步成本。这是结合现有源码得出的工程判断，不是宣称 MAUI 对所有 Android 项目最方便，也不是手机实机验证已经通过。

UI 重新按 Android 使用方式设计，不移植 WPF XAML，也不照搬 HermesApp/Flutter 或 momo-chat/HTML。采用 MAUI 控件与必要的 Android 原生实现；Markdown 的具体渲染组件另作小范围验证，不提前将整套 UI 固定为 WebView 或 Blazor Hybrid。

MAUI 提供 C#/XAML 控件，并允许直接调用平台 API；单目标 Android 可以使用其 Android 支持，无需为 iOS 维护业务或平台实现。[Microsoft MAUI 概述](https://learn.microsoft.com/en-us/dotnet/maui/what-is-maui?view=net-maui-10.0)

## 2 候选比较

| 方案 | Android 能力与接入方式 | 对 FurtherAgent 的成本判断 | 当前结论 |
| --- | --- | --- | --- |
| .NET MAUI，仅 Android | 通用控件和平台集成 API；缺少封装的能力可在 Android 层实现 | 控制端纯 .NET 逻辑有共享基础；新写手机 UI 与平台适配；无需另维护 Dart/Kotlin 协议副本 | **首选建议**，先验证关键 Android 场景 |
| Kotlin + Jetpack Compose | Android 官方原生 UI 工具，直接使用 Android 生态 | 平台接入直接；需要用 Kotlin 实现现有 RPC、同步与文件协议，并维护跨语言一致性 | Android 深度交互若在 MAUI 下出现明显阻碍时的备选 |
| Flutter | Android UI 与平台插件；需要时以平台通道调用 Kotlin/Java | 新写 Dart 协议客户端和移动状态逻辑；旧 HermesApp 不给选型加权 | 能满足需求，但本工程当前没有比 C# 共享更明确的便利依据 |

Compose 是 Android 官方当前推荐的 UI 方向；Flutter 提供平台通道接原生代码。两者都具备 Android 开发能力，表中的优先级来自本工程维护成本判断，而非功能上无法实现。[Android Compose 说明](https://developer.android.com/develop/ui/compose/first?hl=en)、[Flutter 平台通道](https://docs.flutter.dev/platform-integration/platform-channels)

不为比较数量额外引入 Web 外壳方案：当前目标包含文件、凭据、通知和前后台状态，只有聊天网页能显示并不能代表完整 Android 客户端。也不因只做 Android 就排除可单独面向 Android 的框架。

## 3 本工程代码能共享到哪里

本次核对的 Core 为 `net10.0`，无 WPF 项目引用，但同时包含控制端和接入端职责。需要按职责抽取或隔离，不能把整个 Core 不加判断地当作“已通过 Android 验证”的库。

| 现有代码 | 处理方向 | 尚需验证或适配 |
| --- | --- | --- |
| `Contracts.cs` | 共享协议数据模型与能力标志 | Android 发布裁剪后的 JSON 序列化、后端版本兼容 |
| `Wire.cs` 的 RpcPeer、`WebSocketTransport.cs` | 共享 RPC 请求关联、收发和取消逻辑 | Android TLS、网络切换、休眠后半开连接及 UI 调度 |
| `RegistryClient.cs`、`RelayConnection.cs` 的 RemoteConnection | 共享配对、控制端连接与重连语义 | 生命周期触发与恢复订阅；控制端和同文件 Host 逻辑分离 |
| `SessionSync.cs`、`ConnectionHeartbeat.cs`、`SessionActions.cs` | 共享增量基线校验、心跳与操作条件 | 进程重启后重建快照；所有结果未知操作不自动重放 |
| `FileTransfer.cs` 的 FileTransferClient | 共享分块协议与 SHA256 校验 | 当前上传/下载以文件路径为入口，Android 文档 URI 需转为应用缓存文件或改为流接口；输出经系统保存/分享 |
| WPF 页面和 Windows 设置保存 | 重新实现 Android UI 与存储 | WPF XAML 不直接复用，Windows DPAPI 不用于 Android |
| CodexDesktopAdapter、HermesAdapter、DshAdapter、DshBackgroundRuntime | 留在 B/Host 端 | 手机不连接桌面命名管道，不启动 DSH 后台，也不持有 Hermes 后端密钥 |

表内文件均在 `src/FurtherAgent.Core/`，WPF 部分在 `src/FurtherAgent.Client/`。当前 `FileTransferStore` 与客户端也在同一文件内，后续可按必要范围拆分；本次没有执行拆分。

本机已有 .NET SDK `10.0.401`，`dotnet workload list` 当前无已安装工作负载。选择 MAUI 仍需准备 Android/MAUI 构建工具链；已有 .NET SDK 不等于 Android 环境就绪。这里没有声称 Flutter 或 Kotlin 工具链是否已经安装。

## 4 Android 完整使用的能力核对

| 必要能力 | 拟采用路径 | 完整可用的验收条件 |
| --- | --- | --- |
| 中继配对、会话、发送/停止/审批 | 共享控制端协议 + Android UI | 同一后端会话一致；按能力开关操作；错误和未知结果可辨认 |
| 富文本、代码、图片和长历史 | MAUI 列表与适合的正文渲染方案 | 中文输入、复制、表格/代码横向阅读、长输出与流式滚动实机可用 |
| 文件/相册选择、下载与分享 | FilePicker/媒体接口 + Android 文档与分享接口 | 不依赖手机可见的真实文件路径；取消、权限拒绝、传输校验、保存可用 |
| 配对信息持久化 | 普通设置与 SecureStorage 分开 | 敏感配对信息不写日志；重装/备份恢复失败有明确重新配对流程 |
| 返回键、键盘、安全区、旋转 | MAUI 页面生命周期与 Android 特定处理 | 输入框不被遮挡，返回操作不误退出或丢草稿，状态恢复准确 |
| 弱网和前后台恢复 | 生命周期事件 + 重建连接和完整快照 | Wi-Fi/移动网络切换、锁屏、杀进程重开后不重复提交任务 |
| 通知和后台交互 | Android 通知 API；按实际需求选择推送或符合系统要求的后台机制 | 后台可收到所要求的通知；点击进入正确会话，审批已处理状态可同步 |
| 语音输入 | Android 语音能力或可选转写服务 | 权限、语言、可用服务和识别失败处理明确；先转草稿再发送 |
| 安装、更新与发布 | Android 签名 APK/AAB，按实际分发渠道选择 | 正式签名、升级保留配置、目标设备可安装启动与更新 |

MAUI 官方提供文件选择和安全存储 API，但文件选择结果不能假设有可直接访问的 FullPath，应通过流读取；这与当前 FileTransferClient 的路径入口正是需要适配的地方。[文件选择](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/storage/file-picker?view=net-maui-10.0)、[安全存储](https://learn.microsoft.com/dotnet/maui/platform-integration/storage/secure-storage)

通知与后台恢复不是换框架即可自动完成的功能。MAUI 可接 Android 原生通知，后台执行仍受 Android 的生命周期与任务规则约束。客户端进入后台不应承担继续运行远端 Agent 的职责；远端任务在 B 继续，手机恢复后重新读取。只有本地通知代码不等于锁屏后必然收到远端完成事件，需要单独设计事件送达方案，并考虑目标设备是否具备相应推送服务。[MAUI 本地通知](https://learn.microsoft.com/en-us/dotnet/maui/platform-integration/local-notifications?view=net-maui-10.0)、[Android 后台任务](https://developer.android.com/develop/background-work/background-tasks)

## 5 后续实施前的验证门槛

用户尚未要求开始开发。后续若进入开发，先验证一个最小 Android 控制端：真实配对、读取会话、接收增量、发送确认、文件选择上传与下载、长消息渲染、前后台重连和结果未知不重发。随后再扩展完整界面。

若这些关键场景在 MAUI 中出现必须长期绕过框架、明显损害体验的问题，再评估 Kotlin + Compose；不因已经写了一点界面就坚持不合适方案。框架名不是验收结果，“Android 完整使用”最终以目标手机上的端到端结果为准。

最低 Android 版本、设备厂商范围、安装分发方式和后台通知要求仍需在需求细化时确定；不要求兼容 iOS，也不为其预留额外开发工作。当前建议不构成已经安装环境、创建工程或通过实机测试的声明。
