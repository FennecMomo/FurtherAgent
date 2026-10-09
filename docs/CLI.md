# FurtherAgent 后台 CLI

独立命令行控制端，复用 Core 中继协议，不启动 WPF，不读取或写入 WPF 配置。Windows / Linux 均可构建。服务端 B 同一授权码仍只允许一个控制者：独立 CLI 不会绕过此限制，持续联调应使用单独启动的测试 Host 和专用授权码。

## 构建

```powershell
dotnet build FurtherAgent.slnx
./scripts/Publish-Cli.ps1               # Windows 自包含
./scripts/Publish-Cli.ps1 -Runtime linux-x64
```

## 连接与命令

0.5.0 增加 `archive --session ID`、`unarchive --session ID`，要求远端声明 CanArchive。通过 Hermes 原生 PATCH 修改 archived 并 GET 读回验证，不删除会话。当前 Hermes API 无已归档列表入口，恢复必须保留会话 ID。新建空会话、模型配置和文件命令不变。消息记录新增可选 detail 字段，包含后端实际提供的工具/过程详情。

0.4.1 在共享 RegistryClient 内增加 TLS 握手有限重试：每个 HTTP/WS 建连步骤最多 3 次，重试等待 300/600 毫秒，仍受原有超时与取消约束。仅匹配 SecureConnectionError；不重试 HTTP 状态错误、普通断线、RPC 或结果未知的业务请求，不关闭证书校验。CLI 输出 connection-retry 事件，包含阶段、次数和错误链。此机制区别于已建立连接断开后的自动重连；CLI 仍不会自动重连并重放命令。

使用 `FURTHERAGENT_CODE` 环境变量，或 `--code-file` / `FURTHERAGENT_CODE_FILE` 指向仅含授权码的本机文件。授权码不接受直接命令行值，不输出到 JSON。服务端通过 `--server`、`FURTHERAGENT_SERVER` 指定，默认 https://furtheragent.fennecmomo.top。业务正文与历史会出现在 read/watch 的输出中，应按私有文件保存。

```powershell
# 文件在你自己的受限目录中准备，不要提交到 Git。
$env:FURTHERAGENT_CODE_FILE = '你的授权码文件路径'
$env:FURTHERAGENT_SERVER = 'https://furtheragent.fennecmomo.top'
$cli = 'E:/etc/FurtherAgent/artifacts/release/FurtherAgent-Cli-0.4.0-win-x64/FurtherAgent.Cli.exe'
& $cli probe --seconds 35
& $cli agents
& $cli sessions
& $cli create --request-id 11111111111111111111111111111111 --title 'FurtherAgent-联调专用'
& $cli read --session '上一步返回的会话ID'
& $cli send --session '测试会话ID' --text '只回复 FurtherAgent 联调成功，不要调用工具。' --seconds 15
& $cli watch --session '测试会话ID' --seconds 30
& $cli settings --session '测试会话ID'
& $cli configure --session '测试会话ID' --model '远端实际支持的模型' --provider '提供商' --mode default
& $cli stop --session '测试会话ID'
& $cli respond --session '测试会话ID' --request-id '审批ID' --decision decline
# Codex 问题回答可用 --response-file answer.json，内容是对应 response 对象。
```

新建不同会话应换一个 GUID（32 位十六进制）；同一次创建结果未知时沿用原 ID。省略 ID 时 CLI 在请求前输出 create-request 事件，重试必须使用该 ID。上述固定 ID 仅作示例。

每条命令建立一次连接，结束即关闭；没有自动重连或修改请求重放。`probe` 只读取适配器和会话数量，不 attach 业务会话；`watch` 只 attach 并观察，不发消息。`send` 的 accepted 仅表示接入端接受操作，不保证 Agent 已执行完成。观察窗口结束不会停止 Agent，后续使用 watch 继续观察；Hermes Host 不应因此重启。观察中连接丢失以退出码 1 返回。Ctrl+C 退出 130，其余错误 1，成功 0。

新建与模型设置要求升级后的 Hermes Host；旧 Host 缺少能力标志时 create 明确报不支持，不会尝试发送未知写入。所有命令输出 JSON Lines，诊断错误写 stderr。文本参数在连接前校验，错误参数不会导致提交；同一 Hermes Host 进程保留未知提交的幂等键，可显式原样重试。

0.4.0 支持 upload/files/download，以及 send --file / --file-id。双向文件校验、边界、示例见 File-Transfer.md。图片预览、视觉输入、权限切换、归档仍需另行接入。
