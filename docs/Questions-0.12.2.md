# Codex 提问适配：Windows 0.12.2 / Android 0.10.1

旧版只处理 `requests` 内的 `item/tool/requestUserInput`。异步提问位于 `agentMessage.questions`，即使消息正文为空也必须显示；回答按 Codex 的 `send_user_message_question_reply` 消息格式携带 questionItemId 回传。

本次保留原同步提问，新增异步问题投影、选项规范化、当前问题校验与回答提交。已接受的用户回复移除对应问题，尚未接受的 steering 不提前移除，新轮次不重新弹出旧问题。界面把回复包装显示为问题和答案，不展示协议标签。

桌面输入框上方提供问题、选项按钮、自由输入和提交按钮；手机出现高亮「回答问题」，点进去选择或自行填写。不会自动选答案或自动提交。不增加 todolist。

验证：7 项问题投影/回复回归通过；实际 WPF 问题区选项点击与切换主题保留回答通过；原有 27 项 WPF 检查通过。Windows 修复版已安装，Android APK 已构建。当前没有 ADB 手机连接；手机实机验收仍待完成。

2026-10-12 14:22（北京时间），用户连到本机测试接入端后，从 FurtherAgent 提交「链路测试通过」，回答到达正在运行的 Codex 会话。公网中继观察端记录 `sawQuestion=true`、最新用户回答包含该测试答案、FurtherAgent 待处理问题列表为空。此结果只确认问题显示、人工提交、答案回传与 FurtherAgent 问题移除，不代表 Codex 原生问题卡片已同步为已回答。第一次未找到会话是客户端尚未连接测试接入端，切换后重发测试题完成验证。

已知未解决项：用户随后确认 Codex 原生问题卡片仍然保留。检查本机 Codex 26.1007.2314.0 的前端实现及 IPC 记录，回答携带的 questionItemId 正确，但提问轮次结束后，回复进入新轮次。原生问题卡片从提问所在轮次的条目计算回答；Codex 自身提交处理还会直接更新窗口内的 lastSubmission 等状态，FurtherAgent 的消息回传不会调用该界面处理。已检查的 follower IPC 接口未发现异步问题卡片状态更新入口；同步提问的 submit-user-input 接口不能据此视为异步卡片更新接口。尚未实现或验证两端原生卡片状态一致，不能将本次回传测试标记为完整验收通过。不修改 Codex 安装文件或历史记录来伪造完成状态。

日常使用时，远端负责接入 Codex 的 Windows 客户端也需要更新到 0.12.2 或以后；只更新控制端，旧接入端仍不会投影异步问题。上述实测不覆盖手机交互或所有同步提问类型。

真实验证辅助入口：`FurtherAgent.SmokeTests --question-watch <thread-id> <private-output-path>`，创建临时本机 Codex 接入端，经公网中继订阅指定会话并记录问题/答案，10 分钟后释放。配对码写入输出路径附加 `.code` 的私有文件，不提交到仓库。这个辅助入口只观察，不自动回答。
