# Hermes 接入与联调（0.4.0 候选版）

适用调查对象：官方 Hermes 0.21.0、Ubuntu x64、已启用的原生 HTTP API Server。
本机实现已通过模拟 API + 真实 FurtherAgent 中继的契约测试，尚未在目标 Linux 服务器执行，也未通过真实 Hermes 写入验收。

## 连接与能力边界

```text
WPF A → HTTPS/WSS 中继 ← Linux FurtherAgent.Host → 127.0.0.1:8642 Hermes API
```

Linux 接入端输出授权码，A 输入码后自动显示 Hermes。一个接入端共享一个 Agent，原有 WPF 本机共享仍为 Codex。
两端主动连接中继，Hermes 无需开放公网端口；默认 profile 可直接使用现有配置，不需要改 multiplex_profiles。

- 会话列表最多读取 1000 条；历史显示最近 250 条，每条最多显示 20000 字符。只读取 HTTP API，不读写 state.db。
- 可在既有 session_id 上提交新 turn，默认继承网关/会话模型设置；可在「模型与模式」中显式修改会话模型锁定。发送消息不替换会话历史或注入 instructions。
- 只能停止、追加和审批**这个接入端进程发起并持有 run_id 的运行**。其他入口是否正在执行未知，发送可能被 Hermes 会话租约排队。
- 订阅增量、工具进度；审批只提供“允许本次 / 拒绝”，必须有准确 request_id，不扩大为 session/always。不支持 clarify。
- WPF/中继断线不销毁接入端的 Hermes 订阅；A 重连并重新选中同一会话后恢复快照。
- Hermes SSE 本身断线后不重订阅，降级每 2 秒轮询 run 状态；期间增量可能丢失，完成后重读历史。
- HTTP 提交结果不明确时，保留原文和幂等键；用户原样重试才找回同一运行，不自动重发。追加、停止、审批也不自动重放。
- 运行状态返回 404 或接口不可达时，不宣称完成，不允许继续发送/停止/审批。待 Hermes 状态恢复后自动恢复可用操作。
- 当前一次跟踪一个会话；有活动运行或未知提交时，不允许切换到另一会话。先处理原会话。
- **运行归属、待确认提交和增量缓存在接入端内存中。接入端重启会丢失这些信息，不会停止或重新提交 Hermes 工作，也不能找回旧 run 的控制权。运行中不要重启；重启前先在 Hermes 确认旧工作结束。** 持久化恢复留待后续版本。
- 界面中的“停止共享/退出接入端”是撤销远程连接，并不等同于停止已经交给 Hermes 的运行。

## 打包

Windows 开发机执行：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Publish-Hermes.ps1
```

生成 `artifacts/release/FurtherAgent-Hermes-0.4.0-linux-x64.tar.gz`。
这是 .NET 10 Linux x64 自包含包，不依赖服务器上的 .NET 8，也不需要安装 .NET 10 SDK/Runtime。
自包含仍使用 Linux 系统库；目标 Ubuntu 的真实启动结果以 `--probe` 为准。

## 配置和只读探测

先将包放到服务器一个新目录并解压，避免覆盖正在运行的服务。执行 `chmod +x FurtherAgent.Host`。
密钥文件仅存放 API_SERVER_KEY 的值，一行，不是整份 Hermes `.env`，权限设为 600。不要把密钥复制到聊天或命令行参数。

```bash
export HERMES_API_URL=http://127.0.0.1:8642
export HERMES_API_KEY_FILE="$HOME/.config/furtheragent/hermes-api.key"
./FurtherAgent.Host --probe
```

`--probe` 只调用 capabilities 和会话列表，打印能力与会话数量，不连接中继、不提交任务、不打印密钥或会话正文。
也支持 `HERMES_API_KEY` 环境变量，但优先文件。程序不会读取整个 Hermes 环境文件或自动修改 Hermes 配置。
现有网关是否支持多个并存 API key 尚未确认；不要为了“专用 key”直接替换旧 key，以免影响原服务。

## 前台联调

需要已经部署、证书可信、支持 WebSocket 的 FurtherAgent 中继服务。该包只含接入端，不会安装或启动中继。

```bash
export FURTHERAGENT_SERVER=https://你的中继服务域名
export FURTHERAGENT_NAME=Hermes-default
./FurtherAgent.Host
```

将打印的授权码提供给可信任的 A。授权码具备实际控制权限，systemd 日志也需限制访问。
A 使用 0.4.0 WPF，填写同一中继地址和授权码。不要把 Hermes API 地址填入 WPF 的中继服务地址。
确认要执行联调后，选择单独的测试会话，顺序验证：

1. 列出会话、显示已有历史；外部运行状态应显示不可见。
2. 发送一条仅要求文本回答的测试消息；核对同一个 session_id 写回历史，模型路由沿用现有设置。
3. 验证增量输出、工具进度，以及本接入端运行的追加/停止/一次审批与拒绝（由用户选择合适测试内容）。
4. 仅断开并重连 A，验证 run_id 不变、不重复执行。
5. 结束测试运行后，再测试 Hermes SSE 故障后的状态轮询。不要为了故障测试重启生产网关。

不能用模拟接口通过来替代上述真实验证，也不要对微信里正在进行的工作发测试命令。

## 可选 systemd 用户服务

首次前台验收后，再考虑安装包内 `furtheragent-hermes.service`。模板默认程序目录为
`~/.local/share/furtheragent`，配置为 `~/.config/furtheragent/host.env`：

```ini
FURTHERAGENT_SERVER=https://你的中继服务域名
FURTHERAGENT_NAME=Hermes-default
HERMES_API_URL=http://127.0.0.1:8642
HERMES_API_KEY_FILE=/home/ubuntu/.config/furtheragent/hermes-api.key
```

这是新增服务模板，不替换 Hermes 服务。第一版不自动重启，以免掩盖运行归属丢失。
配置文件和 key 文件仅接入端用户可读。默认只接 default；mom 保持原状。

## 实现依据与版本差异

以用户提供的《FurtherAgent-Hermes-适配调查报告.md》为部署依据，并对照官方 API 文档和源码实现字段解析。
官方 main 的 SSE 恢复能力已有变化，不能套用到服务器 0.21.0；本实现使用报告中的保守降级策略。
仍需实际 `--probe` 和专用测试会话验证当地版本的 session JSON、delta、approval 和 steer 请求形状。

- https://hermes-agent.nousresearch.com/docs/user-guide/features/api-server
- https://github.com/NousResearch/hermes-agent/blob/main/gateway/platforms/api_server_runs.py


双向文件传输使用 Host 自身的会话文件目录，不需要启用 Hermes browser_control/artifact API。配置 FURTHERAGENT_FILES_ROOT 可隔离联调文件，详见 File-Transfer.md。

## 2026-10-10：第二个 profile（mom）已接入 0.9.1

用户授权主 profile 配置第二个 profile 的独立接入。服务器主 profile 完成配置并提供脱敏报告，本地 Windows CLI 随后完成公网只读验收。本节为当前部署记录，前文 0.4.0 候选状态和“mom 保持原状”属于历史记录。

| 项目 | default | mom |
| --- | --- | --- |
| API | `http://127.0.0.1:8642` | `http://127.0.0.1:8643` |
| 接入端名称 | Hermes-default | Hermes-mom |
| 身份文件 | `~/.config/furtheragent/hermes-default.identity` | `~/.config/furtheragent/hermes-mom.identity` |
| 文件根目录 | `/home/ubuntu/furtheragent-files` | `/home/ubuntu/furtheragent-mom-files` |

- mom 使用独立网关 `hermes-gateway-mom.service`，HERMES_HOME 为 `/home/ubuntu/.hermes/profiles/mom`。服务器在其环境中启用 API 并配置独立端口及凭据，仅监听 loopback；未启用主网关 multiplex 路由、未新增公网端口。
- 本次重启 mom 网关使 API 配置生效，报告的网关 PID 为 3751064；主网关 PID 15657 保持不变，现有中继和两个原接入端保持在线，主身份文件未变。
- 新接入端复用 `/home/ubuntu/furtheragent-0.9.1/host/FurtherAgent.Host`，注册到原中继 `http://127.0.0.1:47320`。独立 systemd 用户服务为 `hermes-furtheragent-mom.service`，已 enabled/active，当次 Host PID 为 3751510。环境文件 `/home/ubuntu/furtheragent-second-profile/hermes-mom.env` 受限保存，不入 Git。
- 服务器用目标 API 的 `/v1/models` 返回 model id `mom`、对应进程环境和接入配置确认 profile；`--probe` 通过并读取到 96 个会话。新固定码独立于主 profile，未复用身份。
- 北京时间 15:53:59–15:54:20，本地 CLI 使用新码实际连到 Hermes-mom，Agent 可用，读取 96 个会话，持续观察 20 秒心跳正常、退出码 0；另行连接 Hermes-default 成功。首次连接遇到 TLS 握手超时，重试只读探测后成功，未重放任何业务写入。
- 本轮没有向业务会话发送任务，也没有实体手机验收。客户端仍按不同固定码选择 profile，没有新增应用内 profile 切换界面。

运维时可先用 `systemctl --user status hermes-furtheragent-mom.service` 查看新 Host。撤销新 Host 可使用 `systemctl --user disable --now hermes-furtheragent-mom.service`，保留身份和文件目录以便恢复原码。若需撤销 mom API 配置，先确认没有活动任务，再恢复 `/home/ubuntu/furtheragent-second-profile/` 下的本次环境备份并重启 mom 网关；不要重启主网关或删除身份来完成普通回退。

脱敏报告和原始公网验证日志保存于本地忽略目录 `artifacts/integration/second-profile`；短码仅保存于该目录的私有连接文件并交付用户，不写入 Git。
