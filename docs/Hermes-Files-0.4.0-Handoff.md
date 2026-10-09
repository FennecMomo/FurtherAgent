# 转交 Hermes：启动 FurtherAgent 0.4.0 专用文件联调接入端

用户已授权启用专用联调接入端。Codex 将全程使用后台 CLI 验证文件功能，不操作 Windows 桌面。

请执行下面的部署准备，不用更新中继服务，也不要停止业务接入端或重启 Hermes 网关。

## 包

- 文件：FurtherAgent-Hermes-0.4.0-linux-x64.tar.gz
- SHA256：184F6682E3B9F66AC7D338B9E71845006EBA3C42B4D592269A42398FD62F931D
- linux-x64 自包含，无需安装 .NET。

先核验实际收到的包，哈希不一致就停止并报告。不要以旧包替代。解压到全新的 `~/furtheragent-integration/host-0.4.0`，目录已有内容时另取新目录，不覆盖运行中的文件。给 FurtherAgent.Host 执行权限。

## 独立启动

1. 确认 `https://furtheragent.fennecmomo.top/health` 成功；已有中继 0.2.0 可以直接转发文件 RPC，无需升级、重启或修改 Cloudflare Tunnel。
2. 准备独立文件目录 `~/furtheragent-integration/files-0.4.0`，权限 700。它只是文件工作区隔离，不是 Agent 的系统权限沙箱。
3. 子进程环境：
   - FURTHERAGENT_SERVER=https://furtheragent.fennecmomo.top
   - FURTHERAGENT_NAME=Hermes-FA-Files-040
   - HERMES_API_URL=http://127.0.0.1:8642
   - FURTHERAGENT_FILES_ROOT=上面独立文件目录的实际绝对路径
   - HERMES_API_KEY=当前真正生效的 key，只经子进程环境传入。
4. 如果取自 `~/.hermes/.env`，取最后一次有效的 API_SERVER_KEY 赋值、正确处理引号；不要 source/执行整个 .env，不改重复行，不把 key 写进命令行或报告。
5. 可先运行 --probe 确认鉴权正常，再启动一个独立 Host 后台进程。建议用 subprocess.Popen(start_new_session=True) 或你可维护的后台终端；私有日志目录 700、日志 600。记录真实 PID，不能仅记录 shell/tee PID。不要安装 systemd。
6. 保留业务 Host、旧联调 Host、Hermes 网关、模型配置和现有权限不变。新进程名字是 Hermes-FA-Files-040，应获得新授权码。若该名字已存在，先核实它的路径、版本和进程，不重复启动、不擅自停止未知进程。
7. 新 Host 与 Hermes 必须同机、同一个能读写这些文件的用户。无需启用 browser_control，也不调用 Hermes artifact API。

## 回传

- 包哈希是否匹配，真实启动路径/PID、设备名、文件根目录。
- `--probe` 和中继注册是否成功。
- **专用授权码单独回到本次私聊**，不要写进可转发报告，也不要发送完整 host.log。
- 后台进程生命周期；当前回复结束后是否继续运行。
- 确认原业务进程、配置、Tunnel 和端口没有改动。

保持专用 Host 运行，等待 Codex CLI 连接。服务器本轮无需主动执行 Agent run 或修改任何已有会话。

## 后续由 Codex CLI 执行的验收

在新建的 FurtherAgent 文件联调会话中：上传无敏感信息的 sample.txt，先下载回本机对比 SHA256；然后请 Hermes 读取该附件，生成一个小型 result.txt 到提示中指定的 outbox；列出、下载该结果并核对内容。这些测试仅用新会话和专用文件目录，不触碰微信等业务会话。

文件根目录下会按 session ID 的 SHA256 建立 inbox/outbox。上传文件会有唯一前缀；完整文件保留供重连后读取，未完成上传只保留暂存。每文件上限 100 MiB。输出只能通过会话目录文件 ID 下载，不能用任意服务器路径下载。
