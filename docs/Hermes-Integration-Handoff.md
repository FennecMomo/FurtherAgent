# 给服务器 Hermes 的完整指令：独立 FurtherAgent 联调通道与六项接口核对

用户已授权为 FurtherAgent 建立专用联调通道。Codex 将使用后台 FurtherAgent.Cli，不操作用户桌面。请完成以下准备并返回结果，不需要再次询问是否执行这些步骤。

## 目标和范围

- 保留现有 FurtherAgent 中继、业务接入端和 Hermes 网关运行，不停止、不覆盖它们，不使用历史 PID 直接操作进程。
- 在新目录中启动另一个 FurtherAgent.Host 0.3.0，名字固定为 `Hermes-FA-Integration`，接同一个中继和当前 default profile API。它应取得独立授权码，不占用业务接入端的单控制者连接。
- 本轮只新增测试接入端进程、它自己的目录和日志，并只读核对安装版源码/API。不改 Hermes 源码、config.yaml、.env、权限策略、模型路由、Cloudflare Tunnel、DNS、监听端口或 systemd。
- 不向现有会话发送消息，不执行 run，不归档/删除会话，不上传/下载现有用户附件。测试新会话及后续 run 由 Codex CLI 在拿到专用授权码后创建。
- 同 API/profile 不代表安全沙箱隔离；请明确标注只是接入端和会话隔离，不宣称操作系统权限隔离。

## 一、检查包与独立启动

我会把此包一起发给你：`FurtherAgent-Hermes-0.3.0-linux-x64.tar.gz`。
预期 SHA256：
`09F7B4291B5C4E3F43E1708E610157448B7653DD9CE4A1F3BF90FDCA6C879F13`

1. 找到收到的实际文件路径，先校验哈希，不匹配就停止部署并报告。不要用 0.2.0 包代替。
2. 解压到 `~/furtheragent-integration/host-0.3.0-20261009` 这样的全新目录；若已存在，先检查是否就是本次测试实例，不能覆盖或重复启动。目录权限 700，日志权限 600，给本包 FurtherAgent.Host 执行权限。包为 linux-x64 自包含，无需安装 .NET。
3. 先确认 `https://furtheragent.fennecmomo.top/health` 可达。若不通，只报告；不要自行重启中继或修改 Tunnel。
4. 使用以下子进程环境启动独立 Host：
   - `FURTHERAGENT_SERVER=https://furtheragent.fennecmomo.top`
   - `FURTHERAGENT_NAME=Hermes-FA-Integration`
   - `HERMES_API_URL=http://127.0.0.1:8642`
   - `HERMES_API_KEY`：优先用当前实际生效凭据。如读取 `~/.hermes/.env`，按最后一次有效赋值取 API_SERVER_KEY，处理引号，不执行整份 .env，不修改重复行。只通过子进程环境传入，不放命令行、不打印、不写入报告。
5. 可先执行一次 `--probe`，确认正常再启动共享模式。启动方式采用你可以保留的独立后台进程（例如 Python subprocess.Popen，start_new_session=True，stdout/stderr 指向权限 600 的测试日志）；记录真实 Host PID 和启动方式，不安装 systemd。若其生命周期仍依附当前 Hermes 终端，明确说明。
6. 确认它注册成功并保持运行。不要转发完整 host.log（含授权码）；只把此专用授权码单独回在本次私聊，供 Codex CLI 使用。

## 二、只读核对“已安装版本”的接口契约

以当前服务器文件和 loopback API 为准，不能用网上 main 源码当成本机版本。返回相关函数名、实际源码文件路径、行号和必要的短代码片段；不要返回 .env、密钥、用户消息或附件内容。标明“源码确认 / 实测 / 未确认”。

A. 图片输入与输出
- `/v1/runs` 的 input 到 agent.run 的完整转换：是否支持带 text + image_url/input_image 的 content 数组？是否保留到模型输入与历史，而非转为字符串？只接受字符串的步骤在哪里？
- `/api/sessions/{id}/messages` 的 inline_images 处理：图片是 data URL、下载 URL、文件路径还是占位？输出图片/artifact 的具体结构是什么？
- 当前默认模型是否声明支持图片输入？只读核对，勿切换模型或触发推理。

B. 文件与 artifact
- `/v1/artifacts/upload` 与 `/v1/artifacts/download/{id}` 是否受 browser_control 开关限制？当前开关实际状态是什么？只读读取该配置项，不回显整份 config。
- 上传 Content-Type/必要头/大小限制/响应字段/TTL/作用域；下载是否一次性消费。
- 上传 receipt 能否作为 `/v1/runs` 输入或工具可读文件？有没有明确的转换方法？请不要仅因路由存在就判断通用附件可用。
- 是否有独立的会话附件、工作区文件或产物读写接口？没有就说明。

C. 权限切换
- `/v1/runs` 和 session 模型/设置接口是否接受并实际执行 sandbox、approval_policy、toolsets 或权限 profile 等逐会话/逐运行字段？给出白名单与使用点。
- 能否真正实现只读/工作区写入/完全访问，并且不改全局网关配置、不影响微信/其他会话？若只有工具审批选项，不等同权限模式。

D. 思考/工具详情
- 安装版 SSE 事件的完整字段：message.delta、reasoning 类、tool.started/tool.completed/工具输出、approval、run 完成/失败。
- 是否有工具调用 ID、参数、输出、耗时、退出码、文件 diff；哪些被截断或隐藏；历史接口是否保存这些信息。
- 不制造测试 run，本轮仅源码与只读能力核对，真实事件稍后由 CLI 测试会话采集。

E. 归档与恢复
- PATCH /api/sessions/{id} 接受字段的实际白名单，是否包含 archived。
- 如果支持：请求/返回、归档列表过滤参数、unarchive 方法，是否只是标记而不是删除；如果不支持，直接说明。
- 不对任何真实会话执行 PATCH/DELETE 来验证。

Markdown 是 Windows 客户端工作，不需要服务器修改。

## 三、返回格式

1. 包哈希是否匹配，测试 Host 的实际路径/PID/设备名，公网中继状态。
2. **专用授权码（仅此私聊）**；不要把授权码写进可转发的调查报告。
3. probe 成功与注册成功的脱敏证据；测试进程能保持多久、何种行为会回收它。
4. A–E 的接口契约报告，特别列出未知与不支持项。
5. 副作用清单：只新增的目录/日志/测试 Host；原中继、业务 Host、网关与配置是否保持不变。

如果某项受限，完成其他独立项并报告具体阻碍，不要通过改全局配置绕过。让测试 Host 保持运行，等待 FurtherAgent.Cli 配对。
