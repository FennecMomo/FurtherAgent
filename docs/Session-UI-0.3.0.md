# 0.3.0 会话界面与能力验收

## 已实现

会话主页、独立设置页、授权码连接弹窗、左上角新建入口、可选择复制的消息卡片、复制整段对话、会话搜索、时间格式化、内存草稿、Ctrl+Enter、执行中追加、停止、审批和错误提示。服务端地址本机持久化，授权码不落盘。

Hermes：POST /api/sessions 创建空会话，显式固定 ID 遇 409 时读取已创建资源，重复点击不会产生新运行；模型列表 GET /api/model/options，模型锁定 POST /api/sessions/{id}/model。忙碌、未知提交或不可用状态下禁止新建和模型修改。Hermes 无确认可用的原生 Plan 接口，禁用该控件并说明原因。

Codex：读取桌面快照 latestCollaborationMode；使用 thread-follower-update-thread-settings v2 设置 model 与 collaborationMode。模式值为 default / plan；保留 reasoning_effort，清空跨模式复制的 developer_instructions，由 Codex 使用原生模式。当前运行中不修改设置。内部 IPC 随桌面版本变化，模型 ID 可手动输入，模型可用性由远端校验。没有另启 CLI 进程或直接写桌面历史。Codex 新建会话仍需在 B 桌面操作。

## 验证与限制

构建无警告、无错误，自动化 50 项通过（真实回环中继 + 模拟 Hermes API）。新增用例覆盖能力协商、模型列表过滤、模型锁定请求、拒绝伪 Plan、重复创建恢复、未知提交时禁止改模型。WPF 实际窗口已检查主页和独立设置页。

这些结果不代表真实服务器的模型路由与 Plan 行为已验收。下一轮需升级远端 Hermes 接入端，在专用测试会话验证新建、模型切换及文字回复；Codex 模型和 Plan 需要在专用桌面会话观察双方设置与实际执行行为。没有对用户现有会话发送测试消息或更改模型。

中继无需因本次 UI 更新重启。Linux 接入端升级会失去旧进程内的 run 所有权，须先等待当前运行完成并处理未确认提交。新接入端会给出新授权码。
