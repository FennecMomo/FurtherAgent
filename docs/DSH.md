# DSH 双模式接入（开发中）

此适配从 FurtherAgent 0.6.8 开始实现，不依赖尚未推送的 0.7.0。
桌面与独立后台使用同一插件、同一个 Core 适配器，WPF 和 CLI 通过既有中继协议访问。
尚未完成整套验收，不能将下面的阶段结果等同于正式发布。

## 生命周期与安装

`scripts/Install-DshBridge.ps1 -Mode desktop` 在当前用户 DSH 桌面 profile 中添加插件。
安装前备份 patch；插件按内容摘要放在独立目录，更新不覆盖正在使用的模块。
首次加载或升级后需在 DSH 空闲时彻底退出桌面并重新启动。

`scripts/Install-DshBridge.ps1 -Mode background` 准备独立后台的 patch。
后台使用用户指定的 DSH 桌面安装包所带运行时，不使用 PATH 上可能较旧的 dsh。
其 DSH_HOME 是 `%LOCALAPPDATA%/FurtherAgent/dsh/background-home`，不复制桌面的账号、会话或配置。
后台需要单独配置模型账号；退出请求若发现任务或待处理队列会拒绝关闭。

接入端参数：

| 环境变量 | 用途 |
| --- | --- |
| FURTHERAGENT_BACKEND | hermes（默认）、dsh-desktop、dsh-background |
| FURTHERAGENT_DSH_EXE | 后台使用的 DSH 桌面程序路径 |
| FURTHERAGENT_DSH_DESCRIPTOR | 显式选择现有本地插件实例；不设置时自动发现桌面或启动独立后台 |
| FURTHERAGENT_SERVER / FURTHERAGENT_NAME | 中继与设备名称，沿用既有机制 |

`FurtherAgent.Host --probe` 检查能力和会话数量；后台模式会启动独立运行时、初始化独立目录，再正常退出。
本地连接描述包含随机凭据，只允许当前用户和 SYSTEM 访问，不能上传或加入版本库。
插件只监听 127.0.0.1，拒绝浏览器 Origin 和缺失/错误的 Bearer 凭据。

## 当前实现边界

- 列表、历史、新建、发送、执行中追加、停止和模型设置已接入原生控制器。
- Plan/Build 通过 DSH 自身 `/plan` 命令切换；归档/恢复归档使用 workspaceController，拒绝带活动任务的归档，不删除历史。
- 查看历史仅调用 `sessionQuery.observeSession`，不调用 `resume` 或会隐式恢复冷会话的 `follow`。
- 监听同一 Host 的 assistant-stream 事件，区分真实文本和 reasoning 增量；提交最终消息后移除临时消息。
- 历史排除模型上下文替换副本及插件注入的 user 消息，保留人类原始对话。
- Core 当前每秒取得快照，断线恢复读取，不自动重发结果未知的消息。持久事件游标订阅优化尚未完成。
- 图片、文件、审批、提问、DSH 打断后发送仍未开放；不得仅凭原生接口名称宣称可用。
- 桌面插件退出后不会另起实例接管桌面会话，不删除锁文件、不直接写会话存储。

## 2026-10-10 阶段证据

真实桌面运行时为 0.2.0-rc.2；全局 CLI 0.1.5-rc.2 未用于接入。
后台独立运行时探测读到 0 条会话；桌面读到 9 条既有会话。
两条既有会话各读到 386、387 条投影消息，读取没有取得 Agent 写入权。
专用测试会话在桌面 Host 内创建、接受一条提示词并回复 `FA_DSH_OK`，没有调用工具。
完成后同一 Host 的 Agent 仍可查询，历史为一条用户消息和一条回复。

`dotnet run --project tests/FurtherAgent.SmokeTests -c Release -- --dsh-live` 是显式真实联调入口。
它会创建一个专用会话并提交一条无工具测试提示词，启动独立回环中继，调用真实 CLI。
当前 22 项通过，覆盖同一 Host 双读取者、CLI 中继读取/发送、模型目录、Plan/Build、
原生归档/恢复、观察连接更新、断开后读取不重发、流式和最终回复不重复。
这不等同于鼠标操作或 WPF 视觉验收，也尚未覆盖两个入口同时执行写入的竞争。

尚待完整端到端验收：WPF/CLI 经中继共用桌面最新会话、双入口并发、停止与排队竞争、
断线续接、全部扩展能力，以及两个模式同时工作时的隔离与更新流程。
