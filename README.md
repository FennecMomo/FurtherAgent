# FurtherAgent

FurtherAgent 是基于 **WPF** 的 Windows 远程 Agent 客户端。A 输入 B 显示的授权码，通过常开的服务端连接 B 上的 Codex 桌面会话，继续原项目中的工作。

当前版本：**0.6.5，输入区布局修正**。附件图标工具栏位于输入框外侧正上方，14 像素图标、24 像素按钮；输入文字左上对齐，周围保留 4 像素内边距。发布后通过本机控制通道退出旧客户端、后台启动最新构建，并核对实际进程。启动自动连接支持有限重试；`scripts/Client-Control.ps1 -Action Status` 可查询版本、就绪和连接状态，不唤起窗口。有草稿或传输时暂缓切换，避免丢失内容。成功连接后将服务端和设备保存到 `%LOCALAPPDATA%\FurtherAgent`，授权码采用 Windows 当前用户 DPAPI 加密，更新构建目录不影响记录；启动自动重连，主动断开后暂停自动连接，设置中可忘记连接。用户和 AI 消息分色，工具与思考默认折叠；用户气泡隐藏自动文件工作区说明。关闭窗口收到托盘，双击托盘图标或桌面快捷方式恢复，托盘菜单可彻底退出。重启、更新后保留接入端身份，详见 [固定配对码](docs/Stable-Pairing.md)。新增原生 WPF Markdown、图片粘贴与文件预览、可折叠工具详情、Hermes 原生归档/按 ID 恢复。保留共享文件传输和 TLS 有限重试。当前能力及实机验收见 [0.5.0 报告](docs/Features-0.5.0-Validation.md)。DeepseekHarness 后续通过独立适配器接入，不限定 MCP。

Hermes 的部署、只读探测、授权码连接和能力边界见 [Hermes 接入与联调](docs/Hermes.md)。Linux 接入端提供自包含包，无需在服务器安装 .NET 10。

服务器第一阶段已反馈：Linux 启动、鉴权后的能力探测和 25 条会话列表读取成功，见 [阶段验证记录](docs/Hermes-Validation.md)。0.4.0 后台 CLI 已在独立测试会话完成真实文件任务，见 [文件实机联调记录](docs/File-Transfer-Live-Validation.md)；停止、审批和其他入口运行接管不属于本次通过范围。
Linux 中继另用 `scripts/Publish-Relay.ps1` 生成自包含包，临时启动及公网接入前提见 [中继部署说明](docs/Relay.md)。

## 0.4.0 双向文件

WPF 的「添加文件」「会话文件 / 下载」与 CLI 的 upload/files/download/send --file 共用 Core 文件传输服务。文件经中继分块传送，上传与下载均校验 SHA256；Agent 在专用会话输出目录交付产物。需更新接入端至 0.4.0，现有中继不必重启。详见 [文件传输说明](docs/File-Transfer.md)。

## 0.3.0 使用入口

- 主界面：左侧搜索、刷新及「＋ 新建会话」，右侧消息、确认和输入框；Ctrl+Enter 发送，运行中发送为追加指令。
- 「设置」：中继地址、本机 Codex 共享、授权码和断开连接。地址保存在 `%LocalAppData%/FurtherAgent/settings.json`；不保存授权码。环境变量 `FURTHERAGENT_SERVER` 在启动时优先于保存值。
- 「连接设备」：输入授权码，弹窗会显示当前中继地址。默认预置 `https://furtheragent.fennecmomo.top`。
- 「模型与模式」：在打开会话后使用，修改成功后供后续轮次使用。实际执行权限仍由 Agent 管理。

| 功能 | Hermes 接入端 0.3.0 | Codex 桌面适配 |
| --- | --- | --- |
| 新建空会话 | 支持；固定请求 ID 重试避免重复创建 | 尚不支持；需在远端桌面创建后刷新 |
| 切换模型 | 原生模型列表、提供商和会话模型锁定 | 填写远端可用的模型 ID，实验性设置接口 |
| Build / Plan | 仅 Build；原生 HTTP 接口未确认 Plan 能力 | 原生 default / plan 协作模式，实验性 |
| 会话草稿 | 窗口内切换会话时保留 | 同左 |
| 复制对话、停止、审批 | 支持；控制范围限本接入端发起的运行 | 支持已有适配类型 |

旧 0.2.0 接入端仍可读历史、发送及停止，但不会声明新功能。升级 Windows 客户端和 Hermes 接入端后才会启用新建与模型设置；中继协议没有变化，已有 0.2.0 中继可继续运行。**不要仅替换正在运行的 Linux 二进制文件**；等待接入端运行结束后，停止该接入端、解压新目录再启动，并使用新授权码连接。详见 [0.3.0 验收说明](docs/Session-UI-0.3.0.md)。

## 后台 CLI 联调

新增独立 `FurtherAgent.Cli`，在后台通过中继配对、读取/新建会话、发送、观察、停止、审批和修改模型。不启动桌面窗口，不使用 WPF 设置。使用 `scripts/Publish-Cli.ps1` 生成自包含包，详见 [CLI 使用说明](docs/CLI.md)。

六项待扩展功能的难度和接口证据见 [可行性排查](docs/Feature-Feasibility.md)；无 SSH 时可将 [独立接入端准备指令](docs/Hermes-Integration-Handoff.md) 转给服务器 Hermes。

## 技术方向

- 运行平台：Windows
- 界面框架：WPF
- 开发语言：C#
- .NET 版本：10
- 服务端：ASP.NET Core，可部署在 Windows 或 Linux
- 网络：双方主动建立 WebSocket 连接，经服务端全程转发；公网使用 HTTPS/WSS

## 连接方式

```text
A 的 FurtherAgent ──出站连接── 服务端 ──出站连接── B 的 FurtherAgent ──本机 IPC── Codex 桌面
```

上图两端均主动连接服务端。客户端不监听入站端口，不需要填写 A/B 的 IP、配置端口映射或提供公网地址。授权码只用于定位与授权，实际消息由服务端持续中继。

网线、Wi-Fi、路由器、交换机、运营商 NAT 下都可以使用，前提是双方能访问同一服务端且网络允许 HTTPS/WSS。完全离线、企业网络拦截或服务器不可达时无法连接。

- 15 秒 WebSocket 心跳，15 秒响应超时；失联后按 1、2、4、8、15 秒间隔重试。
- A 重连后恢复选中的会话快照；发送消息、停止、审批等操作不自动重发，避免重复执行。
- B 短时断线后可用原授权码恢复。离线超过约两分钟或服务端重启后会重新生成授权码，A 需要输入新码。
- 第一版每台 B 同时允许一个 A 控制；停止共享会断开远程端并撤销授权码。
- 公网每一段连接使用 TLS，服务端可以访问转发内容；本版尚未实现端到端加密。

## 开发与本机运行

安装 .NET 10 SDK，然后在仓库根目录执行：

```powershell
dotnet build FurtherAgent.slnx
dotnet run --project src/FurtherAgent.Server -- --urls http://127.0.0.1:47320
```

另开终端启动客户端（可启动两次，分别模拟 A、B）：

```powershell
dotnet run --project src/FurtherAgent.Client
```

本机测试时，两端服务地址均为 `http://127.0.0.1:47320`：

1. 在 B 打开 Codex 桌面，使用与 FurtherAgent 相同的 Windows 用户。
2. B 进入「设置」，点击「开启共享」，复制服务端生成的授权码。
3. A 点击「连接设备」，输入该授权码并连接，选择远端会话。
4. 若会话未连接成功，先在 B 的 Codex 桌面打开该会话，再在 A 重新选择。
5. A 可以查看输出、发送消息、停止任务、回答支持的确认。执行中的消息作为补充指令。

`FURTHERAGENT_SERVER` 环境变量可预置服务端地址。界面只保留服务端部署配置，不包含客户端 IP 或端口输入。

## 公网部署

将服务端部署在常开且双方可达的服务器上。通过受信任证书提供 HTTPS/WSS，通常使用 443 端口；反向代理需允许 WebSocket Upgrade，空闲超时应大于心跳周期（建议至少 75 秒）。后端 HTTP 只绑定本机并由 HTTPS 反向代理转发。

客户端服务地址填写同一个 `https://你的服务域名`。除本机回环测试外，客户端拒绝明文 HTTP。服务器证书由系统正常验证，不跳过证书错误。服务端地址是部署配置，后续可以预置固定地址。

第一版授权机制是持有码访问：只有 B 持有设备管理密钥，A 通过授权码换取 60 秒有效的一次性票据；密钥和票据不放在 URL 中。授权码与在线记录目前保存在服务端内存，重启会失效。中继增加服务端带宽消耗，公网容量与延迟需在实际部署后测量。

## Codex 适配边界

实验性适配针对本机检查过的 Codex Desktop `26.1002.7124.0` / CLI `0.162.0-alpha.2`。通过当前用户的 `codex-ipc` 命名管道连接桌面会话协同接口（状态流协议版本 11），不另开一个写入同一聊天的 CLI 实例，也不改写历史文件或认证配置。

已实现会话列表、状态快照/增量更新、发送/补充消息、停止、命令与文件审批、普通问题回答。其他交互会提示在 B 桌面处理。客户端显示最近 250 条可呈现记录，单条长输出有截断。桌面升级可能改变内部协议；不兼容时需更新适配器。

真实 Codex 会话的只读列表、快照与更新可通过下面的显式测试验证。真实发送、停止和审批仍需要专用会话验收；测试适配器的成功不能替代这项验收。

## 测试与发布

```powershell
dotnet build FurtherAgent.slnx
dotnet tests/FurtherAgent.SmokeTests/bin/Debug/net10.0/FurtherAgent.SmokeTests.dll
# 可选：指定本机桌面正在打开的会话，执行只读集成验证。
dotnet tests/FurtherAgent.SmokeTests/bin/Debug/net10.0/FurtherAgent.SmokeTests.dll --codex-thread <会话ID>
# 加 --watch 等待该会话出现实时更新（35 秒超时）。
```

测试会启动并清理自己的回环服务端，验证中继双向传输、唯一授权码、错误授权、票据防重放、单控制端限制、A 断线自动重连且不重发指令、B 在服务端重启后自动注册、停止共享撤销授权，以及可选的真实 Codex 只读连接。公网跨两台电脑与 GUI 人工验收需另行执行。

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

生成 `artifacts/release/<版本号>` 目录与 ZIP，包含客户端、服务端和说明。该包依赖 .NET 10：客户端机器安装 Desktop Runtime，服务端机器安装 ASP.NET Core Runtime。脚本只在本地构建与打包，不提交 Git、不推送、不创建 GitHub Release。

## 版本规则

版本号采用 `系统.模块.修改` 三段数字格式，初始版本为 `0.0.0`。

| 位次 | 含义 | 更新场景 |
| --- | --- | --- |
| 第一位 | 系统 | 系统级整体升级或重大架构调整 |
| 第二位 | 模块 | 新增模块或模块级功能更新 |
| 第三位 | 修改 | 问题修复、细节调整和小范围改进 |

每次更新按本次变更涉及的最高层级递增对应位；高位递增时，后续低位归零。例如：

- 修改更新：`0.0.0` → `0.0.1`
- 模块更新：`0.0.1` → `0.1.0`
- 系统更新：`0.1.0` → `1.0.0`

## 仓库

[FennecMomo/FurtherAgent](https://github.com/FennecMomo/FurtherAgent)

## 许可证

本项目采用 [MIT License](LICENSE)。

Copyright (c) 2026 FennecMomo

## 桌面启动入口

每次执行 `scripts/Publish.ps1` 完整发布成功后，自动更新桌面 `FurtherAgent.lnk`。快捷方式调用固定的 `scripts/Start-LatestClient.ps1`，每次读取 `artifacts/current/client.json`，打开最近一次成功发布的客户端。

发布时将客户端复制到 `artifacts/current/builds/<版本-时间-唯一标识>`，逐文件校验后原子替换当前版本指针。旧进程继续使用旧快照，下次从桌面打开即使用新版本。构建失败不会切换目标，不自动关闭正在使用的客户端，也不自动删除旧快照。普通 `dotnet build` 只做开发编译，不更新桌面入口；交付桌面版本统一运行发布脚本。

快捷方式依赖本仓库路径；移动仓库后重新执行发布脚本即可重新生成。`artifacts` 和桌面快捷方式不提交 Git。

应用图标源文件为 `src/FurtherAgent.Client/Assets/FurtherAgent.svg`。PNG 和多尺寸 ICO 已提交，正常构建无需图像工具；修改矢量后可运行 `python scripts/Build-Icon.py`（需要 Pillow、CairoSVG）重建资源。图标用于 EXE、窗口、界面品牌和桌面快捷方式。
