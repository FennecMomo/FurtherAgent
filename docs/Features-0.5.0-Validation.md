# 0.5.0 剩余功能与实机验收

日期：2026-10-09。后台 CLI 验收，不打开 WPF 窗口、不操作用户桌面。客户端继续经 Cloudflare；测试 Host 走本机中继。

## 实现

| 功能 | 当前实现与范围 |
|---|---|
| 文件 | 共用 FileTransferClient，双向分块/SHA256/取消/受控目录，0.4.0 已完成真实 Agent 文件任务 |
| 图片 | 图片文件上传、剪贴板图片编码 PNG 上传、文件列表内下载后预览；不自动抓取远程 URL，不宣称 runs 原生视觉输入 |
| Markdown | WPF FlowDocument：标题、粗体、斜体、行内/围栏代码、列表、引用、管道表格、HTTP(S) 手动链接、可选择复制；HTML 保留纯文本，不执行脚本。未包含公式、Mermaid、完整 CommonMark 嵌套语法 |
| 过程详情 | ChatEntry 可选 Detail；Hermes 实际 tool.* / reasoning 事件字段及历史工具记录；Codex 快照中的推理摘要、工具调用/执行/文件变更原始字段。有界截断和折叠。只显示后端给出的数据；无事件不制造思考内容 |
| 归档 | Hermes 原生 PATCH archived + GET 读回；CLI archive/unarchive；WPF 归档按钮、恢复对话框，本机按服务地址/设备名保存恢复 ID。不会 DELETE |
| 模型/模式/权限 | 模型切换原生会话锁定；Hermes 当前 API 无逐会话权限/沙箱或 Plan，不提供伪开关。Codex 模型/Plan 保留既有实现，权限及原生归档仍未接通桌面 follower IPC |

Hermes 归档列表不支持枚举已归档会话，因此本机记录不是全量远端归档库，换电脑可手动输入 ID 恢复。归档可影响 Hermes 压缩会话的关联链，这是其原生语义。工具事件仅限本 Host 启动的 run，历史缺失/SSE 断流/Host 重启不保证还原完整过程；完成后的本轮工具事件在当前适配器内保留。

## 服务器接口证据

通过旧专用接入端运行只读源码核查，取得安装版 0.21.0 报告，未读取业务会话。下载报告 SHA256：C6006467FDD2A07BC2F2F9BA190AABF78AED49A0D8EE30FE00D59453BD5D3323。

- api_server_runs.py:483–497：runs 只解析文本 input，未调用多模态解析器。
- api_server.py:4455、4474–4475：PATCH 白名单支持 archived，布尔原生软归档。
- api_server.py:4254–4268：会话列表未暴露 include_archived / archived_only。
- api_server_runs.py:188–211：tool.started {tool,preview}；tool.completed {tool,duration,error}；reasoning.available {text}。
- 当前 HTTP API 未暴露逐会话权限、沙箱、Plan。不能把网关斜杠命令当成 HTTP 模式设置。

原始报告保留在忽略目录 artifacts/integration/files-live/contract-review.md，作为安装版证据，不将其他版本文档当成实测。

## 自动部署专用端

通过现有 CLI 上传仅含 Core/Host DLL 的 85,687 字节增量包，哈希 8C6AEAB6AC288B9B668C6D5E0D33DA15C2406EC73232A06C2E4DE52F555FF07B。由授权测试会话复制已验证 0.4.0 自包含运行库至新目录，再替换两个 DLL；不覆盖旧端，不依赖服务器 SDK。

- 新目录：/home/ubuntu/furtheragent-integration/host-0.5.0-integration
- 设备：Hermes-FA-Features-050
- 启动时 PID：3379449
- 独立文件根：/home/ubuntu/furtheragent-integration/files-0.5.0
- 测试会话：furtheragent_c91504c806034569bd6c77495e270950

授权码仅留在本地忽略目录中的专用部署结果文件，不写入此报告。旧测试端、中继、业务端、Hermes 网关、Tunnel 和防火墙未要求变更。

## 已验证

- 新版 Host 能力声明及后台新建会话成功。
- CLI 原生归档成功，重新拉取列表该 ID 消失；恢复后 ID 重现。
- 模型由 opencode-go / deepseek-v4.1-flash 切换为 deepseek-v4-flash，settings 读回一致，然后恢复原模型并再次读回一致。没有在临时模型下提交任务；不是对所有模型推理可用性的保证。
- 无窗口 WPF 文档测试：原生表格、粗体、安全链接、代码/HTML 纯文本、无远端图片自动加载，5 项通过。未做实际剪贴板和预览窗口交互测试。

- 上传 PNG 320x200 后，实际 tool.started/tool.completed 明确记录 vision_analyze，耗时 11.849 秒、error=false。视觉结果正确识别白底、左侧红方块、右侧蓝圆，不是仅凭 Agent 自述工具名称。
- Agent 用代码生成 response.png（200x100 RGB、绿底白圆），CLI 下载后 SHA256 为 9471FB61DFF13A2685183D6CA6464FBD1923FF212BC95E818D836780572944F3；Pillow 独立验证尺寸、模式、背景像素和圆心像素均正确。
- 完成后的会话快照含 24 条带详情记录，实际工具包含 vision_analyze、terminal、execute_code、write_file，包含耗时/error 字段和后端 reasoning.available 事件。
- 当前核心/协议自动化 112 项通过，加上 5 项无窗口 WPF 文档检查，共 117 项。没有把这些测试当作实际窗口交互验收。
- 真实停止：提交仅等待 20 秒的无文件副作用测试，CLI stop 接受后观察到该 run 为 cancelled、CanStop=false、CanSend=true。未测试危险命令，不能由此推断所有外部工具都能立即终止。

不能完成的原生能力：Hermes HTTP 会话权限/沙箱和 Plan 不存在；Codex 桌面 follower 的权限/归档入口未验证且未接通。审批流程自动化通过，但本次没有触发真实审批请求；不为了测审批去执行危险操作。当前业务 Host 未升级，要使用 0.5.0 新能力需连接新版专用 Host，或后续独立安排业务端升级。
