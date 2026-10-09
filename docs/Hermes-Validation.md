# Hermes 0.2.0 验证记录

## 2026-10-09：服务器第一阶段反馈

证据来源：用户提供的第一阶段探测报告及 `probe.masked.txt`。以下服务器结果是远端返回的实测证据，本地 Codex 没有登录服务器重复执行。

- 接入端包 SHA256：`6BB7F360B6866CDC6E5229C3240F85C765EE826755D191A21B8F353219AFC28F`，远端核验一致。
- Linux x64 自包含程序能在目标 Ubuntu 启动，不需要另装 .NET 10。
- 首次凭据错误返回 HTTP 401 / exit 1；传入有效凭据后 `--probe` 返回 exit 0。
- capabilities 与当前接入端依赖字段匹配；会话列表返回 25 条，远端独立 HTTP 查询也是 25 条。
- 无中继服务部署；SSE 实际事件、发送/追加/停止/审批及跨机器重连尚未验证。

### 凭据问题的归属纠正

报告发现 Hermes `.env` 中存在两条 API_SERVER_KEY，第一次外部提取使用了失效值。
当前 `src/FurtherAgent.Host/Program.cs` **不解析 Hermes `.env`，也不存在“取第一个匹配”的逻辑**。
它只读取 HERMES_API_KEY 环境变量，或 HERMES_API_KEY_FILE 指向的纯 key 文件。
因此修复应在提供凭据的提取步骤，不需要修改接入端或为了此次联调重载 Hermes 网关。
后一次探测使用的值已经被运行中的网关接受；清理重复配置可作为独立维护事项，不是继续联调的前置条件。

### 下一步

1. 验证独立 Linux 中继包的回环启动与 /health。
2. 确定公网 HTTPS 域名和代理/隧道配置，验证 WebSocket 配对。
3. 在专用测试会话中验证真实 run 行为，不对现有微信工作发送测试指令。

不要把历史读取成功表述为完整远程控制已跑通，也不要用新 main 的 SSE 恢复行为替代当地 0.21.0 契约。

## 2026-10-09：中继启动与公网回源反馈

证据来源：用户提供的《FurtherAgent-中继0.2.0-启动与回源检查报告.md》，本地未重复执行服务器测试。

- 中继包 SHA256 `E8F9BA4F64D6837847A5FFED4A4B405FB48F5BCB17A2398F10C0E785B33B1976` 核验一致。
- Ubuntu 上临时启动成功，只监听 `127.0.0.1:47320`；本机 /health 返回 0.2.0 / relay / ok。
- cloudflared 位于宿主机；实际路由来自 Cloudflare 仪表盘下发配置，本地旧 config.yml 不作为配置真源。
- 临时运行期间，`https://furtheragent.fennecmomo.top/health` 返回 HTTP 200，中继日志关联到该请求。普通 HTTPS 回源已验证。
- 报告结束时临时中继已停止，47320 无监听。此证据不代表服务现在仍在线。
- WebSocket /host、/join、/control 配对和真实 Hermes 运行控制尚未验证。

下一步先临时启动中继与接入端，保持在线供 Windows A 用授权码配对，只读取会话并验证重连；随后单独安排测试会话写入及常驻安装。
报告另发现现有 api 子域名也回源 Hermes 8642；该路由用途未知，本任务不自动删除或更改它，也不清理/轮换现有凭据。

## 2026-10-09：Windows 侧真实公网只读配对

本轮由本地 Codex 使用用户提供的授权码直接测试，未调用 send、interrupt、respond 或创建 Hermes run。

- 首次 HTTP 诊断：/health 200；随后 /pair 的 TLS 建连失败，内部错误为 unexpected EOF。后续默认网络路径及禁用代理的对照请求都返回 /health 200、/pair 200，尚不能认定具体网络根因。
- 使用 FurtherAgent.Core 真实连接：/pair 与 /control 握手成功，B 的 /join 已完成；agents 返回 hermes，会话列表 25 条，首个会话历史读取成功（39 条显示项，不记录内容）。
- 保持连接 35 秒后 IsConnected 为 true，再次调用 agents 成功。随后诊断客户端主动断开。
- 使用现有发布目录 `artifacts/release/0.2.0/Client/FurtherAgent.Client.exe`，经 Windows 界面输入公网中继地址与授权码，点击连接成功。界面显示 Hermes-default，列出会话，选择会话后历史显示成功。
- WPF 测试结束后保留该已连接窗口供用户使用。此记录不是持续在线监控；单控制端限制仍适用。
- 未复现用户的持续连接失败，未对连接代码做推测性修复。用户原始错误尚待提供。真实消息增量、追加、停止、审批及故障重连仍未验收。
