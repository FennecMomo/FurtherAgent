# FurtherAgent 手机端旧项目功能盘点

记录日期：2026-10-10。用途：收集旧项目功能，为 FurtherAgent 手机端需求讨论提供依据。本轮仅分析和记录，没有启动手机端开发、改动业务代码或部署服务。

**阶段结论：momo-chat 可提供小屏聊天、网页产物入口、记忆与技能等产品参考；手机端的远程连接和会话控制应优先沿用 FurtherAgent 现有协议。HermesApp 尚未取得源码，本报告不是两个仓库均已完成的盘点。**

## 1 调查基线与完成范围

| 对象 | 本次基线 | 完成范围 |
| --- | --- | --- |
| [momo-chat](https://gitee.com/fennecmomo/momo-chat) | `master`，`2b615adc021c3cbc69e22fc227759ce46faaf723`，提交时间 2026-10-09 22:46:35 +08:00 | 已完整克隆并阅读全部 7 个功能及模板文件；另有 `.gitignore`。源码静态核对，未运行旧服务 |
| [HermesApp](https://gitee.com/fennecmomo/HermesApp) | 未获得有效引用或提交号 | HTTPS 要求凭据；现有本机 SSH 身份未获 Gitee 授权。需要正确地址、仓库访问授权或已有源码路径后继续 |
| FurtherAgent | `0.8.2`，`f2c1dbbb835fa0f8dd5aa6c51abcdaea7b080497` | 阅读当前 Core 协议、适配器、同步和文件传输代码，对照现有文档；没有重跑真实 Agent 联调 |

旧仓库检出目录位于 `artifacts/inspection/mobile-legacy/`，由工程既有 `.gitignore` 排除，不纳入 FurtherAgent 源码，也没有执行旧仓库的脚本。HermesApp 目录目前只是失败克隆留下的初始化目录，不能当作已拉取仓库。下文路径以各自仓库根目录为基准，函数名用于在固定提交中定位。

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

## 3 影响复用的具体缺口

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

## 4 FurtherAgent 当前可以承接什么

手机端建议作为新的 A 控制端，继续走 `手机 A → 中继 ← 接入端 B → Agent`。B 仍负责本机 IPC、Hermes HTTP、DSH 桥接和文件工作区。该方向基于本工程现有架构，尚未选定 Android、iOS、Web 或跨平台 UI 技术。

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

### 4.1 后端能力差异

| 项目 | Codex 桌面 | Hermes | DSH |
| --- | --- | --- | --- |
| 原会话历史与发送 | 当前适配器实现；实验性桌面 IPC | 原生 HTTP 接口接入 | 同一 Host 桌面或独立后台接入 |
| 新建会话 | 当前未开放 | 已实现 | 按桥接能力开放 |
| 模型与模式 | 实验性配置接口 | 模型配置；只支持 Build | 模型和 Plan/Build |
| 原生归档 | 当前未开放 | 已实现归档及按 ID 恢复 | 按桥接能力开放 |
| 文件与图片 | 共享文件通道；具体模型理解能力另看适配 | 共享文件、输出产物、缩略图 | 当前未开放 |
| 停止、追加和审批 | 依已有适配类型 | 受本接入端持有 run_id 限制 | 停止、队列、追加已接；远程审批与提问未开放 |

依据为当前源码及 [DSH 接入](DSH.md)、[Hermes 接入](Hermes.md)、[Hermes 0.8.1 验收](Hermes-0.8.1-Validation.md)。这些文档中的既往联调不等于手机端已经验证。README 含历史版本说明，涉及最新行为时以当前源码和对应版本记录为准。

## 5 手机端候选功能池

以下为候选范围与建议顺序，不是已批准的开发计划。H 表示 HermesApp 待补充来源，不能预先认定其已有实现。

| 优先级 | 候选功能 | 来源 | 定义与条件 |
| --- | --- | --- | --- |
| P0 | 连接设备、记住配对、主动断开、连接诊断 | FurtherAgent | 手机安全存储配对信息；中继健康、B 心跳与 Agent 状态分开 |
| P0 | 会话列表、搜索、切换、新建、归档 | M02–M04 + FurtherAgent | 能力驱动；保留会话切换草稿，不把删除当归档 |
| P0 | 聊天、Markdown、代码、图片和工具折叠 | M05–M10 + FurtherAgent | 支持小屏、键盘与安全区；大历史按需加载/呈现；代码复制、长表格横向阅读 |
| P0 | 发送、追加、停止、审批、提问 | FurtherAgent | 按真实后端能力；显示发送中、已接收、执行中、等待审批和结果未知 |
| P0 | 前后台恢复、网络切换、断线重连 | FurtherAgent + 旧项目缺口 | 重建订阅和基线，恢复选中会话；不自动重发不确定写操作 |
| P0 | 图片与文件上传、下载、产物入口 | M08/M14 + FurtherAgent | 可用后端先接；文件校验、进度、失败保留，原图按需下载 |
| P1 | 模型、提供商、Plan/Build 和发送方式 | FurtherAgent | 能力驱动，返回实际生效状态 |
| P1 | 语音转文字 | M12 | 识别后先填草稿供编辑；平台权限、语言、时延、服务位置待定 |
| P1 | 任务完成与待审批通知 | 手机使用场景新增 | 两个旧项目均不能据当前资料认定有完整推送；需要通知权限、后台方案和跨端已读设计 |
| P1 | 链接或网页产物卡片 | M13/M14 | 可先显示后端提供的 URL；应用托管、公开发布是额外服务范围 |
| P2 | 记忆查看与编辑 | M16 | 先定义各 Agent 的能力与归属；不得把手机编辑视为所有后端统一生效 |
| P2 | 技能目录与管理 | M17/M18 | 需接入端能力和授权，旧模板先作为资料保存 |
| P2 | 跨会话检索与回顾 | M20 | 搜索范围、用户归属和正文可见权限先定义 |
| P2 | 网页应用托管与应用数据存储 | M13/M15 | 明显超出聊天客户端；需要隔离、鉴权、容量和生命周期设计 |

## 6 复用取舍与后续验收

**优先复用现工程协议与业务语义，借鉴旧工程界面和产品功能。** momo-chat 的 Python 后端与 WPF 客户端都不能直接成为手机 UI。若最终选择兼容 .NET 的客户端，可评估抽取或引用 Core 中与平台无关的连接、同步和传输逻辑；若选其他技术，则按相同 RPC 契约实现客户端。任何方案都还需手机目标平台的编译与运行验证，不能仅凭 Core 是 net10.0 宣称可直接移植。

可保留的资料包括：小屏抽屉与消息布局、应用完成卡片、语音流程原型、记忆和技能操作语义、项目模板。需要重写或隔离的部分包括：认证与会话授权、Markdown 解析、旧轮询状态机、旧 Agent Service 桥接、公开应用托管和数据 API。两个仓库的完整比较仍待 HermesApp 源码。

进入开发前建议确认：目标是 Android、iOS 还是两者；是否需要安装包；手机端是否只作为 A 控制端；应用托管、记忆和技能是否纳入首版。当前默认分析范围为“手机作为 A 远程控制已有 Agent”，不包含在手机运行完整 Agent。

后续验收应覆盖：

- 同一会话在电脑与手机的历史、增量、工具进度及最终结果一致；跨端竞争遵循服务端控制权规则。
- 切后台、锁屏、进程被系统回收、Wi-Fi/移动网络切换后能恢复读取；提交确认丢失时不重复执行。
- 各后端不支持的能力不可误触；Hermes 运行归属、DSH 审批缺口、Codex 新建与归档限制准确呈现。
- 长消息、代码、表格、大量历史、键盘、旋转和系统返回有实机结果；消息切换后旧异步响应不串入新会话。
- 图片缩略图与原图、上传与下载、文件校验及失败恢复可用；任务完成通知不把“连接恢复”误报为“任务完成”。

## 7 HermesApp 待补采集清单

获得源码后补充固定提交号、工程技术栈、页面与导航、聊天和会话功能、图片文件与语音、模型配置、登录和连接、后台/通知、缓存与离线、系统分享与深链、更新与打包、服务端依赖、已弃用或占位入口。逐项追到实际调用链，再与 M01–M21 和 FurtherAgent 对照，标注可直接提取、需改造、仅产品参考或不应迁移。

本阶段阻塞来自仓库访问，不代表仓库没有这些功能。没有用本工程的 `HermesAdapter` 或第三方同名项目替代 HermesApp 的证据。
