# TLS 建连排查与有限重试（0.4.1）

2026-10-09，使用 Hermes-FA-Files-040 专用接入端、后台 CLI。Host 仍为服务器现有 0.4.0，走本机中继；Windows 控制端走公网域名。无业务会话读写、无服务器配置修改、无桌面操作。

## 实际发现

- 旧 CLI 四次只读探测中三次成功、一次 TLS 建连失败。另用独立 HttpClient 做六次 health GET 均成功，不能据此排除间歇问题。
- 新版错误链定位到 HTTP `pair` 和 WebSocket `control` 的握手阶段：`HttpRequestError.SecureConnectionError`，底层 `IOException: Received an unexpected EOF or 0 bytes from the transport stream.`。
- 未观察到证书过期或域名不匹配报错；EOF 无法单独确定责任在 Cloudflare、本机网络、代理还是其他链路环节，根因未定。
- 新版连续八次 probe 均退出 0 并完成连接后读取。前三个需要恢复的样本分别为：pair 失败一次；pair 失败两次；pair 和 control 各失败一次。其余五次无重试，总共五条重试事件。每次都未附加或写入 Agent 会话。
- 另一次 35 秒持续观察结束时连接仍有效，能再次读取 Agent 信息；不等同于长期稳定性保证。

## 修改

RegistryClient 共用 TLS 握手重试，WPF 和 CLI 同时受益。每步最多三次，间隔 300/600 毫秒，保留原有总超时和取消。WebSocket 失败尝试释放旧 socket 并创建新 socket；HTTP 请求每次重新创建正文。只重试明确的 TLS 握手错误，在发送 WebSocket 授权帧与业务 RPC 之前，不重试普通断线、HTTP 状态错误或结果未知的消息/审批/文件分块。不改变证书校验。

CLI 新增 connection-retry 事件，记录阶段、次数、异常链，授权码保持脱敏。没有输出票据、设备密钥或 API key。

## 解释与边界

当前证据支持暂时用有限重试应对，用户可感知的影响主要是连接偶尔多等几秒；不能保证所有网络故障均可恢复。三次失败或超时依然报错。此次不是长时间稳定性或负载测试。

自动化新增八项检查覆盖恢复、次数上限、非 TLS/HTTP 错误/RPC 不重试、取消和 WS 内层错误分类。旧的票据重放拒绝测试暴露出服务端发送错误后立即 Abort 导致错误帧未送达的情况，断开也属于拒绝；测试接受错误帧或 WebSocket 拒绝断开，仍不接受成功应答或超时，服务端关闭方式未改动。

Release 构建通过（零警告、零错误），修正拒绝测试的传输结果假设后全部 106 项检查通过。WPF 仅构建，不启动窗口；公网恢复验证使用同一 Core 的 CLI。

原始成功探测日志保留于忽略目录 artifacts/integration/files-live/tls-041-probes.jsonl。不需要更新服务器 Host 或中继，更新 Windows WPF/CLI 即可获得此次修复。
