# 0.11.0 工作空间

Windows 控制端按远端会话工作目录分组；同名文件夹按完整路径区分，Windows 路径大小写和分隔符归一化。组内按最近活动排序，刷新保留折叠状态和当前会话。搜索支持标题、工作空间名称和完整路径。Codex 原生项目列表中的空工作空间也显示。

## 使用

1. 控制端 A 与 Codex 接入端 B 都升级到 0.11.0，B 保持 Codex 桌面运行。中继无须升级。
2. 「添加工作空间」浏览的是 B 的磁盘。打开已有目录并添加；或填写新文件夹名称，在当前目录下创建后添加。
3. 「新建会话」选择工作空间、填写标题，点击「创建并打开」。会话保持为空，之后再发送任务。
4. 不存在、无权限的目录会报错；不自动创建多级父目录。Hermes/DSH 不显示工作空间管理入口。

本轮更新 Windows 控制端及 Codex 接入端。Android 0.10.0 的旧新建窗口没有工作空间选择，请使用更新后的 Windows 控制端进行此操作。

## 实现与兼容边界

- 工作空间使用 Codex 原生 `project/list`、`project/create`；会话使用 `thread/start` 指定 cwd 和 projectId、`thread/name/set`，然后读回目录校验。协议参考：[Codex App Server](https://learn.chatgpt.com/docs/app-server)。
- 短期 stdio 元数据进程不发送任何 turn，不恢复正在运行的已有会话。退出后通过原有 Codex Desktop follower IPC 打开和控制新会话。沿用远端 Codex 配置，不覆盖模型、权限或登录状态。
- 优先发现正在运行的 Codex 运行时，再尝试桌面附带的程序或 PATH。可用 `FURTHERAGENT_CODEX_EXECUTABLE` 指定绝对路径。要求远端 Codex 支持原生 project API；旧版本明确报错。
- 新增工作空间按路径复用已有原生项目。新建会话请求日志位于 B 的 `CODEX_HOME/furtheragent/create-requests`，在提交前记录 pending，收到 ID 后持久化。已知 ID 可恢复命名，结果未知时拒绝自动重放。
- Codex 桌面自己的项目侧栏可能缓存项目列表；原生项目已持久化且 FurtherAgent 刷新可见，不以另一应用侧栏即时刷新为保证。

## 验证

- Release 编译成功；257 项 smoke 和 27 项 WPF/持久化检查通过。
- 本机 Codex 26.1007.2314.0 实际原生目录、项目、空会话创建和读回成功。
- 公网中继真实链路已验证目录浏览、中文/空格路径创建、原生工作空间保存、重复请求返回相同会话、会话列表恢复 cwd，以及 Codex 桌面 IPC 打开新空会话（0 条消息，CanSend=true）。
- 已安装至稳定桌面入口；本机控制通道核验实际进程版本 0.11.0、ready=true、连接和双端心跳在线。
- Computer Use 打开 FurtherAgent 时用户按 Esc 停止，因此本轮没有完成实际窗口点击流程验收。分组逻辑、WPF 编译检查和真实公网 RPC 验证已完成，不将其等同于完整 UI 点击验收。临时验证共享随后关闭。
