# FurtherAgent 手机端旧项目功能盘点

记录日期：2026-10-10。用途：收集旧项目功能，为 FurtherAgent 手机端需求讨论提供依据。本轮仅分析和记录，没有启动手机端开发、改动业务代码或部署服务。

**两个仓库已拉取并完成源码功能盘点，共收集 37 项功能或原型条目。它们仅作为功能点参考，不构成新客户端的架构、框架、界面或实现约束，也不要求复用其代码。目标平台已明确为 Android，不考虑 iOS。按 Android 功能完整性与本工程开发维护成本比较后的首选建议见 [Android 框架选型](Mobile-Android-Framework.md)。**

## 1 调查基线与完成范围

| 对象 | 本次基线 | 完成范围 |
| --- | --- | --- |
| [momo-chat](https://gitee.com/fennecmomo/momo-chat) | `master`，`2b615adc021c3cbc69e22fc227759ce46faaf723`，提交时间 2026-10-09 22:46:35 +08:00 | 已完整克隆并阅读全部 7 个功能及模板文件；另有 `.gitignore`。源码静态核对，未运行旧服务 |
| [HermesApp](https://gitee.com/fennecmomo/HermesApp) | `master`，`93975a7b5bec76e084092baa50f3a58f7307ba2c`，提交时间 2026-05-21 22:15:53 +08:00；应用版本 `0.1.0+1` | 已拉取并阅读全部 7 个业务 Dart 文件、依赖配置、Android/iOS 主要工程配置、README 和测试；静态分析，未编译或连接旧服务 |
| FurtherAgent | `0.8.2`，`f2c1dbbb835fa0f8dd5aa6c51abcdaea7b080497` | 阅读当前 Core 协议、适配器、同步和文件传输代码，对照现有文档；没有重跑真实 Agent 联调 |

两个旧仓库均已检出到 `artifacts/inspection/mobile-legacy/`，由工程既有 `.gitignore` 排除，不纳入 FurtherAgent 源码，也没有执行旧仓库的脚本。下文路径以各自仓库根目录为基准，函数名用于在固定提交中定位。

状态口径：“实现”表示在当前源码中找到执行路径，不代表本次运行验收通过；“部分”表示入口禁用、链路缺失或外部依赖未提供；“模板”表示文字规则，不能视作已接入的功能。

## 2 momo-chat 功能清单

该仓库是 Python Flask 服务，HTML、CSS、JavaScript 集中在 `templates.py`，SQLite 存储聊天与应用数据，Python WebSocket 客户端连接外部 Agent Service。不是独立的 Android 或 iOS 原生工程。

| 编号 | 功能点 | 源码与实现状态 | 手机端可利用部分 |
| --- | --- | --- | --- |
| M01 | 用户识别与登录保护 | **部分**。`app.py:index` 和聊天路由使用 `auth.shared`；认证模块在仓库外，固定添加 `/home/ubuntu/dev-projects` 导入路径 | 借鉴用户与会话分层；不把旧认证接入视为可直接复用 |
| M02 | 会话列表、新建、切换 | **实现**。`app.py:api_conversations/api_new_conv/api_get_conv`，`templates.py:loadConversations/newChat/loadChat` | 小屏历史抽屉、新会话入口；数据接 FurtherAgent 原生会话 |
| M03 | 会话标题、排序与时间 | **实现**。`db.py:save_message/get_conversations`；第一条用户内容前 30 字自动作标题，按更新时间排序，气泡显示时间 | 列表信息结构可保留；标题以远端会话为准 |
| M04 | 删除会话与消息级联删除 | **实现**。`db.py:delete_conversation` 验证用户名，SQLite 外键级联；前端有确认 | 仅收集需求。FurtherAgent 原生归档不等同删除，不照搬删除语义 |
| M05 | 手机布局 | **实现**。`templates.py` 使用 `100dvh`、600px 断点、侧边抽屉与遮罩、固定输入区 | 可提取布局交互参考；键盘、安全区、缩放与触控仍需专项验证 |
| M06 | 文本输入与发送体验 | **实现**。自动增高输入框至 120px，Enter 发送、Shift+Enter 换行，发送中禁用按钮，思考动效，失败提示 | 转为手机明确发送按钮、草稿保留、提交结果状态；不能只用一个 busy 状态 |
| M07 | 消息角色气泡与历史渲染 | **实现**。`renderMsg/loadChat`，用户与助手区分，历史加载后渲染正文并滚动 | 可保留角色区分、时间、历史阅读结构 |
| M08 | Markdown 与图片链接 | **实现但简易**。`renderMarkdown` 含标题、代码、强调、删除线、引用、列表、分隔线、链接、图片及基础表格 | 功能集可复用为需求；不直接复用正则渲染器。图片 URL 展示不等于文件上传 |
| M09 | 后台生成与会话状态 | **实现但受限**。`app.py:api_chat` 启动 daemon 线程，写 SQLite 占位消息；前端每秒读取消息和状态 | 保留“离开界面后远端任务继续”的目标；旧线程不是可靠持久任务队列 |
| M10 | 输出增量与工具过程提示 | **部分**。`api.py:agent_chat_stream` 处理 status、token、tool_call、tool_exec、tool_result、done；`app.py` 保存最近 3 行状态 | 保留思考、工具和执行状态展示；当前收集器先收完再 yield，不能算端到端实时流式 |
| M11 | 进度百分比与打字机 | **部分**。`showProgress` 从 `【进度 X%】` 正文标记提取；`typewriterAppend` 有定义但无调用 | 可借鉴进度呈现；正文标记不是执行引擎的真实完成比例，未调用动画不能算已启用 |
| M12 | 语音输入与转写 | **部分且入口关闭**。麦克风按钮移除，`toggleMic` 直接返回；保留 `MediaRecorder`、`/api/voice` 和 `stt.py:transcribe` | 适合作为后续语音输入原型。后端依赖 Linux Hermes venv 和 faster-whisper；非完整可用功能 |
| M13 | 网页应用生成与托管 | **实现框架，生成依赖外部 Agent**。`api.py` 提示词及 write_file 工具将 HTML 写入用户项目；`app.py:serve_app` 暴露 `/apps/<username>/<project>/` | 值得保留“任务产出可打开应用”的产品方向；是否托管、是否公开需另定 |
| M14 | 应用完成卡片 | **实现**。`templates.py:pollMessages/renderLinkCard` 在完成正文中识别 `/apps/` 链接并生成卡片 | 可推广成文件、图片、网页链接等产物入口；应由结构化产物数据驱动 |
| M15 | 按应用保存数据 | **部分，有明确缺陷**。`db.py` 用 `(username, project, key)` 唯一键做 upsert；`app.py` 有 save/load/list 路由，但 save/load 使用的 `json` 未在模块作用域导入 | 收集为应用数据存储需求；不能按现成可用服务迁移 |
| M16 | 用户偏好与长期记忆 | **实现**。`api.py:_read_memory/_write_memory/_build_memory_prompt` 使用 `USER.md`、`MEMORY.md`，支持增删替换并注入后续请求 | 作为可选能力，归属远端 Agent；不能在手机客户端另建一套隐式覆盖各 Agent 的记忆 |
| M17 | 技能创建、修补和目录 | **实现**。`_write_skill/_read_skill/_list_skills`，`skill_manage` 工具；提示词注入最多 10 个技能的名称与描述 | 可收集技能浏览与管理需求；各 Agent 的存储与执行格式不同 |
| M18 | 项目开发与自改进模板 | **模板**。`skill_templates/project-dev.md`、`self-improving.md`；未找到安装到用户技能目录的调用 | 可提炼操作规范；不能声称两个模板自动生效 |
| M19 | 文件写入、带行号读取、搜索、定点替换 | **实现**。`api.py:_execute_tool` 含 write_file/read_file/search_files/patch；工作目录按用户划分 | 属于远端执行能力。手机只提供状态和产物入口，不在手机重做执行器 |
| M20 | 让 Agent 回顾历史会话 | **实现但归属校验不足**。list_conversations 按用户列出最多 10 项；get_conversation 按 ID 读历史，每条截取 500 字 | 可收集跨会话检索需求；当前 FurtherAgent 协议未提供统一的此类 Agent 工具 |
| M21 | 自定义工具双向调用 | **实现框架**。WebSocket 收到 tool_exec 后在 Flask 进程执行工具，再发 tool_result | 可理解旧后端组织方式；不能和 FurtherAgent 中继 RPC 或 Hermes 原生 HTTP API 混用 |

## 3 HermesApp 功能清单

HermesApp 是 Flutter/Dart 客户端，包含 Android 和 iOS 工程目录。根 README 声明 Flutter 3.44+，`pubspec.yaml` 声明 Dart `^3.12.0`；业务依赖只有 Flutter、http 和 cupertino_icons。页面仅聊天、设置两种，没有内置 Hermes Agent 或 MCP Bridge 服务端。以下路径简写均相对 `hermes_app/`。

| 编号 | 功能点 | 源码与实现状态 | 手机端可利用部分 |
| --- | --- | --- | --- |
| H01 | 移动应用入口与页面导航 | **实现**。`lib/main.dart:HermesApp`，`chat_screen.dart:_openSettings`，MaterialApp + Navigator | 若选 Flutter，可改造应用壳和聊天/设置导航；尚不等于确定技术选型 |
| H02 | 系统深浅主题 | **实现**。`main.dart` 配置 Material 3、light/darkTheme、ThemeMode.system；气泡和输入框按亮度配色 | 可提取主题和基础颜色结构；设置页部分颜色固定，仍需完整适配 |
| H03 | 服务器地址与令牌设置 | **实现**。`settings_screen.dart` 有地址、隐藏显示的 Token、非空校验、保存按钮与成功/失败状态；返回后重载配置 | 可改成中继地址与配对入口；旧 Token 不是 FurtherAgent 授权码 |
| H04 | 本地配置保存与启动恢复 | **实现但存储需替换**。`services/config_storage.dart` 将 server_url/token 写入本地 JSON，按 HOME/USERPROFILE 或固定 Android 路径定位 | 保留跨启动恢复配置的需求；手机使用平台目录和安全凭据存储 |
| H05 | 首次配置引导与状态灯 | **实现但不是在线检测**。`_loadConfigAndInit(showWelcome: true)` 添加欢迎/配置提示；标题红绿点依据字段非空 | 可借鉴引导；必须改为区分已配置、连接中、在线和断线 |
| H06 | 小屏输入区 | **实现**。`_buildInputBar` 有 1–4 行输入、软键盘发送、发送按钮、等待时禁用输入；底部加安全区 padding，Android adjustResize | 可提取布局，补运行中追加指令、草稿、键盘与前后台验证 |
| H07 | 消息列表与角色气泡 | **实现**。ListView.builder、自动到底部、头像、左右气泡、最大宽度 75%、RepaintBoundary；`widgets/message_bubble.dart` | 比 Web 模板更接近可改造的原生移动 UI 组件；正文目前只是 Text，不含 Markdown 或选择复制 |
| H08 | 思考占位与错误反馈 | **实现**。ChatMessage.loading、思考中圆点动画、超时和连接异常气泡；完成后移除 loading | 可借鉴视觉状态；圆点 tween 是进入动画，未见循环控制；异常不应伪装成 Agent 正常回答 |
| H09 | 回复打字机 | **实现，非网络流式**。`_sendMessage` 等 askHermes 返回完整字符串，再每 50ms 显示 3 个 Dart 字符串代码单元 | 仅作动画参考；优先按真实增量更新，不用动画掩盖后端等待 |
| H10 | MCP 初始化和连接会话 | **实现框架**。`services/hermes_mcp_service.dart:_ensureInitialized` 发送 initialize、initialized 通知，保存 Mcp-Session-Id 并在后续 HTTP 请求中携带 | 仅适用于旧 MCP Bridge；不能直接当作 FurtherAgent 的 WebSocket 中继协议 |
| H11 | 提问工具调用 | **实现框架，服务端在仓库外**。askHermes 调 tools/call → ask_hermes，仅传 question，默认 120 秒超时 | 可保留“服务层与页面分离”的结构；没有原会话 ID 或完整历史同步契约 |
| H12 | 状态工具调用 | **部分**。checkStatus 调 hermes_status，默认 30 秒超时；没有找到 UI 或其他调用者 | 只算服务方法，不算已经接好的健康检查、心跳或在线灯 |
| H13 | JSON 与 SSE 响应解析 | **部分**。`_parseJsonRpcBody/_parseSseBody` 支持普通 JSON 和收齐后的 SSE 文本；HTTP 错误、JSON-RPC error 有处理 | UTF-8 解码思路可保留；多事件 SSE、真正流式、工具 isError 与响应对应校验需重做 |
| H14 | 中文解码与 CLI 文本清理 | **实现**。显式 utf8.decode(bodyBytes)；`_cleanHermesResponse` 清 ANSI、边框、统计行，优先提取 Hermes 回答块 | 有旧格式兼容价值；FurtherAgent 已有结构化消息，不应先转终端文本再用正则还原 |
| H15 | 消息数据模型 | **实现但只有内存数据**。`models/chat_message.dart` 有 id、sender、text、timestamp、isLoading 和 copyWith | 可参考模型分层；缺会话 ID、远端稳定消息 ID、工具/附件结构，timestamp 未显示，copyWith 未用于打字机更新 |
| H16 | Android 与 iOS 工程骨架 | **平台工程已提交，交付未验证**。Manifest 网络权限、启动 Activity、键盘配置，iOS Runner/Info.plist；应用包标识仍为示例，Android release 使用 debug 签名 | 可参考工程组织和平台配置；不是已完成正式签名、打包、分发或双端实机验收 |

### 3.1 当前仓库没有接通的能力

7 个业务 Dart 文件中未找到：会话列表/新建/切换/归档、消息持久化或离线历史、Markdown/代码块/图片/文件交互、语音输入、工具过程详情、停止/追加/审批、模型与模式切换、多设备配对、自动重连/心跳/断点恢复、前后台任务恢复、系统通知/推送、系统分享/深链，以及应用内更新。平台模板中的 PROCESS_TEXT 查询不能据此算作系统分享功能。

这些是“当前代码没有该实现”的结论，不代表旧服务器不具备相应能力。特别是 `_sessionId` 为 MCP 传输会话标识，不能证明同一 Hermes 聊天历史得到续接；ask_hermes 只传 question，旧 Bridge 如何组织 Agent 会话需要其服务端源码才能确认。

### 3.2 不宜原样迁移的实现

1. **旧协议不是 FurtherAgent 协议。** 客户端直接连接 MCP Bridge；没有中继配对、能力协商、SessionView、修订号或共享附件通道。只替换服务器 URL 无法接入 FurtherAgent，需替换服务层和状态模型。
2. **Token 存储与传输需要替换。** ConfigStorage 明文写 JSON，并打印读取的完整配置；`_endpoint` 把 Token 拼进 URL，调用日志打印该端点。设置示例为 HTTP，Android 允许明文流量，客户端没有 HTTPS 强制校验。文档不保存实际凭据，也没有调用旧线上端点；迁移时不保留这些日志与凭据处理。
3. **流式和会话恢复不足。** http.post 等完整响应；SSE 解析把所有 data 行合并后一次 jsonDecode，多个独立 JSON-RPC 事件会产生解析风险。没有逐事件订阅、失效 session 重建或结果未知恢复。初始化解析后也没有完整验证 JSON-RPC error 和通知响应，工具 result.isError 未单独处理。
4. **消息不持久。** `_messages` 只在页面 State；设置保存不等于聊天保存。更换服务配置后列表仍保留，可能混合不同服务器的旧消息；重新加载配置创建新服务时未先关闭旧 http.Client。没有前后台恢复和旧响应代次隔离。
5. **打字机可能改变消息身份和文本边界。** 每步创建新的 ChatMessage.hermes，id 和时间随之改变；按 UTF-16 代码单元 substring，可能切开 emoji 等字符。完整回复到达就先恢复发送，长回复动画与新一轮消息可能重叠。
6. **“已配置”不等于“连接正常”。** 绿点只检验字符串非空，checkStatus 没有接 UI；超时提示让用户重试，但没有查询原请求是否已执行。手机应采用 FurtherAgent 的结果未知语义，避免重复任务。
7. **模板与测试不能算交付证明。** `test/widget_test.dart` 仍引用不存在的 MyApp 并测试默认计数器；当前入口是 HermesApp。Android release 仍用 debug 签名，应用标识和图标是模板。根 README 的“地址和 Token 写在服务文件中”已与设置页实现不一致。没有把这些文件视为测试通过或正式移动包已可发布的证据。

## 4 momo-chat 影响复用的具体缺口

这些问题来自本次源码路径检查，不是对旧线上部署的完整安全或运行审计。

1. **运行依赖不完整。** 仓库没有包含 `auth.shared`、Agent Service 的服务端源码，也没有依赖清单与部署说明。`stt.py` 还固定依赖 Hermes 的 Linux Python 环境。仅克隆这 7 个文件不能复原原系统。
2. **流式链路被缓冲。** `api.py:agent_chat_stream` 内部 `_collect()` 先收集全部 WebSocket 事件，待 `asyncio.run(_collect())` 返回才 `yield from chunks`；done 回复再切成小块。前端每秒轮询数据库，不是直接消费真实 token 流。
3. **对话上下文不完整。** `agent_chat_stream` 遍历输入时将所有 user 消息排除在 history 外，仅保留最后一条为 message；早期用户消息没有随 history 发送。session_id 虽在 UI 和函数参数中出现，却未传入 WebSocket chat 请求，不能证明继续了 Hermes 原会话。
4. **会话状态有串线和恢复风险。** `newChat/loadChat` 没有停止旧轮询或用会话代次丢弃过期响应；`loadChat` 没有根据 processing 状态恢复轮询。刷新和切换后的持续观察链路不完整，不能作为手机前后台恢复方案。
5. **会话所有权检查不一致。** 列表和删除按用户过滤；读取消息、读取状态、提交聊天和 get_conversation 工具在可见代码中只按 conversation_id 访问，没有校验当前用户归属。仓库外认证实现未知，但这些调用未传入归属条件。
6. **应用数据不是已鉴权的用户隔离。** save/load/list 路由无需登录，以调用者传入的 username 区分数据；公开应用托管与主应用还在同一来源。可保留功能目标，但需要重设计访问控制、应用隔离和发布范围。`app.py` 的 `json` 导入只在 `_run_ai` 局部，save/load 路由不能使用它。
7. **富文本不宜直接移植。** 会话列表把 title 拼进 innerHTML；Markdown 正则把 URL 拼入属性，没有完整属性转义与协议白名单。正文会继续经过其他正则替换，代码块等也缺少独立解析。应保留显示需求而重做安全渲染。
8. **工具授权与路径约束不完整。** tool_exec 直接执行；文件读写有 `_sandbox_path`，但技能名称拼入路径没有相同的规范化检查。不能把“按用户分目录”当成系统级沙箱，也不能替代 FurtherAgent 的远程审批。
9. **移动端交互尚未完成。** viewport 禁止缩放，部分文字和按钮偏小，删除入口依赖 hover；键盘遮挡、系统返回、前后台、弱网和大历史都没有本次实机证据。
10. **状态和错误语义过于简单。** 只有 ready/processing；后台 finally 会把异常也置 ready。`api.py` 的错误输出字段与 `app.py` 检查 type=error 的条件不一致。不能据 ready 判断任务成功，更不能据超时自动重发。

## 5 FurtherAgent 当前可以承接什么

手机端建议作为新的 A 控制端，继续走 `Android 手机 A → 中继 ← 接入端 B → Agent`。B 仍负责本机 IPC、Hermes HTTP、DSH 桥接和文件工作区。目标平台仅 Android；框架以完整满足需求和开发维护方便为准，不受两个旧项目技术栈影响。

| 能力 | 当前依据 | 手机端处理方向 |
| --- | --- | --- |
| 配对和设备连接 | `Core/RegistryClient.cs`、`RelayConnection.cs`：POST pair 获取 ticket，通过 control WebSocket 首帧认证 | 复用契约；手机持有配对资料，不暴露 B 的 Agent 凭据 |
| 功能开关 | `Core/Contracts.cs:AgentInfo`，接入端 agents 返回真实能力 | 新建、配置、归档、文件、缩略图、发送方式按能力显示，不能因旧 App 有按钮就默认支持 |
| 会话与正文 | `SessionInfo/SessionView/ChatEntry/PendingPrompt`，sessions、attach RPC | 同步已有会话，不建立与远端脱节的第二份聊天事实来源 |
| 增量和重连 | `SessionSync.cs` 的 revision、baseRevision、keep；`RemoteConnection` 重新协商同步 | 完整实现基线校验；恢复读快照，发送和审批结果未知时不自动重放 |
| 发送、停止、提问与审批 | send、interrupt、respond；`SessionActions.cs`；各适配器限制不同 | 手机上要呈现执行状态与真实可操作范围，审批默认只处理后端支持的类型 |
| 模型、模式、归档 | settings、configure、archive；各适配器 CanCreate/CanConfigure/CanArchive | 按后端能力提供入口；没有统一的永久删除会话 RPC |
| 文件与图片产物 | `FileTransfer.cs`、`ImageThumbnail.cs`、SessionView.OutputFiles | 复用分块、校验、缩略图与原图分离；手机补相册、文件选择、保存和系统分享 |
| 网络可见性 | `ConnectionHeartbeat.cs`、WebSocketTransport 的流量统计，现有 WPF 状态设计 | 区分服务端可达、B 可达、任务执行；手机断网不能误报任务结束 |

上述 `Core/` 均指 `src/FurtherAgent.Core/`。文件传输当前上限为 100 MiB、分块 128 KiB；手机应以协议返回限制为准，不能将旧网页的图片链接识别当作附件协议。

### 5.1 后端能力差异

| 项目 | Codex 桌面 | Hermes | DSH |
| --- | --- | --- | --- |
| 原会话历史与发送 | 当前适配器实现；实验性桌面 IPC | 原生 HTTP 接口接入 | 同一 Host 桌面或独立后台接入 |
| 新建会话 | 当前未开放 | 已实现 | 按桥接能力开放 |
| 模型与模式 | 实验性配置接口 | 模型配置；只支持 Build | 模型和 Plan/Build |
| 原生归档 | 当前未开放 | 已实现归档及按 ID 恢复 | 按桥接能力开放 |
| 文件与图片 | 共享文件通道；具体模型理解能力另看适配 | 共享文件、输出产物、缩略图 | 当前未开放 |
| 停止、追加和审批 | 依已有适配类型 | 受本接入端持有 run_id 限制 | 停止、队列、追加已接；远程审批与提问未开放 |

依据为当前源码及 [DSH 接入](DSH.md)、[Hermes 接入](Hermes.md)、[Hermes 0.8.1 验收](Hermes-0.8.1-Validation.md)。这些文档中的既往联调不等于手机端已经验证。README 含历史版本说明，涉及最新行为时以当前源码和对应版本记录为准。

## 6 手机端候选功能池

以下为候选范围与建议顺序，不是已批准的开发计划。M 为 momo-chat 条目，H 为 HermesApp 条目；这些来源同时包含完整实现、原型和局部代码，状态见各自清单。

| 优先级 | 候选功能 | 来源 | 定义与条件 |
| --- | --- | --- | --- |
| P0 | 连接设备、记住配对、主动断开、连接诊断 | H03–H05/H12 + FurtherAgent | 改造旧设置页；手机安全存储配对信息；中继健康、B 心跳与 Agent 状态分开 |
| P0 | 会话列表、搜索、切换、新建、归档 | M02–M04 + FurtherAgent | 能力驱动；保留会话切换草稿，不把删除当归档 |
| P0 | 聊天、Markdown、代码、图片和工具折叠 | M05–M10 + H06–H09 + FurtherAgent | 支持小屏、键盘与安全区；大历史按需加载/呈现；代码复制、长表格横向阅读 |
| P0 | 发送、追加、停止、审批、提问 | FurtherAgent | 按真实后端能力；显示发送中、已接收、执行中、等待审批和结果未知 |
| P0 | 前后台恢复、网络切换、断线重连 | FurtherAgent + 旧项目缺口 | 重建订阅和基线，恢复选中会话；不自动重发不确定写操作 |
| P0 | 图片与文件上传、下载、产物入口 | M08/M14 + FurtherAgent | 可用后端先接；文件校验、进度、失败保留，原图按需下载 |
| P1 | 模型、提供商、Plan/Build 和发送方式 | FurtherAgent | 能力驱动，返回实际生效状态 |
| P1 | 系统深浅主题与首次配置引导 | H01/H02/H05 | 覆盖完整页面与错误/空状态；主题不是连接状态 |
| P1 | 语音转文字 | M12 | 识别后先填草稿供编辑；平台权限、语言、时延、服务位置待定 |
| P1 | 任务完成与待审批通知 | 手机使用场景新增 | 两个旧项目均未找到完整推送实现；需要通知权限、后台方案和跨端已读设计 |
| P1 | 链接或网页产物卡片 | M13/M14 | 可先显示后端提供的 URL；应用托管、公开发布是额外服务范围 |
| P2 | 记忆查看与编辑 | M16 | 先定义各 Agent 的能力与归属；不得把手机编辑视为所有后端统一生效 |
| P2 | 技能目录与管理 | M17/M18 | 需接入端能力和授权，旧模板先作为资料保存 |
| P2 | 跨会话检索与回顾 | M20 | 搜索范围、用户归属和正文可见权限先定义 |
| P2 | 网页应用托管与应用数据存储 | M13/M15 | 明显超出聊天客户端；需要隔离、鉴权、容量和生命周期设计 |

## 7 两个旧项目的互补与复用取舍

| 维度 | momo-chat | HermesApp | FurtherAgent 手机端处理 |
| --- | --- | --- | --- |
| 客户端形态 | Flask 输出的移动网页 | Flutter + Android/iOS 工程 | 新客户端只做 Android，独立选择框架 |
| UI 和主题 | 深色网页、会话抽屉 | 两页导航、系统深浅主题、安全区、气泡组件 | 只参考相关功能点，新界面按手机使用场景设计 |
| 历史和会话 | SQLite 持久历史，有列表/新建/删除 | 只有当前页面内存列表 | 统一采用 FurtherAgent 远端会话，不复制两套事实来源 |
| 富文本与产物 | Markdown、图片链接、网页完成卡片 | 纯文本，CLI 回答清理 | 扩展结构化正文与附件，不沿用正则或 CLI 格式依赖 |
| 回复链路 | 外部 Agent WS → Python 缓冲 → 数据库轮询 | MCP HTTP 完整回复 → 本地打字机 | 使用 FurtherAgent 的真实更新与增量同步 |
| 连接配置 | 仓库外共享认证 | 地址/Token 设置和本地 JSON | 复用设置交互，替换成中继配对与安全存储 |
| 语音/记忆/技能/应用托管 | 有原型或服务端代码，完整度不同 | 未找到实现 | 作为可选扩展，归属 Agent/服务端，独立排优先级 |
| 网络恢复和移动后台 | 轮询恢复有缺口 | 没有恢复订阅、通知和离线历史 | 新增移动生命周期处理，保留未知结果不重发原则 |

### 7.1 功能参考与实现边界

- **旧项目不构成开发约束：** 不要求继续 Flutter、Flask、旧页面结构、存储格式或旧通信协议。清单中的“可利用”只表示候选参考价值，不是必须迁移的实现，也不因已有代码而优先选择某框架。
- **收集产品功能：** momo-chat 的会话入口、网页产物、语音转写和记忆/技能；HermesApp 的首次引导、深浅主题、手机输入与安全区。最终交互可重新设计，功能池也不等于全部进入首版。
- **复用现工程协议：** FurtherAgent 的配对、连接、能力开关、SessionView、增量基线、审批/停止与文件校验。Flutter 不能直接把 C# Core 当 Dart 库引用；选择 Flutter 时需实现对应协议客户端。
- **保留为参考而不直接迁移：** momo-chat 的旧认证/Agent Service、简易富文本渲染与公开数据接口；HermesApp 的 MCP 服务、CLI 正则、Token JSON 和伪流式动画。

### 7.2 开发范围与后续验收

**目标是完整可用且开发维护方便的 Android 客户端。** 兼容 FurtherAgent 现有服务是本工程集成要求，两个旧项目只用于收集功能。当前首选建议为 .NET MAUI 的 Android 单目标，理由是现工程 C# 控制端逻辑的共享价值；Android 文件、生命周期、通知和 UI 仍需专门实现与验证，不能仅凭 Core 是 net10.0 宣称已经可用。

两仓库的可见客户端与功能源码已覆盖；未提供的旧认证、Agent Service 和 MCP Bridge 属于外部依赖，不把其未知行为补写成旧客户端已有能力。手机端无需为复用界面先恢复这几套旧后端。

平台范围已确认：只做 Android，不考虑 iOS。后续细化最低 Android 版本、设备范围、分发方式和首版功能，应用托管、记忆与技能仍是候选项。当前默认分析范围为“手机作为 A 远程控制已有 Agent”，不包含在手机运行完整 Agent。继续保持只分析、不开发。

后续验收应覆盖：

- 同一会话在电脑与手机的历史、增量、工具进度及最终结果一致；跨端竞争遵循服务端控制权规则。
- 切后台、锁屏、进程被系统回收、Wi-Fi/移动网络切换后能恢复读取；提交确认丢失时不重复执行。
- 各后端不支持的能力不可误触；Hermes 运行归属、DSH 审批缺口、Codex 新建与归档限制准确呈现。
- 长消息、代码、表格、大量历史、键盘、旋转和系统返回有实机结果；消息切换后旧异步响应不串入新会话。
- 图片缩略图与原图、上传与下载、文件校验及失败恢复可用；任务完成通知不把“连接恢复”误报为“任务完成”。

## 8 源码复查入口

两个旧仓库保留在本地独立 Git 目录，后续可以按固定提交复查；它们不作为 FurtherAgent 子模块或源码依赖。

- momo-chat：`artifacts/inspection/mobile-legacy/momo-chat/`；入口 `app.py`，界面 `templates.py`，Agent/工具 `api.py`，数据 `db.py`，语音 `stt.py`，模板 `skill_templates/`。
- HermesApp：`artifacts/inspection/mobile-legacy/HermesApp/`；业务代码在 `hermes_app/lib/`，配置 `pubspec.yaml`，平台入口 `android/app/src/main/AndroidManifest.xml` 和 `ios/Runner/Info.plist`，旧测试 `test/widget_test.dart`。
- FurtherAgent：`src/FurtherAgent.Core/Contracts.cs`、`RelayConnection.cs`、`SessionSync.cs`、`FileTransfer.cs` 与三个 Agent 适配器；这些是手机端协议对照入口。

本次验证限于源码、配置和文档引用核对，没有安装依赖、构建旧 App、连接旧 Agent 或用真实手机验收。37 项是资料盘点条目数，不是 37 项全部可用或独立待开发功能。
