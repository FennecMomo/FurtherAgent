# Codex 提问适配：Windows 0.12.2 / Android 0.10.1

旧版只处理 `requests` 内的 `item/tool/requestUserInput`。异步提问位于 `agentMessage.questions`，即使消息正文为空也必须显示；回答按 Codex 的 `send_user_message_question_reply` 消息格式携带 questionItemId 回传。

本次保留原同步提问，新增异步问题投影、选项规范化、当前问题校验与回答提交。已接受的用户回复移除对应问题，尚未接受的 steering 不提前移除，新轮次不重新弹出旧问题。界面把回复包装显示为问题和答案，不展示协议标签。

桌面输入框上方提供问题、选项按钮、自由输入和提交按钮；手机出现高亮「回答问题」，点进去选择或自行填写。不会自动选答案或自动提交。不增加 todolist。

验证：7 项问题投影/回复回归通过；实际 WPF 问题区选项点击与切换主题保留回答通过；原有 27 项 WPF 检查通过。Windows 修复版已安装，Android APK 已构建。当前没有 ADB 手机连接；手机实机与真实问题回传验收仍待完成，不能把以上检查当作真实链路验收。

真实验证辅助入口：`FurtherAgent.SmokeTests --question-watch <thread-id> <private-output-path>`，创建临时本机 Codex 接入端，经公网中继订阅指定会话并记录问题/答案，10 分钟后释放。配对码写入输出路径附加 `.code` 的私有文件，不提交到仓库。这个辅助入口只观察，不自动回答。
