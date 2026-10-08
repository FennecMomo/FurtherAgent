# FurtherAgent

FurtherAgent 是基于 **WPF** 的 Windows 远程 Agent 客户端。A 输入 B 显示的授权码，通过常开的服务端连接 B 上的 Codex 桌面会话，继续原项目中的工作。

当前版本：**0.1.1，中继流程验证版**。本版只接入 Codex；DeepseekHarness 后续通过独立适配器接入，不限定 MCP。

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
2. B 点击「开启共享」，复制服务端生成的授权码。
3. A 输入该授权码并连接，选择远端会话。
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

生成 `artifacts/release/0.1.1` 目录与 ZIP，包含客户端、服务端和说明。该包依赖 .NET 10：客户端机器安装 Desktop Runtime，服务端机器安装 ASP.NET Core Runtime。脚本只在本地构建与打包，不提交 Git、不推送、不创建 GitHub Release。

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
