# 六项功能可行性排查（2026-10-09）

更新：以下保留初始排查记录。0.5.0 已实现 Markdown、图片文件交互、过程详情和 Hermes 原生归档，真实图片/归档/模型联调与仍受限项目见 [0.5.0 验收](Features-0.5.0-Validation.md)，不要将下文的历史待办当作最新状态。

## 结论与难度

| 功能 | 可落地部分 | 难点 / 待确认 | 难度 |
| --- | --- | --- | --- |
| 双向图片 | WPF 图片选择/粘贴/预览；Codex 原生 image/localImage 输入；通过 B 中转远端输出图片 | Hermes 安装版 /v1/runs 对 content 数组的处理待查，不能把其他兼容接口能力当成 runs 能力；模型须支持视觉，输出取决于 Agent 工具返回 | 中等 |
| 双向文件 | 文件选择、上传、受控暂存、结果下载、SHA256 校验；B 可把收到的文件路径提供给 Agent | 不能发送 A 本地路径当 B 的路径。需要独立分块传输、限额、取消、过期清理，不能把大文件塞进聊天快照。传输成功不等于任意格式都能被模型直接读取 | 中高 |
| 访问权限 | Codex 已观察到的桌面设置接口具备权限参数路径，可扩展只读/工作区/完全访问等真实策略 | 必须读回实际生效权限并尊重远端约束。Hermes 没有确认到逐会话权限切换契约；关闭 UI 工具按钮/修改提示词不构成只读沙箱；不能修改共享网关全局权限来伪装会话级控制 | 高，Hermes 最不确定 |
| 思考/执行/工具详情 | 展示公开推理摘要、步骤、工具参数/结果、状态、耗时、退出码、文件 diff；现有协议已丢弃部分可呈现数据，可补 | 不保证模型提供推理内容；Hermes 安装版事件字段待真实 run 验证，且仅控制本接入端 run。大输出需分页、折叠、增量保留，SSE 断流不能伪造完整过程 | 中等 |
| Markdown | 标题、列表、链接、引用、表格、代码块，后续代码高亮 | 客户端即可做。禁用任意 HTML/脚本；远端本地图片/文件链接需用附件通道转换。公式、Mermaid 可单列后续范围 | 低，优先实现 |
| 会话归档 | Codex 原生 App Server 有 archive/unarchive 和 archived 筛选；Hermes 当前主分支 PATCH 支持 archived | FurtherAgent 连接的是桌面 follower IPC，未确认归档转发入口；Hermes 已安装版本是否支持 archived 需核对 PATCH 白名单及列表过滤/恢复。不能用 DELETE 代替归档；仅本地隐藏须单独标注 | 中等，依赖版本/同步 |

## 证据分级

### 当前代码与本地桌面源码确认

- Core ChatEntry 仅 Role/Text；桌面发送 attachments=[]；WPF 只读 TextBox 显示。需要扩展结构化消息，而非只改按钮。
- Core/Wire.cs Frames.MaxBytes 为 32 MiB，WebSocketTransport 全量序列化。双向附件需要新传输设计，尤其避免阻塞消息/审批。
- 已安装桌面缓存 bootstrap-Dz9A8y86.js：thread-follower-update-thread-settings 调用 updateThreadSettingsForNextTurn / updateThreadPermissions；另有 archiveConversation / unarchiveConversation，但未证明可通过当前 follower 命名管道直接调用。
- 不另启一个写入同一桌面会话的 CLI Agent。新增 FurtherAgent.Cli 是中继控制端，仍走 B 的原适配器。

### 官方文档确认（不能直接等同当前桌面适配已接通）

[Codex App Server 文档](https://learn.chatgpt.com/docs/app-server)：image/localImage、sandboxPolicy、推理与工具事件、thread/archive/unarchive 都有原生协议支持。它证明底层能力存在；桌面协同 IPC 的兼容与同步仍需单独验证。

### Hermes 证据限制

服务器已有报告确认路由含 POST /v1/artifacts/upload、GET /v1/artifacts/download/{id}、PATCH /api/sessions/{id}。但路由存在不证明通用附件/归档可用。

本地 artifacts/inspection/hermes 缓存来自官方 main，**不是服务器安装版精确源码**：
- artifact route 受 browser_control 开关约束，上传返回 receipt 而非通用文件路径；下载是一次性消费，不能盲目复用为会话文件存储。
- main 的 PATCH 接受 archived，而该信息尚未在服务器 0.21.0 实测。
- 需要服务器返回安装版相关方法与字段证据，不能据 main 升级现有服务器结论。

## 本轮真实联调

公网 /health 返回 200、version=0.2.0。旧授权码可用；只读诊断完成 WebSocket 配对、25 条会话计数及历史计数，并经过 35 秒观察后再次读取适配器成功。没有向业务会话发送消息。

用户随后要求不占用桌面：专用 WPF 已正常关闭，后续全部用 FurtherAgent.Cli 后台执行。真实公网 CLI probe 于北京时间 2026-10-09 13:24–13:25 完成：agents 正常、session-count=25、35 秒观察结束后 heartbeat connected=true，退出码 0。自动化 69 项通过（含回环中继 + 模拟 Hermes + CLI 命令）；这不等于真实 Agent 写入已通过。服务端旧 Host 未返回 CanCreate/CanConfigure 字段，不以此声称新功能已联调完成。

## 后续验收顺序

1. 独立 Hermes 测试 Host（同中继、同 loopback API、不同设备名与授权码，不重启业务 Host/网关）。只在专用新会话写入。
2. 先用 CLI 建空会话、发无工具短文本、核对历史与事件，确认基本回路。
3. 核对服务器安装版附件、归档、权限与事件契约。
4. Markdown 和结构化过程详情先实现；图片/文件共享一套附件通道；归档按原生能力实现；权限没有可靠后端约束时保持不可选。

本轮是可行性排查、CLI 基础与联调准备，不宣称六项功能已实现。
